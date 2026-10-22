using System.Net;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace Kubico.Api.Tests;

/// <summary>
/// Um servidor CalDAV em memória que responde como o iCloud: a conta (principal) no endereço inicial, os
/// calendários noutro servidor, cada evento num recurso .ics e a lista completa em REPORT calendar-query.
/// </summary>
public sealed partial class FakeCalDavServer : HttpMessageHandler
{
    public const string Account = "ana@icloud.com";
    public const string Password = "abcd-efgh-ijkl-mnop";
    public static readonly Uri Root = new("https://caldav.test/");
    private const string Home = "https://p01-caldav.test:443/123/calendars/";

    private readonly Lock _lock = new();

    /// <summary>Cada calendário (pelo caminho da coleção): o nome e os recursos (caminho → texto iCalendar).</summary>
    public Dictionary<string, (string Name, Dictionary<string, string> Resources)> Calendars { get; } = [];

    public List<string> Requests { get; } = [];

    public (string Path, Dictionary<string, string> Resources) CalendarNamed(string name)
    {
        lock (_lock)
        {
            var (path, calendar) = Calendars.Single(c => c.Value.Name == name);
            return (path, calendar.Resources);
        }
    }

    /// <summary>O recurso cujo texto tem <paramref name="text"/> (por exemplo, o SUMMARY).</summary>
    public KeyValuePair<string, string> Resource(string calendarName, string text)
    {
        lock (_lock)
        {
            return CalendarNamed(calendarName).Resources.Single(r => r.Value.Contains(text, StringComparison.Ordinal));
        }
    }

    /// <summary>Uma alteração feita no iPhone ou no Mac.</summary>
    public void Edit(string calendarName, string path, Func<string, string> change)
    {
        lock (_lock)
        {
            var resources = CalendarNamed(calendarName).Resources;
            resources[path] = change(resources[path]);
        }
    }

    public void Add(string calendarName, string name, string ics)
    {
        lock (_lock)
        {
            var (path, resources) = CalendarNamed(calendarName);
            resources[path + name] = ics;
        }
    }

    public void Remove(string calendarName, string path)
    {
        lock (_lock)
        {
            CalendarNamed(calendarName).Resources.Remove(path);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (_lock)
        {
            return Handle(request, body);
        }
    }

    private HttpResponseMessage Handle(HttpRequestMessage request, string body)
    {
        var url = request.RequestUri!;
        var path = Uri.UnescapeDataString(url.AbsolutePath);
        Requests.Add($"{request.Method} {url.Host}{path}");
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Account}:{Password}"));
        if (request.Headers.Authorization is not { Scheme: "Basic" } auth || auth.Parameter != expected)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        var collection = path[..(path.TrimEnd('/').LastIndexOf('/') + 1)];
        switch (request.Method.Method)
        {
            case "PROPFIND" when url.Host == Root.Host && path == "/":
                return Multistatus("""
                    <response><href>/</href><propstat><prop>
                    <current-user-principal><href>/123/principal/</href></current-user-principal>
                    </prop><status>HTTP/1.1 200 OK</status></propstat></response>
                    """);
            case "PROPFIND" when path == "/123/principal/":
                return Multistatus($"""
                    <response><href>/123/principal/</href><propstat><prop>
                    <calendar-home-set xmlns="urn:ietf:params:xml:ns:caldav"><href xmlns="DAV:">{Home}</href></calendar-home-set>
                    </prop><status>HTTP/1.1 200 OK</status></propstat></response>
                    """);
            case "MKCALENDAR" when path.StartsWith("/123/calendars/", StringComparison.Ordinal) && !Calendars.ContainsKey(path):
                Calendars[path] = (DisplayName().Match(body).Groups[1].Value, []);
                return new HttpResponseMessage(HttpStatusCode.Created);
            case "REPORT" when Calendars.TryGetValue(path, out var calendar):
                return Multistatus(string.Concat(calendar.Resources.Select(r => $"""
                    <response><href>{r.Key}</href><propstat><prop>
                    <getetag>"{ETag(r.Value)}"</getetag>
                    <C:calendar-data xmlns:C="urn:ietf:params:xml:ns:caldav">{SecurityElement.Escape(r.Value)}</C:calendar-data>
                    </prop><status>HTTP/1.1 200 OK</status></propstat></response>
                    """)));
            case "PUT" when Calendars.TryGetValue(collection, out var calendar):
                if (request.Headers.TryGetValues("If-None-Match", out _) && calendar.Resources.ContainsKey(path))
                {
                    return new HttpResponseMessage(HttpStatusCode.PreconditionFailed);
                }

                var created = !calendar.Resources.ContainsKey(path);
                calendar.Resources[path] = body;
                var put = new HttpResponseMessage(created ? HttpStatusCode.Created : HttpStatusCode.NoContent);
                put.Headers.ETag = new($"\"{ETag(body)}\"");
                return put;
            case "GET" when Calendars.TryGetValue(collection, out var calendar) && calendar.Resources.TryGetValue(path, out var ics):
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ics, Encoding.UTF8, "text/calendar") };
            case "DELETE" when Calendars.Remove(path):
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            case "DELETE" when Calendars.TryGetValue(collection, out var calendar) && calendar.Resources.Remove(path):
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            default:
                return new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }

    private static HttpResponseMessage Multistatus(string responses) => new((HttpStatusCode)207)
    {
        Content = new StringContent($"""<?xml version="1.0" encoding="UTF-8"?><multistatus xmlns="DAV:">{responses}</multistatus>""",
            Encoding.UTF8, "application/xml"),
    };

    private static string ETag(string text) => text.GetHashCode(StringComparison.Ordinal).ToString("x8");

    [GeneratedRegex("<(?:\\w+:)?displayname>([^<]*)</(?:\\w+:)?displayname>")]
    private static partial Regex DisplayName();
}
