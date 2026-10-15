using Kubico.Domain.Calendar;

namespace Kubico.Api.Tests;

/// <summary>Repetições, feriados e conflitos do calendário, sem base de dados.</summary>
public class CalendarRulesTests
{
    private static readonly TimeZoneInfo Lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon");

    [Theory]
    [InlineData("FREQ=WEEKLY;BYDAY=TU,TH", "FREQ=WEEKLY;BYDAY=TU,TH")]
    [InlineData("RRULE:freq=weekly;byday=th,tu;interval=2", "FREQ=WEEKLY;INTERVAL=2;BYDAY=TU,TH")]
    [InlineData("FREQ=MONTHLY;INTERVAL=1;COUNT=3", "FREQ=MONTHLY;COUNT=3")]
    [InlineData("FREQ=DAILY;UNTIL=20261231T235959Z", "FREQ=DAILY;UNTIL=20261231")]
    public void Rules_are_kept_in_canonical_form(string text, string canonical)
    {
        Assert.True(RecurrenceRule.TryParse(text, out var rule));
        Assert.Equal(canonical, rule.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("FREQ=HOURLY")]
    [InlineData("INTERVAL=2")]
    [InlineData("FREQ=DAILY;INTERVAL=0")]
    [InlineData("FREQ=MONTHLY;BYDAY=MO")]
    [InlineData("FREQ=WEEKLY;BYDAY=1MO")]
    [InlineData("FREQ=DAILY;COUNT=3;UNTIL=20261231")]
    [InlineData("FREQ=MONTHLY;BYSETPOS=-1")]
    public void Unsupported_rules_are_rejected(string text)
    {
        Assert.False(RecurrenceRule.TryParse(text, out _));
    }

    [Theory]
    [InlineData("FREQ=WEEKLY;BYDAY=TU,TH;COUNT=5", "2026-10-20", "2026-10-20,2026-10-22,2026-10-27,2026-10-29,2026-11-03")]
    [InlineData("FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,FR", "2026-10-12", "2026-10-12,2026-10-16,2026-10-26,2026-10-30,2026-11-09")]
    [InlineData("FREQ=MONTHLY;COUNT=4", "2026-01-31", "2026-01-31,2026-03-31,2026-05-31,2026-07-31")]
    [InlineData("FREQ=YEARLY;COUNT=3", "2024-02-29", "2024-02-29,2028-02-29,2032-02-29")]
    [InlineData("FREQ=DAILY;UNTIL=20261022", "2026-10-20", "2026-10-20,2026-10-21,2026-10-22")]
    [InlineData("FREQ=DAILY;UNTIL=20261001", "2026-10-20", "2026-10-20")]
    public void Rules_generate_the_expected_dates(string text, string start, string expected)
    {
        Assert.True(RecurrenceRule.TryParse(text, out var rule));

        var dates = rule.Dates(DateOnly.Parse(start)).Take(5).Select(d => d.ToString("yyyy-MM-dd"));

        Assert.Equal(expected, string.Join(',', dates));
    }

    [Fact]
    public void Weekly_event_keeps_its_local_time_after_summer_time_ends()
    {
        // 18:00 em Lisboa: 17:00 UTC no verão, 18:00 UTC depois de 25 de outubro de 2026.
        var swimming = Timed("Natação", Utc(2026, 10, 20, 17), hours: 1, recurrence: "FREQ=WEEKLY;BYDAY=TU");

        var starts = swimming.Occurrences(Utc(2026, 10, 19), Utc(2026, 11, 1), Lisbon).Select(o => o.Start);

        Assert.Equal(new[] { Utc(2026, 10, 20, 17), Utc(2026, 10, 27, 18) }, starts);
    }

    [Fact]
    public void Multi_day_event_is_found_by_any_of_its_days()
    {
        var holidays = new CalendarEvent { Title = "Férias" };
        holidays.SetAllDay(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 15));

        var occurrence = Assert.Single(holidays.Occurrences(Utc(2026, 8, 10), Utc(2026, 8, 11), Lisbon));

        Assert.Equal(new DateOnly(2026, 8, 1), occurrence.StartDate);
        Assert.Equal(new DateOnly(2026, 8, 15), occurrence.EndDate);
        Assert.Empty(holidays.Occurrences(Utc(2026, 8, 16), Utc(2026, 8, 20), Lisbon));
    }

