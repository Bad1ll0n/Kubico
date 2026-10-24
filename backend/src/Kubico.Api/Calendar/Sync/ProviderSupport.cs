using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Kubico.Domain.Calendar;

namespace Kubico.Api.Calendar.Sync;

/// <summary>Pedidos HTTP às APIs de calendário, com os erros traduzidos para as exceções do sincronizador.</summary>
internal static class ProviderHttp
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Envia um pedido autenticado. Devolve o JSON da resposta, ou null quando não há corpo, ou quando
    /// <paramref name="allowMissing"/> e o recurso já não existe (404 ou 410).
    /// </summary>
    public static async Task<JsonElement?> SendAsync(
        HttpClient http,
        string service,
        HttpMethod method,
        string url,
        string accessToken,
        object? body,
        CancellationToken ct,
        bool allowMissing = false,
        Action<HttpRequestMessage>? configure = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: Json);
        }

        configure?.Invoke(request);
        using var response = await http.SendAsync(request, ct);
        if (allowMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            return null;
        }

        var text = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new CalendarAuthException($"O {service} recusou o token de acesso.");
        }

        if (!response.IsSuccessStatusCode)
        {
            var detail = text.Length > 300 ? text[..300] : text;
            throw new CalendarProviderException(
                $"O {service} respondeu {(int)response.StatusCode} a {method} {url}: {detail}", response.StatusCode);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static string Escape(string value) => Uri.EscapeDataString(value);

    /// <summary>Instante em UTC no formato que as APIs pedem nos filtros (2026-10-20T17:00:00Z).</summary>
    public static string Utc(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static string Day(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Hora local da casa, sem desvio (o fuso vai ao lado, no campo timeZone).</summary>
    public static string Local(DateTimeOffset instant, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(instant, zone).DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
}

internal static class JsonElementExtensions
{
    /// <summary>A propriedade, se existir e não for null.</summary>
    public static JsonElement? Prop(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public static string? Str(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    public static string? Str(this JsonElement? element, string name) => element?.Str(name);

    public static int? Int(this JsonElement? element, string name) =>
        element?.Prop(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var number)
            ? number
            : null;

    public static bool Bool(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.True };

    public static IEnumerable<JsonElement> Items(this JsonElement element, string name) =>
        element.Prop(name) is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static IEnumerable<JsonElement> Items(this JsonElement? element, string name) =>
        element is { } value ? value.Items(name) : [];
}

/// <summary>OAuth 2.0 com PKCE, igual no Google e na Microsoft.</summary>
internal static class OAuth
{
    public static string Url(string endpoint, IReadOnlyDictionary<string, string> query) =>
        endpoint + "?" + string.Join('&', query.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    public static async Task<OAuthTokens> RequestTokenAsync(
        HttpClient http, string service, string endpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await http.PostAsync(endpoint, new FormUrlEncodedContent(form), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement json;
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            json = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new CalendarProviderException($"O {service} devolveu uma resposta inválida ({(int)response.StatusCode}).");
        }

        if (!response.IsSuccessStatusCode)
        {
            // invalid_grant: o código ou o refresh token já não servem (acesso retirado, palavra-passe mudada).
            var error = json.Str("error");
            if (error is "invalid_grant" or "unauthorized_client" or "invalid_client" or "interaction_required")
            {
                throw new CalendarAuthException($"O {service} recusou a autorização ({error}).");
            }

            throw new CalendarProviderException(
                $"O {service} recusou o pedido de token ({(int)response.StatusCode} {error}).", response.StatusCode);
        }

        var accessToken = json.Str("access_token")
            ?? throw new CalendarProviderException($"O {service} não devolveu um token de acesso.");
        return new OAuthTokens(accessToken, json.Str("refresh_token"), EmailFromIdToken(json.Str("id_token")));
    }

    /// <summary>
    /// O email da conta, tirado do id_token. Veio diretamente do servidor OAuth por HTTPS, por isso não é preciso
    /// verificar a assinatura (só serve para mostrar qual conta foi ligada).
    /// </summary>
    public static string? EmailFromIdToken(string? idToken)
    {
        if (idToken?.Split('.') is not [_, var payload, ..])
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64Url.DecodeFromChars(payload));
            return document.RootElement.Str("email") ?? document.RootElement.Str("preferred_username");
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>O Google usa nomes de fuso IANA ("Europe/Lisbon"); o Outlook prefere os do Windows ("GMT Standard Time").</summary>
internal static class TimeZoneIds
{
    public static string Iana(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana : "UTC";

    public static string Windows(TimeZoneInfo zone) =>
        !zone.HasIanaId ? zone.Id : TimeZoneInfo.TryConvertIanaIdToWindowsId(zone.Id, out var windows) ? windows : "UTC";
}

/// <summary>
/// Repetições em iCalendar (RRULE e EXDATE, que o Google usa tal e qual) e o subconjunto suportado pelo Kubico.
/// </summary>
internal static class IcsRecurrence
{
    /// <summary>As linhas "recurrence" de um evento do Kubico; null se não se repetir.</summary>
    public static List<string>? ToLines(ExternalEventData data, TimeZoneInfo zone, string tzid)
    {
        if (!RecurrenceRule.TryParse(data.Recurrence, out var rule))
        {
            return null;
        }

        var lines = new List<string> { "RRULE:" + IcsWriter.RuleText(rule, data.IsAllDay, zone) };
        if (data.Cancelled.Count > 0)
        {
            if (data.IsAllDay)
            {
                lines.Add("EXDATE;VALUE=DATE:" + string.Join(',', data.Cancelled.Select(d => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture))));
            }
            else
            {
                // A ocorrência é identificada pela hora local do início da série, nesse dia.
                var time = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(data.StartsAt!.Value, zone).DateTime);
                lines.Add($"EXDATE;TZID={tzid}:" + string.Join(',', data.Cancelled.Select(
                    d => d.ToDateTime(time).ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture))));
            }
        }

        return lines;
    }

    /// <summary>Lê as linhas de repetição de um evento externo. RDATE, EXRULE e regras desconhecidas não são suportadas.</summary>
    public static (string? Rule, List<DateOnly> Cancelled, bool Unsupported) FromLines(
        IEnumerable<string> lines, DateOnly firstDate, TimeZoneInfo zone)
    {
        string? rule = null;
        var cancelled = new List<DateOnly>();
        var unsupported = false;
        foreach (var line in lines)
        {
            var colon = line.IndexOf(':');
            var name = colon < 0 ? "" : line[..colon].Split(';')[0].ToUpperInvariant();
            var value = colon < 0 ? "" : line[(colon + 1)..];
            switch (name)
            {
                case "RRULE" when rule is null:
                    rule = Normalize(value, firstDate, zone);
                    unsupported |= rule is null;
                    break;
                case "EXDATE":
                    cancelled.AddRange(value.Split(',').Select(v => ParseDate(v.Trim(), zone)).OfType<DateOnly>());
                    break;
                default:
                    unsupported = true;
                    break;
            }
        }

        return (rule, cancelled, unsupported);
    }

    /// <summary>
    /// Passa uma RRULE externa para a forma canónica do Kubico, tirando o que é redundante (BYMONTHDAY igual ao dia
    /// de início, WKST, BYDAY só com o dia de início). Null se tiver partes que o Kubico não suporta.
    /// </summary>
    public static string? Normalize(string text, DateOnly firstDate, TimeZoneInfo zone)
    {
        var pairs = new List<(string Key, string Value)>();
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = part.Split('=');
            if (pair.Length != 2)
            {
                return null;
            }

            pairs.Add((pair[0].ToUpperInvariant(), pair[1].ToUpperInvariant()));
        }

        var frequency = pairs.FirstOrDefault(p => p.Key == "FREQ").Value;
        string? weekStart = null;
        var kept = new List<string>();
        foreach (var (key, value) in pairs)
        {
            switch (key)
            {
                case "WKST":
                    weekStart = value;
                    continue;
                case "BYMONTHDAY" when frequency is "MONTHLY" or "YEARLY" && value == Number(firstDate.Day):
                case "BYMONTH" when frequency == "YEARLY" && value == Number(firstDate.Month):
                    continue;
                case "UNTIL" when value.Length == 16 && value.EndsWith('Z'):
                    // Instante UTC → o último dia, no fuso da casa.
                    if (!DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var until))
                    {
                        return null;
                    }

                    var lastDay = ProviderHttp.LocalDate(new DateTimeOffset(until, TimeSpan.Zero), zone);
                    kept.Add("UNTIL=" + lastDay.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
                    continue;
            }

            kept.Add($"{key}={value}");
        }

        if (!RecurrenceRule.TryParse(string.Join(';', kept), out var rule))
        {
            return null;
        }

        // A semana começar ao domingo só muda as regras de várias em várias semanas com vários dias.
        if (weekStart is not null and not "MO" && rule.Interval > 1 && rule.ByDay is { Count: > 1 })
        {
            return null;
        }

        if (rule.Frequency == RecurrenceFrequency.Weekly && rule.ByDay is [var only] && only == firstDate.DayOfWeek)
        {
            rule = rule with { ByDay = null };
        }

        return rule.ToString();
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Um valor de EXDATE: dia (20261020), instante UTC (20261020T170000Z) ou hora local (20261020T180000).</summary>
    private static DateOnly? ParseDate(string value, TimeZoneInfo zone)
    {
        if (!DateOnly.TryParseExact(value.Length >= 8 ? value[..8] : value, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date))
        {
            return null;
        }

        if (value.Length == 16 && value.EndsWith('Z')
            && DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
        {
            return ProviderHttp.LocalDate(new DateTimeOffset(instant, TimeSpan.Zero), zone);
        }

        return date;
    }
}
