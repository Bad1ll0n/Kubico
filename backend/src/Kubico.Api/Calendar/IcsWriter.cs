using System.Globalization;
using System.Text;
using Kubico.Api.Common;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;

namespace Kubico.Api.Calendar;

/// <summary>
/// Escreve os eventos de uma casa em iCalendar (RFC 5545), para subscrever no Google Calendar, Outlook ou Apple.
/// As horas vão no fuso da casa (com VTIMEZONE), para as repetições não mudarem de hora com a hora de verão.
/// </summary>
public sealed class IcsWriter
{
    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    private readonly Household _household;
    private readonly IReadOnlyDictionary<Guid, string> _names;
    private readonly TimeZoneInfo _zone;
    private readonly string _tzid;

    /// <param name="household">A casa, com o nome e o fuso.</param>
    /// <param name="names">Nome de cada membro, para os participantes e as boleias.</param>
    private readonly string _language;

    public IcsWriter(Household household, IReadOnlyDictionary<Guid, string> names, string language = Texts.Portuguese)
    {
        _language = language;
        _household = household;
        _names = names;
        _zone = household.TimeZone;
        _tzid = TimeZoneInfo.TryFindSystemTimeZoneById(household.TimeZoneId, out _) ? household.TimeZoneId : "UTC";
    }

    public string Write(IReadOnlyList<CalendarEvent> events, DateTimeOffset now)
    {
        var series = events.Where(e => e.SeriesId is null).ToDictionary(e => e.Id);
        var replaced = events
            .Where(e => e.SeriesId is not null)
            .Select(e => (e.SeriesId!.Value, e.OriginalDate!.Value))
            .ToHashSet();

        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//Kubico//Calendario partilhado//PT",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            $"X-WR-CALNAME:{Text(_household.Name)}",
            $"X-WR-TIMEZONE:{_tzid}",
        };

        if (_tzid != "UTC")
        {
            lines.AddRange(TimeZoneComponent(_zone, _tzid, now.Year));
        }

        foreach (var calendarEvent in events)
        {
            // Uma exceção cuja série já não existe não tem a que se ligar.
            var parent = calendarEvent.SeriesId is { } seriesId ? series.GetValueOrDefault(seriesId) : null;
            if (calendarEvent.SeriesId is null || parent is not null)
            {
                lines.AddRange(Event(calendarEvent, parent, replaced));
            }
        }

