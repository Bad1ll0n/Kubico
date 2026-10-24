using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Kubico.Domain.Calendar;

public enum RecurrenceFrequency
{
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>
/// O subconjunto de RRULE (RFC 5545) que a app usa: FREQ, INTERVAL, BYDAY (só semanal), COUNT e UNTIL.
/// Guarda-se como texto para simplificar o sync com calendários externos e a exportação ICS.
/// </summary>
public sealed record RecurrenceRule(
    RecurrenceFrequency Frequency,
    int Interval = 1,
    IReadOnlyList<DayOfWeek>? ByDay = null,
    int? Count = null,
    DateOnly? Until = null)
{
    /// <summary>Limite de períodos percorridos, para uma regra nunca gerar datas sem fim.</summary>
    private const int MaxPeriods = 20_000;

    private static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    /// <summary>Lê uma RRULE (com ou sem o prefixo "RRULE:"). Partes desconhecidas tornam a regra inválida.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out RecurrenceRule? rule)
    {
        rule = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim();
        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            text = text["RRULE:".Length..];
        }

        RecurrenceFrequency? frequency = null;
        var interval = 1;
        List<DayOfWeek>? byDay = null;
        int? count = null;
        DateOnly? until = null;

        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=');
            if (pair.Length != 2)
            {
                return false;
            }

            var value = pair[1].ToUpperInvariant();
            switch (pair[0].ToUpperInvariant())
            {
                case "FREQ" when frequency is null && TryParseFrequency(value, out var parsed):
                    frequency = parsed;
                    break;
                case "INTERVAL" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 99:
                    interval = n;
                    break;
                case "BYDAY" when byDay is null && TryParseDays(value, out var days):
                    byDay = days;
                    break;
                case "COUNT" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n is >= 1 and <= 1000:
                    count = n;
                    break;
                case "UNTIL" when TryParseUntil(value, out var date):
                    until = date;
                    break;
                default:
                    return false;
            }
        }

        // COUNT e UNTIL não podem coexistir (RFC 5545); BYDAY só é suportado em regras semanais.
        if (frequency is null || (count is not null && until is not null)
            || (byDay is not null && frequency != RecurrenceFrequency.Weekly))
        {
            return false;
        }

