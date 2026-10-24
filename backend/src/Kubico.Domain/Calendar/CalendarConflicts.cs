namespace Kubico.Domain.Calendar;

/// <summary>
/// Deteção de conflitos: um membro está em dois eventos ao mesmo tempo; tem de levar ou ir buscar alguém enquanto
/// está noutro evento; tem duas boleias à mesma hora (levar ou ir buscar em dois sítios diferentes, com uma margem);
/// ou está fora (ausência) no dia de um evento em que participa ou em que leva ou vai buscar. Eventos de dia inteiro
/// (aniversários, férias) não ocupam ninguém.
/// </summary>
public static class CalendarConflicts
{
    /// <summary>Duas boleias da mesma pessoa a menos deste tempo uma da outra não dão (sítios diferentes).</summary>
    public static readonly TimeSpan DefaultRideMargin = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Para cada ocorrência (pela mesma ordem), os membros que não estão disponíveis para ela. Com
    /// <paramref name="isAway"/> (e o fuso da casa), quem está fora nesse dia também conta.
    /// </summary>
    public static IReadOnlyList<IReadOnlySet<Guid>> Find(
        IReadOnlyList<EventOccurrence> occurrences,
        Func<Guid, DateOnly, bool>? isAway = null,
        TimeZoneInfo? zone = null,
        TimeSpan? rideMargin = null)
    {
        var margin = rideMargin ?? DefaultRideMargin;
        var conflicts = occurrences.Select(_ => new HashSet<Guid>()).ToArray();

        // Varrimento por ordem de início, em vez de comparar todos os pares (O(n²): 0,5 s num mês de uma casa cheia).
        // Todos os conflitos (sobreposição, boleia durante outro evento, duas boleias a menos da margem) exigem que os
        // intervalos [início, fim] dos dois, alargados pela margem, se toquem; por isso, para cada ocorrência, só
        // interessam as seguintes que começam até ao seu fim mais a margem. Usa-se o menor e o maior dos dois instantes,
        // para continuar certo mesmo num evento (vindo de fora) que acabe antes de começar. Os de dia inteiro não ocupam
        // ninguém. Os resultados são os mesmos da comparação de todos os pares (CalendarEquivalenceTests).
        var timed = Enumerable.Range(0, occurrences.Count)
            .Where(i => !occurrences[i].IsAllDay)
            .Select(i => (Index: i, Low: Min(occurrences[i].Start, occurrences[i].End), High: Max(occurrences[i].Start, occurrences[i].End)))
            .OrderBy(x => x.Low)
            .ToArray();

        for (var x = 0; x < timed.Length; x++)
        {
            var reach = timed[x].High + (margin > TimeSpan.Zero ? margin : TimeSpan.Zero);
            for (var y = x + 1; y < timed.Length && timed[y].Low <= reach; y++)
            {
                var (i, j) = (timed[x].Index, timed[y].Index);
                var (a, b) = (occurrences[i], occurrences[j]);
                if (a.Event.Id == b.Event.Id)
                {
                    continue;
                }

                if (a.Overlaps(b))
                {
                    foreach (var userId in a.Event.ParticipantUserIds)
                    {
                        if (b.Event.ParticipantUserIds.Contains(userId))
                        {
                            conflicts[i].Add(userId);
                            conflicts[j].Add(userId);
                        }
                    }
                }

                AddRideConflicts(a, b, conflicts[i], margin);
                AddRideConflicts(b, a, conflicts[j], margin);
            }
        }

        if (isAway is not null)
        {
            for (var i = 0; i < occurrences.Count; i++)
            {
                AddAbsences(occurrences[i], isAway, zone ?? TimeZoneInfo.Utc, conflicts[i]);
            }
        }

        return conflicts;
    }

    /// <summary>
    /// Quem leva tem de estar livre no início de <paramref name="ride"/>, e quem vai buscar, no fim: não pode estar a
    /// participar noutro evento nesse momento, nem ter outra boleia (levar ou ir buscar) a menos da margem.
    /// </summary>
    private static void AddRideConflicts(EventOccurrence ride, EventOccurrence other, HashSet<Guid> conflicts, TimeSpan margin)
    {
        if (ride.Event.DropOffUserId is { } dropOff && IsBusyAt(dropOff, ride.Start, other, margin))
        {
            conflicts.Add(dropOff);
        }

        if (ride.Event.PickUpUserId is { } pickUp && IsBusyAt(pickUp, ride.End, other, margin, atEnd: true))
        {
            conflicts.Add(pickUp);
        }
    }

    private static bool IsBusyAt(Guid userId, DateTimeOffset moment, EventOccurrence other, TimeSpan margin, bool atEnd = false)
    {
        var inside = atEnd
            ? other.Start < moment && moment <= other.End
            : other.Start <= moment && moment < other.End;
        if (other.Event.ParticipantUserIds.Contains(userId) && inside)
        {
            return true;
        }

        // Outra boleia da mesma pessoa: levar no início do outro evento, ou ir buscar no fim.
        return (other.Event.DropOffUserId == userId && Near(other.Start, moment, margin))
            || (other.Event.PickUpUserId == userId && Near(other.End, moment, margin));
    }

    private static bool Near(DateTimeOffset a, DateTimeOffset b, TimeSpan margin) => (a - b).Duration() < margin;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;

    /// <summary>Quem participa, leva ou vai buscar num evento com hora e está fora nesse dia (no fuso da casa).</summary>
    private static void AddAbsences(EventOccurrence occurrence, Func<Guid, DateOnly, bool> isAway, TimeZoneInfo zone, HashSet<Guid> conflicts)
    {
        if (occurrence.IsAllDay)
        {
            return;
        }

        var first = LocalDate(occurrence.Start, zone);
        var last = LocalDate(occurrence.End.AddTicks(-1), zone);
        foreach (var userId in occurrence.Event.ParticipantUserIds)
        {
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                if (isAway(userId, day))
                {
                    conflicts.Add(userId);
                    break;
                }
            }
        }

        if (occurrence.Event.DropOffUserId is { } dropOff && isAway(dropOff, first))
        {
            conflicts.Add(dropOff);
        }

        if (occurrence.Event.PickUpUserId is { } pickUp && isAway(pickUp, last))
        {
            conflicts.Add(pickUp);
        }
    }

    private static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}
