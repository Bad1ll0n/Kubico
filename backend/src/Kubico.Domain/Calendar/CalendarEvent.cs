using Kubico.Domain.Households;

namespace Kubico.Domain.Calendar;

/// <summary>
/// Um evento do calendário da casa ("Natação da Rita", "Jantar em casa dos avós"). Ou tem hora
/// (instantes UTC) ou ocupa dias inteiros (datas no fuso da casa), e pode repetir-se segundo uma RRULE.
/// </summary>
public class CalendarEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Household Household { get; set; } = null!;

    public required string Title { get; set; }
    public string? Notes { get; set; }
    public string? Location { get; set; }

    /// <summary>Início e fim de um evento com hora; null nos de dia inteiro.</summary>
    public DateTimeOffset? StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }

    /// <summary>Primeiro e último dia (inclusive) de um evento de dia inteiro; null nos que têm hora.</summary>
    public DateOnly? StartDate { get; private set; }
    public DateOnly? EndDate { get; private set; }

    /// <summary>RRULE canónica, sem o prefixo "RRULE:" (ex.: "FREQ=WEEKLY;BYDAY=TU,TH"). Null = não se repete.</summary>
    public string? Recurrence { get; set; }

    /// <summary>
    /// Dias (locais) das ocorrências retiradas da série, apagadas ou substituídas por uma exceção (o EXDATE do iCalendar).
    /// </summary>
    public List<DateOnly> ExcludedDates { get; set; } = [];

    /// <summary>Numa exceção ("só este evento"), a série de que substitui uma ocorrência. Null nos eventos normais.</summary>
    public Guid? SeriesId { get; set; }

    /// <summary>Numa exceção, o dia da ocorrência original que substitui.</summary>
    public DateOnly? OriginalDate { get; set; }

    /// <summary>Membros envolvidos. A cor do evento e a deteção de conflitos vêm daqui.</summary>
    public List<Guid> ParticipantUserIds { get; set; } = [];

    /// <summary>Boleias: quem leva (no início do evento) e quem vai buscar (no fim).</summary>
    public Guid? DropOffUserId { get; set; }
    public Guid? PickUpUserId { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsAllDay => StartDate is not null;

    public bool IsRecurring => Recurrence is not null;

    /// <summary>Dia (local) da primeira ocorrência.</summary>
    public DateOnly FirstDate(TimeZoneInfo zone) =>
        StartDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(StartsAt!.Value, zone).DateTime);

    public void SetTimed(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        StartsAt = startsAt.ToUniversalTime();
        EndsAt = endsAt.ToUniversalTime();
        StartDate = null;
        EndDate = null;
    }

    public void SetAllDay(DateOnly startDate, DateOnly endDate)
    {
        StartDate = startDate;
        EndDate = endDate;
        StartsAt = null;
        EndsAt = null;
    }

    /// <summary>
    /// Numa repetição semanal com dias escolhidos, a primeira ocorrência tem de ser num desses dias (RFC 5545: um início
    /// fora da regra dá resultados diferentes em cada calendário). Se não for, o início passa para o primeiro dia
    /// escolhido a seguir, à mesma hora local (como no Google Calendar). Devolve true se mudou.
    /// </summary>
    public bool AlignStartToRule(TimeZoneInfo zone)
    {
        if (!RecurrenceRule.TryParse(Recurrence, out var rule) || rule.Frequency != RecurrenceFrequency.Weekly
            || rule.ByDay is not { Count: > 0 } days)
        {
            return false;
        }

        var first = FirstDate(zone);
        var shift = Enumerable.Range(0, 7).First(d => days.Contains(first.AddDays(d).DayOfWeek));
        if (shift == 0)
        {
            return false;
        }

        if (StartDate is { } start)
        {
            SetAllDay(start.AddDays(shift), EndDate!.Value.AddDays(shift));
        }
        else
        {
            var duration = EndsAt!.Value - StartsAt!.Value;
            var startsAt = ZonedTime.ToInstant(TimeZoneInfo.ConvertTime(StartsAt.Value, zone).DateTime.AddDays(shift), zone);
            SetTimed(startsAt, startsAt + duration);
        }

        return true;
    }

    /// <summary>Alguém saiu da casa: deixa de participar e de levar ou ir buscar. Devolve true se o evento mudou.</summary>
    public bool RemoveMember(Guid userId, DateTimeOffset now)
    {
        if (!ParticipantUserIds.Contains(userId) && DropOffUserId != userId && PickUpUserId != userId)
        {
            return false;
        }

        ParticipantUserIds = [.. ParticipantUserIds.Where(id => id != userId)];
        DropOffUserId = DropOffUserId == userId ? null : DropOffUserId;
        PickUpUserId = PickUpUserId == userId ? null : PickUpUserId;
        UpdatedAt = now;
        return true;
    }

    /// <summary>
    /// Ocorrências que se cruzam com [<paramref name="from"/>, <paramref name="to"/>). As repetições mantêm a hora
    /// local da casa: um evento às 18:00 continua às 18:00 depois da mudança de hora.
    /// </summary>
    public IEnumerable<EventOccurrence> Occurrences(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var firstDate = FirstDate(zone);

        // Os dias que acabam de certeza antes de "from" saltam-se só pela data, sem converter fusos (uma série diária
        // com anos tinha de calcular a hora local de cada dia que já passou).
        var dates = RecurrenceRule.TryParse(Recurrence, out var rule)
            ? rule.Dates(firstDate, FirstDateEndingAfter(from, zone))
            : [firstDate];
        var excluded = ExcludedDates.Count > 8 ? ExcludedDates.ToHashSet() : null;
        var duration = EndsAt - StartsAt;
        var localTime = StartsAt is { } startsAt ? TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(startsAt, zone).DateTime) : default;
        foreach (var date in dates)
        {
            var occurrence = OccurrenceOn(date, date == firstDate, zone, localTime, duration);
            if (occurrence.Start >= to)
            {
                yield break;
            }

            if (occurrence.End > from && !(excluded?.Contains(date) ?? ExcludedDates.Contains(date)))
            {
                yield return occurrence;
            }
        }
    }

    /// <summary>
    /// Um dia (local) tal que as ocorrências que começam antes dele acabam todas antes de <paramref name="from"/>:
    /// o dia de <paramref name="from"/> menos a duração (em dias de 23 horas, o mais curto com a mudança de hora) e
    /// dois dias de margem.
    /// </summary>
    private DateOnly FirstDateEndingAfter(DateTimeOffset from, TimeZoneInfo zone)
    {
        var fromDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(from, zone).DateTime);
        var days = SpanDays() + 2;
        return fromDate.DayNumber > days ? DateOnly.FromDayNumber(fromDate.DayNumber - days) : DateOnly.MinValue;
    }

    /// <summary>Quantos dias (no máximo) uma ocorrência ocupa além do dia em que começa.</summary>
    private int SpanDays() => StartDate is { } startDate
        ? Math.Max(0, EndDate!.Value.DayNumber - startDate.DayNumber)
        : (int)Math.Min(int.MaxValue / 2, Math.Ceiling(Math.Max(0, (EndsAt!.Value - StartsAt!.Value).TotalHours) / 23));

    /// <summary>
    /// Último dia (com margem) em que a série ainda pode ocupar o calendário: o início da última ocorrência (o UNTIL
    /// ou a última de um COUNT), mais a duração e três dias. <see cref="DateOnly.MaxValue"/> numa série sem fim; null
    /// nos eventos que não se repetem. A consulta de um intervalo usa-o para não carregar as séries que já acabaram
    /// (crescem com cada "este e os seguintes"). Não depende do fuso: nos eventos com hora, o primeiro dia local está
    /// a um dia do dia UTC. Calcula-se sozinho ao gravar (<see cref="UpdateRecurrenceEnd"/>, chamado pelo DbContext).
    /// </summary>
    public DateOnly? RecurrenceEndDate { get; private set; }

    /// <summary>Volta a calcular <see cref="RecurrenceEndDate"/> a partir da repetição e das datas.</summary>
    public void UpdateRecurrenceEnd()
    {
        RecurrenceEndDate = ComputeRecurrenceEnd();
    }

    private DateOnly? ComputeRecurrenceEnd()
    {
        if (!RecurrenceRule.TryParse(Recurrence, out var rule) || (StartDate is null && StartsAt is null))
        {
            return null;
        }

        if (rule.Count is null && rule.Until is null)
        {
            return DateOnly.MaxValue;
        }

        // O primeiro dia local possível (todos, nos eventos com hora: fusos de -14 h a +14 h).
        DateOnly[] firstDates = StartDate is { } startDate
            ? [startDate]
            : [.. new[] { -1, 0, 1 }.Select(d => DateOnly.FromDateTime(StartsAt!.Value.UtcDateTime).AddDays(d))];

        var lastStart = firstDates.Max(first => rule.Count is null
            ? (rule.Until!.Value > first ? rule.Until.Value : first)
            : rule.Dates(first).Last());
        var end = (long)lastStart.DayNumber + SpanDays() + 3;
        return end >= DateOnly.MaxValue.DayNumber ? DateOnly.MaxValue : DateOnly.FromDayNumber((int)end);
    }

    /// <summary>Se a série tem uma ocorrência neste dia (mesmo que tenha sido retirada).</summary>
    public bool HasOccurrenceOn(DateOnly date, TimeZoneInfo zone) =>
        Dates(FirstDate(zone)).TakeWhile(d => d <= date).Contains(date);

    /// <summary>Retira a ocorrência deste dia ("apagar só este evento" ou substituí-la por uma exceção).</summary>
    public void Exclude(DateOnly date)
    {
        if (!ExcludedDates.Contains(date))
        {
            ExcludedDates = [.. ExcludedDates, date];
        }
    }

    /// <summary>
    /// Termina a série antes de <paramref name="date"/> ("este e os seguintes"). Devolve false quando não sobra
    /// nenhuma ocorrência; nesse caso o evento deve ser apagado.
    /// </summary>
    public bool EndBefore(DateOnly date, TimeZoneInfo zone)
    {
        if (!RecurrenceRule.TryParse(Recurrence, out var rule))
        {
            return false;
        }

        // A última ocorrência que fica; um COUNT passa a UNTIL, para a série não crescer.
        DateOnly? last = null;
        foreach (var d in rule.Dates(FirstDate(zone)).TakeWhile(d => d < date))
        {
            last = d;
        }

        if (last is null)
        {
            return false;
        }

        Recurrence = (rule with { Count = null, Until = last }).ToString();
        ExcludedDates = [.. ExcludedDates.Where(d => d < date)];
        return true;
    }

    private IEnumerable<DateOnly> Dates(DateOnly firstDate) =>
        RecurrenceRule.TryParse(Recurrence, out var rule) ? rule.Dates(firstDate) : [firstDate];

    /// <summary>
    /// A ocorrência de um dia. <paramref name="localTime"/> (a hora local do início) e <paramref name="duration"/>
    /// vêm calculados de fora, uma vez por série.
    /// </summary>
    private EventOccurrence OccurrenceOn(DateOnly date, bool isFirst, TimeZoneInfo zone, TimeOnly localTime, TimeSpan? duration)
    {
        if (StartDate is { } startDate)
        {
            var lastDate = date.AddDays(EndDate!.Value.DayNumber - startDate.DayNumber);
            return new EventOccurrence(
                this,
                date,
                ZonedTime.StartOfDay(date, zone),
                ZonedTime.StartOfDay(lastDate.AddDays(1), zone),
                date,
                lastDate);
        }

        // A primeira usa o instante guardado: numa hora ambígua (quando o relógio recua) a hora local não chega.
        var start = isFirst ? StartsAt!.Value : ZonedTime.ToInstant(date.ToDateTime(localTime), zone);
        return new EventOccurrence(this, date, start, start + duration!.Value);
    }
}

/// <summary>
/// Uma ocorrência concreta de um evento. <see cref="Date"/> é o dia (local) que a identifica dentro da série.
/// Nos de dia inteiro, <see cref="Start"/> e <see cref="End"/> são as meias-noites locais e
/// <see cref="StartDate"/>/<see cref="EndDate"/> os dias (inclusive).
/// </summary>
public sealed record EventOccurrence(
    CalendarEvent Event,
    DateOnly Date,
    DateTimeOffset Start,
    DateTimeOffset End,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null)
{
    public bool IsAllDay => StartDate is not null;

    public bool Overlaps(EventOccurrence other) => Start < other.End && other.Start < End;
}

/// <summary>Conversões entre a hora local da casa e instantes UTC.</summary>
public static class ZonedTime
{
    /// <summary>Hora local → instante UTC. Uma hora que não existe (quando o relógio avança) passa para a seguinte.</summary>
    public static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone), TimeSpan.Zero);
    }

    public static DateTimeOffset StartOfDay(DateOnly date, TimeZoneInfo zone) =>
        ToInstant(date.ToDateTime(TimeOnly.MinValue), zone);
}
