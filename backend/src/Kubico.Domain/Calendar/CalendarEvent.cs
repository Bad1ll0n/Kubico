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

    /// <summary>Membros envolvidos. A cor do evento e a deteção de conflitos vêm daqui.</summary>
    public List<Guid> ParticipantUserIds { get; set; } = [];

    /// <summary>Boleias: quem leva (no início do evento) e quem vai buscar (no fim).</summary>
    public Guid? DropOffUserId { get; set; }
    public Guid? PickUpUserId { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsAllDay => StartDate is not null;

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
        var firstDate = StartDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(StartsAt!.Value, zone).DateTime);
        var dates = RecurrenceRule.TryParse(Recurrence, out var rule) ? rule.Dates(firstDate) : [firstDate];

        foreach (var date in dates)
        {
            var occurrence = OccurrenceOn(date, date == firstDate, zone);
            if (occurrence.Start >= to)
            {
                yield break;
            }

            if (occurrence.End > from)
            {
                yield return occurrence;
            }
        }
    }

    private EventOccurrence OccurrenceOn(DateOnly date, bool isFirst, TimeZoneInfo zone)
    {
        if (StartDate is { } startDate)
        {
            var lastDate = date.AddDays(EndDate!.Value.DayNumber - startDate.DayNumber);
            return new EventOccurrence(
                this, ZonedTime.StartOfDay(date, zone), ZonedTime.StartOfDay(lastDate.AddDays(1), zone), date, lastDate);
        }

        var duration = EndsAt!.Value - StartsAt!.Value;
        var localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(StartsAt.Value, zone).DateTime);

        // A primeira usa o instante guardado: numa hora ambígua (quando o relógio recua) a hora local não chega.
        var start = isFirst ? StartsAt.Value : ZonedTime.ToInstant(date.ToDateTime(localTime), zone);
        return new EventOccurrence(this, start, start + duration);
    }
}

/// <summary>
/// Uma ocorrência concreta de um evento. Nos de dia inteiro, <see cref="Start"/> e <see cref="End"/> são as
/// meias-noites locais e <see cref="StartDate"/>/<see cref="EndDate"/> os dias (inclusive).
/// </summary>
public sealed record EventOccurrence(
    CalendarEvent Event,
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
