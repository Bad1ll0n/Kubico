namespace Kubico.Domain.Calendar;

/// <summary>
/// Deteção de conflitos: um membro está em dois eventos ao mesmo tempo, ou tem de levar/ir buscar alguém
/// enquanto está noutro evento. Eventos de dia inteiro (aniversários, férias) não ocupam ninguém.
/// </summary>
public static class CalendarConflicts
{
    /// <summary>Para cada ocorrência (pela mesma ordem), os membros que não estão disponíveis para ela.</summary>
    public static IReadOnlyList<IReadOnlySet<Guid>> Find(IReadOnlyList<EventOccurrence> occurrences)
    {
        var conflicts = occurrences.Select(_ => new HashSet<Guid>()).ToArray();

        for (var i = 0; i < occurrences.Count; i++)
        {
            for (var j = i + 1; j < occurrences.Count; j++)
            {
                var (a, b) = (occurrences[i], occurrences[j]);
                if (a.IsAllDay || b.IsAllDay || a.Event.Id == b.Event.Id)
                {
                    continue;
                }

                if (a.Overlaps(b))
                {
                    foreach (var userId in a.Event.ParticipantUserIds.Intersect(b.Event.ParticipantUserIds))
                    {
                        conflicts[i].Add(userId);
                        conflicts[j].Add(userId);
                    }
                }

                AddRideConflicts(a, b, conflicts[i]);
                AddRideConflicts(b, a, conflicts[j]);
            }
        }

        return conflicts;
    }

    /// <summary>Quem leva tem de estar livre no início de <paramref name="ride"/>; quem vai buscar, no fim.</summary>
    private static void AddRideConflicts(EventOccurrence ride, EventOccurrence other, HashSet<Guid> conflicts)
    {
        var busy = other.Event.ParticipantUserIds;
        if (ride.Event.DropOffUserId is { } dropOff && busy.Contains(dropOff)
            && other.Start <= ride.Start && ride.Start < other.End)
        {
            conflicts.Add(dropOff);
        }

        if (ride.Event.PickUpUserId is { } pickUp && busy.Contains(pickUp)
            && other.Start < ride.End && ride.End <= other.End)
        {
            conflicts.Add(pickUp);
        }
    }
}