    [Theory]
    [InlineData(2025, "2025-04-20")]
    [InlineData(2026, "2026-04-05")]
    [InlineData(2027, "2027-03-28")]
    public void Easter_sunday(int year, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), PortugueseHolidays.EasterSunday(year));
    }

    [Fact]
    public void Portugal_has_13_national_holidays_some_of_them_moving_with_easter()
    {
        var holidays = PortugueseHolidays.For(2026).ToList();

        Assert.Equal(13, holidays.Count);
        Assert.Contains(new Holiday(new DateOnly(2026, 4, 3), "Sexta-feira Santa"), holidays);
        Assert.Contains(new Holiday(new DateOnly(2026, 6, 4), "Corpo de Deus"), holidays);
        Assert.Equal(holidays.OrderBy(h => h.Date), holidays);
    }

    [Fact]
    public void Same_member_in_two_overlapping_events_is_a_conflict_for_both()
    {
        var mom = Guid.NewGuid();
        var meeting = Timed("Reunião", Utc(2026, 11, 10, 9), hours: 2, participants: [mom]);
        var call = Timed("Chamada", Utc(2026, 11, 10, 10), hours: 1, participants: [mom]);
        var lunch = Timed("Almoço", Utc(2026, 11, 10, 11), hours: 1, participants: [mom]);

        var conflicts = FindConflicts(meeting, call, lunch);

        Assert.Equal(mom, Assert.Single(conflicts[0]));
        Assert.Equal(mom, Assert.Single(conflicts[1]));
        Assert.Empty(conflicts[2]);
    }

    [Fact]
    public void Driver_who_is_busy_when_they_have_to_take_or_fetch_someone_is_a_conflict()
    {
        Guid mom = Guid.NewGuid(), dad = Guid.NewGuid(), kid = Guid.NewGuid();
        var meeting = Timed("Reunião", Utc(2026, 11, 10, 9), hours: 2, participants: [mom]);
        var football = Timed("Futebol", Utc(2026, 11, 10, 10), hours: 1.5, participants: [kid], dropOff: mom, pickUp: dad);
        var gym = Timed("Ginásio", Utc(2026, 11, 10, 11), hours: 1, participants: [dad]);
        var birthday = new CalendarEvent { Title = "Aniversário da avó", ParticipantUserIds = [mom, dad] };
        birthday.SetAllDay(new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 10));

        var conflicts = FindConflicts(meeting, football, gym, birthday);

        Assert.Empty(conflicts[0]);
        Assert.Equal(new[] { mom, dad }.Order(), conflicts[1].Order());
        Assert.Empty(conflicts[2]);
        Assert.Empty(conflicts[3]);
    }

    private static IReadOnlyList<IReadOnlySet<Guid>> FindConflicts(params CalendarEvent[] events) =>
        CalendarConflicts.Find([.. events.SelectMany(e => e.Occurrences(Utc(2026, 11, 1), Utc(2026, 12, 1), Lisbon))]);

    private static CalendarEvent Timed(
        string title,
        DateTimeOffset start,
        double hours,
        string? recurrence = null,
        List<Guid>? participants = null,
        Guid? dropOff = null,
        Guid? pickUp = null)
    {
        var calendarEvent = new CalendarEvent
        {
            Title = title,
            Recurrence = recurrence,
            ParticipantUserIds = participants ?? [],
            DropOffUserId = dropOff,
            PickUpUserId = pickUp,
        };
        calendarEvent.SetTimed(start, start.AddHours(hours));
        return calendarEvent;
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour = 0) =>
        new(year, month, day, hour, 0, 0, TimeSpan.Zero);
}