        lines.Add("END:VCALENDAR");

        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(Fold(line)).Append("\r\n");
        }

        return builder.ToString();
    }

    private IEnumerable<string> Event(CalendarEvent e, CalendarEvent? parent, HashSet<(Guid, DateOnly)> replaced)
    {
        yield return "BEGIN:VEVENT";

        // As exceções partilham o UID da série e dizem que ocorrência substituem (RECURRENCE-ID).
        yield return $"UID:{e.SeriesId ?? e.Id}@kubico";
        yield return $"DTSTAMP:{Utc(e.UpdatedAt)}";
        yield return $"LAST-MODIFIED:{Utc(e.UpdatedAt)}";

        if (e.IsAllDay)
        {
            yield return $"DTSTART;VALUE=DATE:{Date(e.StartDate!.Value)}";
            yield return $"DTEND;VALUE=DATE:{Date(e.EndDate!.Value.AddDays(1))}";
        }
        else
        {
            yield return Timed("DTSTART", [Local(e.StartsAt!.Value)]);
            yield return Timed("DTEND", [Local(e.EndsAt!.Value)]);
        }

        if (parent is not null)
        {
            yield return Occurrences("RECURRENCE-ID", parent, [e.OriginalDate!.Value]);
        }

        if (RecurrenceRule.TryParse(e.Recurrence, out var rule))
        {
            yield return $"RRULE:{RuleText(rule, e.IsAllDay, _zone)}";

            // Os dias substituídos por uma exceção não vão no EXDATE: a exceção ocupa o lugar deles.
            var excluded = e.ExcludedDates.Where(d => !replaced.Contains((e.Id, d))).Order().ToList();
            if (excluded.Count > 0)
            {
                yield return Occurrences("EXDATE", e, excluded);
            }
        }

        yield return $"SUMMARY:{Text(e.Title)}";
        if (e.Location is { } location)
        {
            yield return $"LOCATION:{Text(location)}";
        }

        if (Description(e) is { Length: > 0 } description)
        {
            yield return $"DESCRIPTION:{Text(description)}";
        }

        yield return "END:VEVENT";
    }

    /// <summary>Com hora marcada, a RFC 5545 pede o UNTIL em UTC: o fim do último dia, no fuso da casa.</summary>
    public static string RuleText(RecurrenceRule rule, bool isAllDay, TimeZoneInfo zone)
    {
        if (isAllDay || rule.Until is not { } until)
        {
            return rule.ToString();
        }

        var withoutUntil = rule with { Until = null };
        var end = ZonedTime.StartOfDay(until.AddDays(1), zone).AddSeconds(-1);
        return $"{withoutUntil};UNTIL={Utc(end)}";
    }

    /// <summary>Ocorrências de uma série (EXDATE, RECURRENCE-ID), identificadas pelo dia e pela hora da série.</summary>
    private string Occurrences(string property, CalendarEvent series, IEnumerable<DateOnly> dates)
    {
        if (series.IsAllDay)
        {
            return $"{property};VALUE=DATE:{string.Join(',', dates.Select(Date))}";
        }

        var time = TimeOnly.FromDateTime(Local(series.StartsAt!.Value));
        return Timed(property, [.. dates.Select(d => d.ToDateTime(time))]);
    }

    private string Timed(string property, IReadOnlyList<DateTime> localTimes)
    {
        const string format = "yyyyMMdd'T'HHmmss";
        var values = string.Join(',', localTimes.Select(t => t.ToString(format, CultureInfo.InvariantCulture)));
        return _tzid == "UTC"
            ? $"{property}:{string.Join(',', localTimes.Select(t => t.ToString(format + "'Z'", CultureInfo.InvariantCulture)))}"
            : $"{property};TZID={_tzid}:{values}";
    }

    private string Description(CalendarEvent e)
    {
        var lines = new List<string>();
        if (e.Notes is { } notes)
        {
            lines.Add(notes);
        }

        if (e.ParticipantUserIds.Count > 0)
        {
            lines.Add(Texts.Format(_language, "Participantes: {0}", string.Join(", ", e.ParticipantUserIds.Select(Name))));
        }

        if (e.DropOffUserId is { } dropOff)
        {
            lines.Add(Texts.Format(_language, "Leva: {0}", Name(dropOff)));
        }

        if (e.PickUpUserId is { } pickUp)
        {
            lines.Add(Texts.Format(_language, "Vai buscar: {0}", Name(pickUp)));
        }

        return string.Join("\n", lines);
    }

    private string Name(Guid userId) => _names.GetValueOrDefault(userId, "?");

    private DateTime Local(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, _zone).DateTime;

    private static string Utc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    private static string Date(DateOnly date) => date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// Texto iCalendar: barras, pontos e vírgulas, vírgulas e mudanças de linha são escapados (também um CR sozinho,
    /// que algumas apps leem como fim de linha); os outros caracteres de controlo saem.
    /// </summary>
    public static string Text(string value) => new string(value
        .Replace("\\", "\\\\")
        .Replace(";", "\\;")
        .Replace(",", "\\,")
        .Replace("\r\n", "\\n")
        .Replace("\n", "\\n")
        .Replace("\r", "\\n")
        .Where(c => !char.IsControl(c))
        .ToArray());

    /// <summary>Parte as linhas com mais de 75 bytes (RFC 5545 §3.1), sem cortar caracteres a meio.</summary>
    public static string Fold(string line)
    {
        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 75)
            {
                builder.Append("\r\n ");
                bytes = 1;
            }

            builder.Append(rune.ToString());
            bytes += rune.Utf8SequenceLength;
        }

        return builder.ToString();
    }

    /// <summary>
    /// VTIMEZONE com as mudanças de hora do fuso, descritas como regras anuais a partir das mudanças de
    /// <paramref name="year"/> (por exemplo, o último domingo de março e o de outubro na Europa).
    /// </summary>
    public static IEnumerable<string> TimeZoneComponent(TimeZoneInfo zone, string tzid, int year)
    {
        yield return "BEGIN:VTIMEZONE";
        yield return $"TZID:{tzid}";

        var transitions = Transitions(zone, year);
        if (transitions.Count == 0)
        {
            var offset = Offset(zone.GetUtcOffset(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            yield return "BEGIN:STANDARD";
            yield return "DTSTART:19700101T000000";
            yield return $"TZOFFSETFROM:{offset}";
            yield return $"TZOFFSETTO:{offset}";
            yield return "END:STANDARD";
        }

        foreach (var (local, from, to) in transitions)
        {
            var kind = to > from ? "DAYLIGHT" : "STANDARD";
            var ordinal = local.Day + 7 > DateTime.DaysInMonth(local.Year, local.Month) ? -1 : (local.Day - 1) / 7 + 1;
            var first = NthWeekday(1970, local.Month, local.DayOfWeek, ordinal).ToDateTime(TimeOnly.FromDateTime(local));
            yield return $"BEGIN:{kind}";
            yield return "DTSTART:" + first.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
            yield return FormattableString.Invariant(
                $"RRULE:FREQ=YEARLY;BYMONTH={local.Month};BYDAY={ordinal}{DayCodes[(int)local.DayOfWeek]}");
            yield return $"TZOFFSETFROM:{Offset(from)}";
            yield return $"TZOFFSETTO:{Offset(to)}";
            yield return $"END:{kind}";
        }

        yield return "END:VTIMEZONE";
    }

    /// <summary>As mudanças de hora de um ano: a hora local (antes da mudança) e os desvios antes e depois.</summary>
    private static List<(DateTime Local, TimeSpan From, TimeSpan To)> Transitions(TimeZoneInfo zone, int year)
    {
        var result = new List<(DateTime, TimeSpan, TimeSpan)>();
        var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var offset = zone.GetUtcOffset(start);

        // As mudanças acontecem à hora certa ou à meia hora; t é o primeiro instante com o desvio novo.
        for (var t = start.AddMinutes(30); t <= start.AddYears(1); t = t.AddMinutes(30))
        {
            var next = zone.GetUtcOffset(t);
            if (next != offset)
            {
                result.Add((DateTime.SpecifyKind(t + offset, DateTimeKind.Unspecified), offset, next));
                offset = next;
            }
        }

        return result;
    }

    /// <summary>O n-ésimo dia da semana do mês (ordinal -1 = o último).</summary>
    private static DateOnly NthWeekday(int year, int month, DayOfWeek day, int ordinal)
    {
        if (ordinal > 0)
        {
            var first = new DateOnly(year, month, 1);
            return first.AddDays(((int)day - (int)first.DayOfWeek + 7) % 7 + 7 * (ordinal - 1));
        }

        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return last.AddDays(-(((int)last.DayOfWeek - (int)day + 7) % 7));
    }

    private static string Offset(TimeSpan offset)
    {
        var sign = offset < TimeSpan.Zero ? "-" : "+";
        var abs = offset.Duration();
        return FormattableString.Invariant($"{sign}{abs.Hours:00}{abs.Minutes:00}");
    }
}
