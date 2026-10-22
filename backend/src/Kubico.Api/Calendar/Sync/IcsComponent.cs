using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Kubico.Domain.Calendar;

namespace Kubico.Api.Calendar.Sync;

/// <summary>Uma propriedade iCalendar: o nome, os parâmetros (TZID, VALUE…) e o valor, como vêm no texto.</summary>
internal sealed record IcsProperty(string Name, IReadOnlyDictionary<string, string> Parameters, string Value)
{
    public string? Parameter(string name) => Parameters.GetValueOrDefault(name);

    public static IcsProperty Parse(string line)
    {
        // O valor começa no primeiro ":" fora de aspas (os parâmetros podem ter ":" entre aspas).
        var quoted = false;
        var colon = -1;
        for (var i = 0; i < line.Length && colon < 0; i++)
        {
            if (line[i] == '"')
            {
                quoted = !quoted;
            }
            else if (line[i] == ':' && !quoted)
            {
                colon = i;
            }
        }

        var head = colon < 0 ? line : line[..colon];
        var parts = head.Split(';');
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1))
        {
            var equals = part.IndexOf('=');
            if (equals > 0)
            {
                parameters[part[..equals]] = part[(equals + 1)..].Trim('"');
            }
        }

        return new IcsProperty(parts[0].ToUpperInvariant(), parameters, colon < 0 ? "" : line[(colon + 1)..]);
    }
}

/// <summary>
/// Um componente iCalendar (VCALENDAR, VEVENT, VTIMEZONE, VALARM…) com as linhas já desdobradas e os
/// componentes que tem dentro. Serve para ler um recurso CalDAV e voltar a escrevê-lo só com o que mudou.
/// </summary>
internal sealed class IcsComponent(string name)
{
    public string Name { get; } = name;

    public List<string> Lines { get; } = [];

    public List<IcsComponent> Children { get; } = [];

    public IEnumerable<IcsProperty> Properties => Lines.Select(IcsProperty.Parse);

    public IcsProperty? Property(string name) => Properties.FirstOrDefault(p => p.Name == name);

    public IEnumerable<IcsComponent> Events => Children.Where(c => c.Name == "VEVENT");

    /// <summary>O texto de um calendário (RFC 5545), com as linhas dobradas desfeitas.</summary>
    public static IcsComponent Parse(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Length > 0 && (raw[0] == ' ' || raw[0] == '\t') && lines.Count > 0)
            {
                lines[^1] += raw[1..];
            }
            else if (raw.Length > 0)
            {
                lines.Add(raw);
            }
        }

        var root = new IcsComponent("ROOT");
        var stack = new Stack<IcsComponent>([root]);
        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase))
            {
                var child = new IcsComponent(line[6..].Trim().ToUpperInvariant());
                stack.Peek().Children.Add(child);
                stack.Push(child);
            }
            else if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (stack.Count > 1)
                {
                    stack.Pop();
                }
            }
            else
            {
                stack.Peek().Lines.Add(line);
            }
        }

        return root.Children.FirstOrDefault(c => c.Name == "VCALENDAR") ?? root;
    }

    public string Write()
    {
        var builder = new StringBuilder();
        WriteTo(builder);
        return builder.ToString();
    }

    private void WriteTo(StringBuilder builder)
    {
        builder.Append("BEGIN:").Append(Name).Append("\r\n");
        foreach (var line in Lines)
        {
            builder.Append(IcsWriter.Fold(line)).Append("\r\n");
        }

        foreach (var child in Children)
        {
            child.WriteTo(builder);
        }

        builder.Append("END:").Append(Name).Append("\r\n");
    }

    /// <summary>Muda só quando o conteúdo muda (o DTSTAMP, que é a hora em que o texto foi gerado, não conta).</summary>
    public string Version()
    {
        var text = string.Join("\n", Lines.Where(l => !l.StartsWith("DTSTAMP", StringComparison.OrdinalIgnoreCase)))
            + string.Concat(Children.Select(c => "\n" + c.Write()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
    }
}

/// <summary>Os valores iCalendar que o Kubico lê e escreve: texto, dias e horas.</summary>
internal static class IcsValues
{
    public static string? Text(IcsProperty? property)
    {
        if (property is null || property.Value.Length == 0)
        {
            return null;
        }

        var builder = new StringBuilder();
        var value = property.Value;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length)
            {
                var next = value[++i];
                builder.Append(next is 'n' or 'N' ? '\n' : next);
            }
            else
            {
                builder.Append(value[i]);
            }
        }

        return builder.ToString();
    }

    public static bool IsDate(IcsProperty property) =>
        string.Equals(property.Parameter("VALUE"), "DATE", StringComparison.OrdinalIgnoreCase) || property.Value.Length == 8;

    public static DateOnly? Date(string value) =>
        DateOnly.TryParseExact(value.Length >= 8 ? value[..8] : value, "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var date) ? date : null;

    /// <summary>Uma hora: em UTC (…Z), no fuso indicado (TZID) ou, sem nenhum, no fuso da casa.</summary>
    public static DateTimeOffset? Instant(IcsProperty property, TimeZoneInfo householdZone)
    {
        var value = property.Value.Trim();
        if (value.EndsWith('Z')
            && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
        {
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }

        if (!DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        return ZonedTime.ToInstant(local, Zone(property.Parameter("TZID"), householdZone));
    }

    /// <summary>O dia (no fuso da casa) de uma data ou hora.</summary>
    public static DateOnly? LocalDate(IcsProperty property, TimeZoneInfo householdZone)
    {
        if (IsDate(property))
        {
            return Date(property.Value);
        }

        if (property.Parameter("TZID") is not null || !property.Value.EndsWith('Z'))
        {
            return Date(property.Value);
        }

        return Instant(property, householdZone) is { } instant ? ProviderHttp.LocalDate(instant, householdZone) : null;
    }

    /// <summary>Uma duração (P1D, PT1H30M, P1W…).</summary>
    public static TimeSpan? Duration(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var negative = value.StartsWith('-');
        var text = value.TrimStart('+', '-');
        if (!text.StartsWith('P'))
        {
            return null;
        }

        var total = TimeSpan.Zero;
        var number = 0;
        var inTime = false;
        foreach (var c in text[1..])
        {
            if (char.IsDigit(c))
            {
                number = number * 10 + (c - '0');
                continue;
            }

            total += (c, inTime) switch
            {
                ('T', _) => TimeSpan.Zero,
                ('W', false) => TimeSpan.FromDays(7 * number),
                ('D', false) => TimeSpan.FromDays(number),
                ('H', true) => TimeSpan.FromHours(number),
                ('M', true) => TimeSpan.FromMinutes(number),
                ('S', true) => TimeSpan.FromSeconds(number),
                _ => TimeSpan.Zero,
            };
            inTime |= c == 'T';
            number = 0;
        }

        return negative ? -total : total;
    }

    private static TimeZoneInfo Zone(string? tzid, TimeZoneInfo fallback) =>
        tzid is not null && TimeZoneInfo.TryFindSystemTimeZoneById(tzid, out var zone) ? zone : fallback;
}
