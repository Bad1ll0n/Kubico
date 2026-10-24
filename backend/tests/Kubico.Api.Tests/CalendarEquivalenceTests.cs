using System.Net.Http.Json;
using Kubico.Api.Calendar;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>
/// P1 (fase 2, desempenho): a expansão das séries (que agora salta os dias que já passaram), os conflitos (agora por
/// varrimento, em vez de todos os pares) e a consulta do intervalo (que afasta as séries terminadas com
/// <see cref="CalendarEvent.RecurrenceEndDate"/>) têm de dar exatamente os mesmos resultados que as versões da fase 1,
/// copiadas tal e qual em <see cref="Reference"/>. Comparam-se em milhares de casos gerados ao acaso (com semente fixa):
/// séries que começaram há anos, COUNT e UNTIL, exceções, dias inteiros, eventos de vários dias, mudanças de hora em
/// vários fusos, boleias, ausências e eventos estranhos vindos de fora (duração zero ou negativa).
/// </summary>
public class CalendarEquivalenceTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    private static readonly string[] Zones =
        ["Europe/Lisbon", "America/New_York", "Australia/Sydney", "Pacific/Chatham", "America/Sao_Paulo", "UTC"];

    private static readonly Guid[] People = [.. Enumerable.Range(1, 6).Select(i => new Guid(i, 0, 0, new byte[8]))];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Occurrences_and_conflicts_are_the_same_as_before(int seed)
    {
        var rnd = new Random(seed);
        var cases = 0;
        foreach (var zoneId in Zones)
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            for (var round = 0; round < 25; round++)
            {
                var (windowFirst, windowLast) = Window(rnd, zone);
                var windowStart = ZonedTime.StartOfDay(windowFirst, zone);
                var windowEnd = ZonedTime.StartOfDay(windowLast.AddDays(1), zone);
                var events = Enumerable.Range(0, rnd.Next(5, 60)).Select(_ => RandomEvent(rnd, windowFirst, zone)).ToList();
                AddExclusions(rnd, events, windowStart, windowEnd, zone);

                foreach (var e in events)
                {
                    var expected = Reference.Occurrences(e, windowStart, windowEnd, zone).ToList();
                    var actual = e.Occurrences(windowStart, windowEnd, zone).ToList();
                    Assert.Equal(expected, actual);
                    CheckRecurrenceEnd(e, zone);
                    cases++;
                }

                var occurrences = Sorted(events.SelectMany(e => Reference.Occurrences(e, windowStart, windowEnd, zone)));
                var absences = RandomAbsences(rnd, windowFirst);
                var isAway = MemberAbsence.Checker(absences);
                foreach (var margin in new TimeSpan?[] { null, TimeSpan.Zero, TimeSpan.FromMinutes(40), TimeSpan.FromMinutes(-5) })
                {
                    AssertSameConflicts(
                        Reference.Find(occurrences, isAway, zone, margin), CalendarConflicts.Find(occurrences, isAway, zone, margin));
                }

                AssertSameConflicts(Reference.Find(occurrences), CalendarConflicts.Find(occurrences));
            }
        }

        Assert.True(cases > 1000, $"só {cases} casos");
    }

    /// <summary>Uma casa cheia (como a "Teste Perf — Grande"): 500 eventos, mais de 3 000 ocorrências no mês.</summary>
    [Fact]
    public void A_full_house_month_gives_the_same_conflicts()
    {
        var rnd = new Random(42);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");
        var (first, last) = (new DateOnly(2026, 9, 28), new DateOnly(2026, 11, 8));
        var windowStart = ZonedTime.StartOfDay(first.AddDays(-1), zone);
        var windowEnd = ZonedTime.StartOfDay(last.AddDays(2), zone);
        var events = Enumerable.Range(0, 500).Select(_ => RandomEvent(rnd, first, zone)).ToList();
        AddExclusions(rnd, events, windowStart, windowEnd, zone);

        var expected = Sorted(events.SelectMany(e => Reference.Occurrences(e, windowStart, windowEnd, zone)));
        var actual = Sorted(events.SelectMany(e => e.Occurrences(windowStart, windowEnd, zone)));
        var isAway = MemberAbsence.Checker(RandomAbsences(rnd, first));

        Assert.Equal(expected, actual);
        Assert.True(expected.Count > 1000, $"só {expected.Count} ocorrências");
        AssertSameConflicts(Reference.Find(expected, isAway, zone), CalendarConflicts.Find(actual, isAway, zone));
    }

    [Theory]
    [InlineData("FREQ=DAILY", "2023-03-26", "2026-10-25")]
    [InlineData("FREQ=DAILY;INTERVAL=3;UNTIL=20261102", "2021-01-01", "2026-10-20")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2", "2020-02-29", "2026-03-29")]
    [InlineData("FREQ=WEEKLY;BYDAY=MO,WE,SU", "2019-12-31", "2026-03-29")]
    [InlineData("FREQ=WEEKLY;INTERVAL=3;BYDAY=TU,SA;UNTIL=20261101", "2018-07-04", "2026-10-25")]
    [InlineData("FREQ=WEEKLY;BYDAY=FR;UNTIL=20200101", "2018-07-04", "2026-10-25")]
    [InlineData("FREQ=WEEKLY;BYDAY=TH;COUNT=400", "2020-01-02", "2026-10-25")]
    [InlineData("FREQ=MONTHLY;INTERVAL=2", "2001-01-31", "2026-10-25")]
    [InlineData("FREQ=YEARLY", "1960-02-29", "2028-02-01")]
    public void Skipping_to_the_window_gives_the_same_dates(string text, string start, string notBefore)
    {
        Assert.True(RecurrenceRule.TryParse(text, out var rule));
        var (from, skip) = (DateOnly.Parse(start), DateOnly.Parse(notBefore));

        var expected = rule.Dates(from).SkipWhile(d => d < skip).Take(500).ToList();
        var actual = rule.Dates(from, skip).Take(500).ToList();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Ao nível da API: o que GET /events devolve (com a consulta que afasta as séries terminadas) é igual ao que as
    /// versões da fase 1 calculam sobre todos os eventos da casa, também depois de "este e os seguintes", de exceções
    /// e de dias apagados.
    /// </summary>
    [Fact]
    public async Task The_calendar_endpoint_returns_the_same_as_before_for_every_window()
    {
        var (admin, auth, householdId) = await Fase1.HouseholdAsync(factory);
        var (_, dad) = await Fase1.MemberAsync(factory, admin, householdId, "Pai", HouseholdRole.Adult);
        var people = new[] { auth.User.Id, dad };
        var rnd = new Random(7);
        var url = $"/api/households/{householdId}/events";
        var anchor = new DateOnly(2026, 10, 1);
        var created = new List<CalendarEventDto>();

        for (var i = 0; i < 60; i++)
        {
            var start = new DateTimeOffset(anchor.AddDays(rnd.Next(-900, 120)).ToDateTime(new TimeOnly(rnd.Next(0, 24), rnd.Next(0, 4) * 15)), TimeSpan.Zero);
            var allDay = rnd.Next(5) == 0;
            var recurrence = rnd.Next(6) switch
            {
                0 => null,
                1 => "FREQ=DAILY",
                2 => $"FREQ=WEEKLY;BYDAY={string.Join(',', new[] { "MO", "TU", "WE", "TH", "FR", "SA", "SU" }.OrderBy(_ => rnd.Next()).Take(rnd.Next(1, 4)))}",
                3 => $"FREQ=DAILY;COUNT={rnd.Next(1, 40)}",
                4 => $"FREQ=WEEKLY;UNTIL={anchor.AddDays(rnd.Next(-400, 200)):yyyyMMdd}",
                _ => "FREQ=MONTHLY",
            };
            var body = new SaveEventRequest(
                $"Evento {i}",
                IsAllDay: allDay,
                StartsAt: allDay ? null : start,
                EndsAt: allDay ? null : start.AddMinutes(rnd.Next(1, 8) * 30),
                StartDate: allDay ? DateOnly.FromDateTime(start.UtcDateTime) : null,
                EndDate: allDay ? DateOnly.FromDateTime(start.UtcDateTime).AddDays(rnd.Next(0, 3)) : null,
                Recurrence: recurrence,
                ParticipantUserIds: [.. people.Where(_ => rnd.Next(2) == 0)],
                DropOffUserId: rnd.Next(4) == 0 ? people[rnd.Next(2)] : null,
                PickUpUserId: rnd.Next(4) == 0 ? people[rnd.Next(2)] : null);
            var response = await admin.PostAsJsonAsync(url, body, TestJson.Options);
            response.EnsureSuccessStatusCode();
            created.Add((await response.Content.ReadFromJsonAsync<CalendarEventDto>(TestJson.Options))!);
        }

        // "Este e os seguintes", "só este" e apagar um dia, em séries que se repetem.
        var month = await admin.GetFromJsonAsync<CalendarDto>($"{url}?from=2026-09-28&to=2026-11-08", TestJson.Options);
        var recurring = month!.Occurrences.Where(o => month.Events.Single(e => e.Id == o.EventId).Recurrence is not null)
            .GroupBy(o => o.EventId).Select(g => g.Skip(2).FirstOrDefault()).OfType<CalendarOccurrenceDto>().Take(9).ToList();
        for (var i = 0; i < recurring.Count; i++)
        {
            var o = recurring[i];
            var series = month.Events.Single(e => e.Id == o.EventId);
            var path = $"{url}/{o.EventId}/occurrences/{o.OccurrenceDate:yyyy-MM-dd}";
            var change = new SaveEventRequest(series.Title + " (mudado)", IsAllDay: series.IsAllDay,
                StartsAt: o.StartsAt?.AddHours(1), EndsAt: o.EndsAt?.AddHours(2), StartDate: o.StartDate, EndDate: o.EndDate,
                ParticipantUserIds: series.ParticipantUserIds);
            var response = (i % 3) switch
            {
                0 => await admin.PutAsJsonAsync($"{path}?scope=following", change with { Recurrence = series.Recurrence }, TestJson.Options),
                1 => await admin.PutAsJsonAsync($"{path}?scope=this", change, TestJson.Options),
                _ => await admin.DeleteAsync($"{path}?scope=this"),
            };
            response.EnsureSuccessStatusCode();
        }

        foreach (var (from, days) in new[] { ("2026-09-28", 42), ("2026-10-19", 7), ("2026-03-23", 14), ("2025-12-29", 92), ("2024-06-01", 30), ("2027-01-01", 60) })
        {
            var first = DateOnly.Parse(from);
            var last = first.AddDays(days - 1);
            var actual = await admin.GetFromJsonAsync<CalendarDto>($"{url}?from={first:yyyy-MM-dd}&to={last:yyyy-MM-dd}", TestJson.Options);
            var expected = await ReferenceCalendarAsync(householdId, first, last);

            Assert.Equal(
                expected.Select(Describe),
                actual!.Occurrences.Select(o => Describe((o.EventId, o.OccurrenceDate, o.StartsAt, o.EndsAt, o.StartDate, o.EndDate, o.ConflictUserIds))));
        }
    }

    /// <summary>O GET /events da fase 1: todos os eventos da casa (sem filtrar as séries terminadas) e as versões antigas.</summary>
    private async Task<List<(Guid, DateOnly, DateTimeOffset?, DateTimeOffset?, DateOnly?, DateOnly?, IReadOnlyList<Guid>)>> ReferenceCalendarAsync(
        Guid householdId, DateOnly first, DateOnly last)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<KubicoDbContext>();
        var zone = (await db.Households.SingleAsync(h => h.Id == householdId)).TimeZone;
        var (windowFirst, windowLast) = (first.AddDays(-1), last.AddDays(1));
        var windowStart = ZonedTime.StartOfDay(windowFirst, zone);
        var windowEnd = ZonedTime.StartOfDay(windowLast.AddDays(1), zone);
        var events = await db.CalendarEvents.AsNoTracking().Where(e => e.HouseholdId == householdId).ToListAsync();
        var occurrences = events.SelectMany(e => Reference.Occurrences(e, windowStart, windowEnd, zone))
            .OrderBy(o => o.Start).ThenByDescending(o => o.IsAllDay).ThenBy(o => o.Event.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var absences = await db.MemberAbsences.AsNoTracking().Where(a => a.HouseholdId == householdId).ToListAsync();
        var conflicts = Reference.Find(occurrences, MemberAbsence.Checker(absences), zone);
        var (rangeStart, rangeEnd) = (ZonedTime.StartOfDay(first, zone), ZonedTime.StartOfDay(last.AddDays(1), zone));
        return [.. occurrences.Index()
            .Where(x => x.Item.Start < rangeEnd && x.Item.End > rangeStart)
            .Select(x => (x.Item.Event.Id, x.Item.Date, x.Item.IsAllDay ? (DateTimeOffset?)null : x.Item.Start,
                x.Item.IsAllDay ? (DateTimeOffset?)null : x.Item.End, x.Item.StartDate, x.Item.EndDate,
                (IReadOnlyList<Guid>)[.. conflicts[x.Index].Order()]))];
    }

    private static string Describe((Guid Id, DateOnly Date, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, DateOnly? StartDate, DateOnly? EndDate, IReadOnlyList<Guid> Conflicts) o) =>
        $"{o.Id} {o.Date} {o.StartsAt?.UtcTicks} {o.EndsAt?.UtcTicks} {o.StartDate} {o.EndDate} [{string.Join(',', o.Conflicts)}]";

    /// <summary>
    /// O fim calculado para a série nunca fica antes do fim de uma ocorrência (seja qual for o fuso): é isso que deixa
    /// a consulta afastar as séries com <c>RecurrenceEndDate &lt; primeiro dia</c> sem perder nada.
    /// </summary>
    private static void CheckRecurrenceEnd(CalendarEvent e, TimeZoneInfo zone)
    {
        e.UpdateRecurrenceEnd();
        if (!RecurrenceRule.TryParse(e.Recurrence, out var rule))
        {
            Assert.Null(e.RecurrenceEndDate);
            return;
        }

        if (rule.Count is null && rule.Until is null)
        {
            Assert.Equal(DateOnly.MaxValue, e.RecurrenceEndDate);
            return;
        }

        var limit = ZonedTime.StartOfDay(e.RecurrenceEndDate!.Value, zone);
        var all = Reference.Occurrences(e, DateTimeOffset.MinValue.AddYears(1), DateTimeOffset.MaxValue.AddYears(-1), zone);
        Assert.All(all, o => Assert.True(o.End <= limit, $"{e.Recurrence} {o.Date}: {o.End} > {limit}"));
    }

    private static (DateOnly First, DateOnly Last) Window(Random rnd, TimeZoneInfo zone)
    {
        // Metade das janelas apanha uma mudança de hora (no fuso que se está a testar) e as outras são ao acaso.
        var year = rnd.Next(2024, 2029);
        var transitions = zone.GetAdjustmentRules()
            .Where(r => r.DateStart.Year <= year && r.DateEnd.Year >= year && r.DaylightDelta != TimeSpan.Zero)
            .SelectMany(r => new[] { TransitionDate(r.DaylightTransitionStart, year), TransitionDate(r.DaylightTransitionEnd, year) })
            .ToList();
        var center = transitions.Count > 0 && rnd.Next(2) == 0
            ? transitions[rnd.Next(transitions.Count)]
            : new DateOnly(year, 1, 1).AddDays(rnd.Next(365));
        var first = center.AddDays(-rnd.Next(0, 20));
        return (first, first.AddDays(rnd.Next(0, 92)));
    }

    private static DateOnly TransitionDate(TimeZoneInfo.TransitionTime t, int year)
    {
        if (t.IsFixedDateRule)
        {
            return new DateOnly(year, t.Month, Math.Min(t.Day, DateTime.DaysInMonth(year, t.Month)));
        }

        var day = new DateOnly(year, t.Month, 1);
        while (day.DayOfWeek != t.DayOfWeek)
        {
            day = day.AddDays(1);
        }

        day = day.AddDays(7 * (t.Week - 1));
        while (day.Month != t.Month)
        {
            day = day.AddDays(-7);
        }

        return day;
    }

    private static CalendarEvent RandomEvent(Random rnd, DateOnly near, TimeZoneInfo zone)
    {
        var e = new CalendarEvent { Title = $"E{rnd.Next(1000)}", Id = Guid.NewGuid() };
        var kind = rnd.Next(10);
        var recurring = kind >= 4;
        var startDay = recurring ? near.AddDays(-rnd.Next(0, 4 * 365)) : near.AddDays(rnd.Next(-40, 120));

        if (rnd.Next(6) == 0)
        {
            // Dia inteiro: um só dia, vários dias e, de vez em quando, um vindo de fora com o fim antes do início.
            var span = rnd.Next(8) == 0 ? -rnd.Next(1, 3) : rnd.Next(0, 4);
            e.SetAllDay(startDay, startDay.AddDays(span));
        }
        else
        {
            // Muitas horas perto da meia-noite e da 1-2 h (onde estão as mudanças de hora).
            var hour = rnd.Next(3) == 0 ? rnd.Next(0, 4) : rnd.Next(0, 24);
            var local = startDay.ToDateTime(new TimeOnly(hour, rnd.Next(0, 4) * 15));
            var start = rnd.Next(2) == 0
                ? ZonedTime.ToInstant(local, zone)
                : new DateTimeOffset(local, TimeSpan.Zero).AddMinutes(rnd.Next(-90, 90));
            var minutes = rnd.Next(12) switch
            {
                0 => 0,
                1 => -rnd.Next(10, 120),
                2 => rnd.Next(24 * 60, 4 * 24 * 60),
                _ => rnd.Next(1, 16) * 15,
            };
            e.SetTimed(start, start.AddMinutes(minutes));
        }

        if (recurring)
        {
            var interval = rnd.Next(4) == 0 ? $";INTERVAL={rnd.Next(2, 4)}" : "";
            var end = rnd.Next(4) switch
            {
                0 => $";COUNT={rnd.Next(1, 300)}",
                1 => $";UNTIL={near.AddDays(rnd.Next(-500, 200)):yyyyMMdd}",
                _ => "",
            };
            var days = string.Join(',', new[] { "MO", "TU", "WE", "TH", "FR", "SA", "SU" }.OrderBy(_ => rnd.Next()).Take(rnd.Next(1, 5)));
            e.Recurrence = rnd.Next(6) switch
            {
                0 or 1 => $"FREQ=DAILY{interval}{end}",
                2 => $"FREQ=WEEKLY{interval}{end}",
                3 => $"FREQ=WEEKLY{interval};BYDAY={days}{end}",
                4 => $"FREQ=MONTHLY{interval}{end}",
                _ => $"FREQ=YEARLY{end}",
            };
        }

        e.ParticipantUserIds = [.. People.OrderBy(_ => rnd.Next()).Take(rnd.Next(0, 4))];
        e.DropOffUserId = rnd.Next(3) == 0 ? People[rnd.Next(3)] : null;
        e.PickUpUserId = rnd.Next(3) == 0 ? People[rnd.Next(3)] : null;
        return e;
    }

    /// <summary>Exceções: dias apagados dentro da janela (e alguns fora); às vezes muitos, para usar o HashSet.</summary>
    private static void AddExclusions(Random rnd, List<CalendarEvent> events, DateTimeOffset windowStart, DateTimeOffset windowEnd, TimeZoneInfo zone)
    {
        foreach (var e in events.Where(e => e.IsRecurring && rnd.Next(3) == 0))
        {
            var dates = Reference.Occurrences(e, windowStart, windowEnd, zone).Select(o => o.Date).ToList();
            var take = rnd.Next(4) == 0 ? 20 : rnd.Next(1, 4);
            foreach (var date in dates.OrderBy(_ => rnd.Next()).Take(take))
            {
                e.Exclude(date);
            }

            e.Exclude(e.FirstDate(zone));
            if (rnd.Next(2) == 0)
            {
                e.Exclude(e.FirstDate(zone).AddDays(rnd.Next(1, 30)));
            }
        }
    }

    private static List<MemberAbsence> RandomAbsences(Random rnd, DateOnly near) =>
        [.. Enumerable.Range(0, rnd.Next(0, 5)).Select(_ =>
        {
            var start = near.AddDays(rnd.Next(-5, 40));
            return new MemberAbsence { UserId = People[rnd.Next(People.Length)], StartDate = start, EndDate = start.AddDays(rnd.Next(0, 6)) };
        })];

    private static List<EventOccurrence> Sorted(IEnumerable<EventOccurrence> occurrences) =>
        [.. occurrences.OrderBy(o => o.Start).ThenByDescending(o => o.IsAllDay).ThenBy(o => o.Event.Title, StringComparer.Ordinal)];

    private static void AssertSameConflicts(IReadOnlyList<IReadOnlySet<Guid>> expected, IReadOnlyList<IReadOnlySet<Guid>> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.True(expected[i].SetEquals(actual[i]), $"ocorrência {i}: [{string.Join(',', expected[i])}] ≠ [{string.Join(',', actual[i])}]");
        }
    }

    /// <summary>As versões da fase 1 (commit c71d529), copiadas sem mudanças, para comparar.</summary>
    private static class Reference
    {
        public static IEnumerable<EventOccurrence> Occurrences(CalendarEvent e, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo zone)
        {
            var firstDate = e.FirstDate(zone);
            IEnumerable<DateOnly> dates = RecurrenceRule.TryParse(e.Recurrence, out var rule) ? rule.Dates(firstDate) : [firstDate];
            foreach (var date in dates)
            {
                var occurrence = OccurrenceOn(e, date, date == firstDate, zone);
                if (occurrence.Start >= to)
                {
                    yield break;
                }

                if (occurrence.End > from && !e.ExcludedDates.Contains(date))
                {
                    yield return occurrence;
                }
            }
        }

        private static EventOccurrence OccurrenceOn(CalendarEvent e, DateOnly date, bool isFirst, TimeZoneInfo zone)
        {
            if (e.StartDate is { } startDate)
            {
                var lastDate = date.AddDays(e.EndDate!.Value.DayNumber - startDate.DayNumber);
                return new EventOccurrence(e, date, ZonedTime.StartOfDay(date, zone), ZonedTime.StartOfDay(lastDate.AddDays(1), zone), date, lastDate);
            }

            var duration = e.EndsAt!.Value - e.StartsAt!.Value;
            var localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(e.StartsAt.Value, zone).DateTime);
            var start = isFirst ? e.StartsAt.Value : ZonedTime.ToInstant(date.ToDateTime(localTime), zone);
            return new EventOccurrence(e, date, start, start + duration);
        }

        public static IReadOnlyList<IReadOnlySet<Guid>> Find(
            IReadOnlyList<EventOccurrence> occurrences, Func<Guid, DateOnly, bool>? isAway = null, TimeZoneInfo? zone = null, TimeSpan? rideMargin = null)
        {
            var margin = rideMargin ?? CalendarConflicts.DefaultRideMargin;
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
            var inside = atEnd ? other.Start < moment && moment <= other.End : other.Start <= moment && moment < other.End;
            if (other.Event.ParticipantUserIds.Contains(userId) && inside)
            {
                return true;
            }

            return (other.Event.DropOffUserId == userId && Near(other.Start, moment, margin))
                || (other.Event.PickUpUserId == userId && Near(other.End, moment, margin));
        }

        private static bool Near(DateTimeOffset a, DateTimeOffset b, TimeSpan margin) => (a - b).Duration() < margin;

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
}
