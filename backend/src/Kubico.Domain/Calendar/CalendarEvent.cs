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
    /// Ocorrências que se cruzam com [<paramref name="from"/>, <paramref name="to"/>). As repetições mantêm a hora
    /// local da casa: um evento às 18:00 continua às 18:00 depois da mudança de hora.
    /// </summary>
    public IEnumerable<EventOccurrence> Occurrences(DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
    {
        var firstDate = FirstDate(zone);
        foreach (var date in Dates(firstDate))
        {
            var occurrence = OccurrenceOn(date, date == firstDate, zone);
            if (occurrence.Start >= to)
            {
                yield break;
            }

            if (occurrence.End > from && !ExcludedDates.Contains(date))
            {
                yield return occurrence;
            }
        }
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

    private EventOccurrence OccurrenceOn(DateOnly date, bool isFirst, TimeZoneInfo zone)
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

        var duration = EndsAt!.Value - StartsAt!.Value;
        var localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(StartsAt.Value, zone).DateTime);

        // A primeira usa o instante guardado: numa hora ambígua (quando o relógio recua) a hora local não chega.
        var start = isFirst ? StartsAt.Value : ZonedTime.ToInstant(date.ToDateTime(localTime), zone);
        return new EventOccurrence(this, date, start, start + duration);
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