        rule = new RecurrenceRule(frequency.Value, interval, byDay, count, until);
        return true;
    }

    /// <summary>Forma canónica, sempre pela mesma ordem (ex.: "FREQ=WEEKLY;INTERVAL=2;BYDAY=MO,WE").</summary>
    public override string ToString()
    {
        var parts = new List<string> { $"FREQ={Frequency.ToString().ToUpperInvariant()}" };
        if (Interval != 1)
        {
            parts.Add(FormattableString.Invariant($"INTERVAL={Interval}"));
        }

        if (ByDay is { Count: > 0 })
        {
            parts.Add("BYDAY=" + string.Join(',', ByDay.Select(d => DayCodes[(int)d])));
        }

        if (Count is { } count)
        {
            parts.Add(FormattableString.Invariant($"COUNT={count}"));
        }

        if (Until is { } until)
        {
            parts.Add("UNTIL=" + until.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        return string.Join(';', parts);
    }

    /// <summary>
    /// Datas das ocorrências por ordem, a começar em <paramref name="start"/>, que conta sempre como a primeira
    /// (RFC 5545). O UNTIL é inclusivo; quem consome pára quando já passou o intervalo que lhe interessa.
    /// </summary>
    public IEnumerable<DateOnly> Dates(DateOnly start)
    {
        var emitted = 0;
        foreach (var date in Candidates(start))
        {
            if (emitted > 0 && Until is { } until && date > until)
            {
                yield break;
            }

            yield return date;
            emitted++;
            if (Count is { } count && emitted >= count)
            {
                yield break;
            }
        }
    }

    /// <summary>
    /// As mesmas datas que <see cref="Dates(DateOnly)"/>, sem as anteriores a <paramref name="notBefore"/>. Nas
    /// regras diárias e semanais sem COUNT salta logo para o primeiro período que interessa, em vez de percorrer
    /// todos desde o início (uma série diária com 3 anos tinha mais de mil dias para trás).
    /// </summary>
    public IEnumerable<DateOnly> Dates(DateOnly start, DateOnly notBefore)
    {
        if (notBefore <= start)
        {
            return Dates(start);
        }

        // Com COUNT conta-se desde o início (no máximo 1000); mensais e anuais têm poucos períodos por ano.
        if (Count is not null || Frequency is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly)
        {
            return Dates(start).SkipWhile(d => d < notBefore);
        }

        return DatesFrom(start, notBefore);
    }

    /// <summary>
    /// Diárias e semanais sem COUNT, a partir de <paramref name="notBefore"/> (&gt; <paramref name="start"/>): a
    /// primeira data (o início) já ficou para trás, por isso o UNTIL aplica-se a todas, como em <see cref="Dates(DateOnly)"/>.
    /// Os limites (<see cref="MaxPeriods"/>, <see cref="MaxYear"/>) são os mesmos de <see cref="Candidates"/>.
    /// </summary>
    private IEnumerable<DateOnly> DatesFrom(DateOnly start, DateOnly notBefore)
    {
        if (Frequency == RecurrenceFrequency.Daily || ByDay is not { Count: > 0 })
        {
            var step = Frequency == RecurrenceFrequency.Daily ? Interval : 7 * Interval;
            var first = Math.Max(1, (notBefore.DayNumber - start.DayNumber + step - 1) / step);
            for (var k = first; k <= MaxPeriods && CanAddDays(start, k * step); k++)
            {
                var date = start.AddDays(k * step);
                if (Until is { } until && date > until)
                {
                    yield break;
                }

                yield return date;
            }

            yield break;
        }

        var days = ByDay.Distinct().OrderBy(MondayIndex).ToList();
        var weekStart = start.AddDays(-MondayIndex(start.DayOfWeek));

        // Começa uma semana antes da que tem notBefore (margem); as datas anteriores filtram-se a seguir.
        var firstWeek = Math.Max(0, (notBefore.DayNumber - weekStart.DayNumber) / (7 * Interval) - 1);
        for (var k = firstWeek; k <= MaxPeriods && CanAddDays(weekStart, 7 * k * Interval + 6); k++)
        {
            var week = weekStart.AddDays(7 * k * Interval);
            foreach (var day in days)
            {
                var date = week.AddDays(MondayIndex(day));
                if (date <= start || date < notBefore)
                {
                    continue;
                }

                if (Until is { } until && date > until)
                {
                    yield break;
                }

                yield return date;
            }
        }
    }

    private IEnumerable<DateOnly> Candidates(DateOnly start)
    {
        yield return start;

        switch (Frequency)
        {
            case RecurrenceFrequency.Daily:
            case RecurrenceFrequency.Weekly when ByDay is not { Count: > 0 }:
                var step = Frequency == RecurrenceFrequency.Daily ? Interval : 7 * Interval;
                for (var k = 1; k <= MaxPeriods && CanAddDays(start, k * step); k++)
                {
                    yield return start.AddDays(k * step);
                }

                break;

            case RecurrenceFrequency.Weekly:
                // Semanas começam à segunda-feira (WKST=MO, o valor por omissão).
                var days = ByDay!.Distinct().OrderBy(MondayIndex).ToList();
                var weekStart = start.AddDays(-MondayIndex(start.DayOfWeek));
                for (var k = 0; k <= MaxPeriods && CanAddDays(weekStart, 7 * k * Interval + 6); k++)
                {
                    var week = weekStart.AddDays(7 * k * Interval);
                    foreach (var day in days)
                    {
                        var date = week.AddDays(MondayIndex(day));
                        if (date > start)
                        {
                            yield return date;
                        }
                    }
                }

                break;

            case RecurrenceFrequency.Monthly:
                // Meses sem esse dia (31 de abril, 30 de fevereiro) são saltados, como na RFC 5545.
                for (var k = 1; k <= MaxPeriods; k++)
                {
                    var monthIndex = start.Year * 12 + start.Month - 1 + k * Interval;
                    var (year, month) = (monthIndex / 12, monthIndex % 12 + 1);
                    if (year > MaxYear)
                    {
                        yield break;
                    }

                    if (start.Day <= DateTime.DaysInMonth(year, month))
                    {
                        yield return new DateOnly(year, month, start.Day);
                    }
                }

                break;

            case RecurrenceFrequency.Yearly:
                for (var k = 1; k <= MaxPeriods; k++)
                {
                    var year = start.Year + k * Interval;
                    if (year > MaxYear)
                    {
                        yield break;
                    }

                    // 29 de fevereiro só nos anos bissextos.
                    if (start.Day <= DateTime.DaysInMonth(year, start.Month))
                    {
                        yield return new DateOnly(year, start.Month, start.Day);
                    }
                }

                break;
        }
    }

    private const int MaxYear = 9998;

    private static bool CanAddDays(DateOnly date, int days) =>
        (long)date.DayNumber + days < new DateOnly(MaxYear, 12, 31).DayNumber;

    private static int MondayIndex(DayOfWeek day) => ((int)day + 6) % 7;

    private static bool TryParseFrequency(string value, out RecurrenceFrequency frequency)
    {
        frequency = default;
        return value is "DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY"
            && Enum.TryParse(value, ignoreCase: true, out frequency);
    }

    private static bool TryParseDays(string value, [NotNullWhen(true)] out List<DayOfWeek>? days)
    {
        days = [];
        foreach (var code in value.Split(','))
        {
            var index = Array.IndexOf(DayCodes, code);
            if (index < 0)
            {
                days = null;
                return false;
            }

            days.Add((DayOfWeek)index);
        }

        days = [.. days.Distinct().OrderBy(MondayIndex)];
        return true;
    }

    /// <summary>Aceita a forma de data (20261231) e a de instante UTC (20261231T235959Z); fica só o dia.</summary>
    private static bool TryParseUntil(string value, out DateOnly date)
    {
        var isInstant = value.Length == 16 && value[8] == 'T' && value[15] == 'Z';
        return DateOnly.TryParseExact(
            isInstant ? value[..8] : value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}
