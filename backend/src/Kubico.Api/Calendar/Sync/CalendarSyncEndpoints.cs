using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Calendar.Sync;

/// <summary>A ligação de quem pede a uma conta Google ou Microsoft. <c>LastError</c> está pronto a mostrar.</summary>
public record CalendarConnectionDto(
    Guid Id,
    Guid HouseholdId,
    CalendarProvider Provider,
    string AccountEmail,
    CalendarConnectionStatus Status,
    DateTimeOffset? LastSyncedAt,
    string? LastError);

/// <summary>Um serviço de calendário e se este servidor tem credenciais para ele.</summary>
public record CalendarSyncProviderDto(CalendarProvider Provider, bool Available);

/// <summary><c>CanConnect</c>: só quem gere o calendário da casa liga contas (o que mudar lá muda no Kubico).</summary>
public record CalendarSyncDto(
    bool CanConnect, IReadOnlyList<CalendarSyncProviderDto> Providers, IReadOnlyList<CalendarConnectionDto> Connections);

/// <summary>A página onde a pessoa autoriza o Kubico; a app abre-a no navegador.</summary>
public record CalendarAuthorizationDto(string Url);

/// <summary>
/// Ligar o calendário da casa ao Google Calendar ou ao Outlook de cada membro, nos dois sentidos. A ligação é
/// pessoal: cada um liga a sua conta e só vê as suas ligações.
/// </summary>
public static class CalendarSyncEndpoints
{
    /// <summary>Quanto tempo há para autorizar na página do Google ou da Microsoft.</summary>
    private static readonly TimeSpan AuthorizationLifetime = TimeSpan.FromMinutes(15);

    public static RouteGroupBuilder MapCalendarSyncEndpoints(this RouteGroupBuilder api)
    {
        var sync = api.MapGroup("/households/{householdId:guid}/calendar-sync")
            .WithTags("Calendar")
            .RequireAuthorization();

        sync.MapGet("/", GetOverview);
        sync.MapPost("/{provider}/authorize", Authorize);
        sync.MapPost("/{connectionId:guid}/sync", SyncNow);
        sync.MapDelete("/{connectionId:guid}", Disconnect);

        // O navegador volta aqui depois de autorizar: quem é e de que casa vem no estado cifrado, não num token.
        api.MapGet("/calendar-sync/callback/{provider}", Callback)
            .WithTags("Calendar")
            .AllowAnonymous()
            .ExcludeFromDescription();

        return api;
    }

    private static async Task<IResult> GetOverview(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        CalendarSyncService sync,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(
            userId, householdId, HouseholdModule.Calendar, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var connections = await db.CalendarConnections.AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.UserId == userId)
            .ToListAsync(ct);

        return Results.Ok(new CalendarSyncDto(
            level >= ModuleAccess.Manage,
            [.. Enum.GetValues<CalendarProvider>().Select(p => new CalendarSyncProviderDto(p, sync.Provider(p)?.IsConfigured == true))],
            [.. connections.OrderBy(c => c.Provider).Select(ToDto)]));
    }

    /// <summary>Começa a ligação: devolve o endereço da página de autorização (OAuth com PKCE).</summary>
    private static async Task<IResult> Authorize(
        Guid householdId,
        string provider,
        ClaimsPrincipal principal,
        HttpRequest http,
        IConfiguration configuration,
        HouseholdAccess access,
        CalendarSyncService sync,
        CalendarSecrets secrets,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(
            userId, householdId, HouseholdModule.Calendar, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        if (ParseProvider(provider) is not { } kind || sync.Provider(kind) is not { } service)
        {
            return Results.NotFound();
        }

        if (!service.IsConfigured)
        {
            return Results.Problem(
                title: $"A ligação ao {ServiceName(kind)} ainda não está configurada neste servidor.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var (verifier, challenge) = CalendarSecrets.NewPkce();
        var state = secrets.ProtectState(
            new OAuthState(userId, householdId, kind, verifier, clock.GetUtcNow() + AuthorizationLifetime));
        var url = service.AuthorizationUrl(RedirectUri(http, configuration, kind), state, challenge);
        return Results.Ok(new CalendarAuthorizationDto(url));
    }

    /// <summary>
    /// O regresso da página de autorização: troca o código por tokens, cria o calendário do Kubico na conta (ou
    /// reaproveita o da ligação anterior à mesma conta) e pede a primeira sincronização.
    /// </summary>
    private static async Task<IResult> Callback(
        string provider,
        string? code,
        string? state,
        string? error,
        HttpRequest http,
        IConfiguration configuration,
        KubicoDbContext db,
        HouseholdAccess access,
        CalendarSyncService sync,
        CalendarSecrets secrets,
        CalendarSyncTrigger trigger,
        HouseholdNotifier notifier,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        if (error is not null)
        {
            return Page("Ligação cancelada", "Não foi dada autorização ao Kubico. Podes fechar esta página e tentar outra vez na app.", 400);
        }

        var request = secrets.UnprotectState(state);
        if (request is null || ParseProvider(provider) is not { } kind || request.Provider != kind || string.IsNullOrEmpty(code))
        {
            return Page("Pedido inválido", "Este endereço não é válido. Volta à app do Kubico e tenta outra vez.", 400);
        }

        var now = clock.GetUtcNow();
        if (request.ExpiresAt < now)
        {
            return Page("O pedido expirou", "Passou demasiado tempo. Volta à app do Kubico e tenta outra vez.", 400);
        }

        var role = await access.GetRoleAsync(request.UserId, request.HouseholdId, ct);
        var household = await db.Households.AsNoTracking().SingleOrDefaultAsync(h => h.Id == request.HouseholdId, ct);
        if (role is null || household is null
            || await access.GetAccessAsync(request.HouseholdId, role.Value, HouseholdModule.Calendar, ct) < ModuleAccess.Manage)
        {
            return Page("Sem permissão", "Já não podes ligar calendários nesta casa.", 403);
        }

        if (sync.Provider(kind) is not { IsConfigured: true } service)
        {
            return Page("Indisponível", $"A ligação ao {ServiceName(kind)} não está configurada neste servidor.", 400);
        }

        var logger = loggers.CreateLogger(typeof(CalendarSyncEndpoints));
        var name = CalendarSyncService.CalendarName(household.Name);
        CalendarConnection? connection;
        string email;
        try
        {
            var tokens = await service.ExchangeCodeAsync(code, RedirectUri(http, configuration, kind), request.CodeVerifier, ct);
            if (tokens.RefreshToken is null)
            {
                return Page("Não foi possível ligar", $"O {ServiceName(kind)} não deu acesso permanente. Tenta outra vez.", 502);
            }

            email = tokens.Email ?? "(conta sem email)";
            connection = await db.CalendarConnections.SingleOrDefaultAsync(
                c => c.HouseholdId == request.HouseholdId && c.UserId == request.UserId && c.Provider == kind, ct);
            if (connection is null || !string.Equals(connection.AccountEmail, email, StringComparison.OrdinalIgnoreCase))
            {
                // Conta nova (ou outra conta): um calendário novo, copiado do zero.
                var calendarId = await service.CreateCalendarAsync(tokens.AccessToken, name, household.TimeZone, ct);
                if (connection is null)
                {
                    connection = new CalendarConnection
                    {
                        HouseholdId = request.HouseholdId,
                        UserId = request.UserId,
                        Provider = kind,
                        AccountEmail = email,
                        ExternalCalendarId = calendarId,
                        ProtectedRefreshToken = "",
                        CreatedAt = now,
                    };
                    db.CalendarConnections.Add(connection);
                }
                else
                {
                    connection.ExternalCalendarId = calendarId;
                    connection.SyncCursor = null;
                    await db.CalendarEventLinks.Where(l => l.ConnectionId == connection.Id).ExecuteDeleteAsync(ct);
                }
            }

            connection.AccountEmail = email;
            connection.ProtectedRefreshToken = secrets.ProtectToken(tokens.RefreshToken);
        }
        catch (Exception e) when (e is CalendarAuthException or CalendarProviderException or HttpRequestException)
        {
            logger.LogWarning(e, "Falha a ligar uma conta {Provider}.", kind);
            return Page("Não foi possível ligar", $"O {ServiceName(kind)} não confirmou a autorização. Tenta outra vez.", 502);
        }

        connection.Status = CalendarConnectionStatus.Active;
        connection.LastError = null;
        await db.SaveChangesAsync(ct);

        trigger.Request(request.HouseholdId);
        await notifier.CalendarConnectionUpserted(request.UserId, ToDto(connection), ct);
        return Page("Calendário ligado",
            $"Os eventos da casa vão aparecer no calendário «{name}» da conta {email}. Já podes voltar ao Kubico.", 200);
    }

    private static async Task<IResult> SyncNow(
        Guid householdId,
        Guid connectionId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        CalendarSyncService sync,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, error) = await access.RequireMemberAsync(userId, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        var owned = await db.CalendarConnections.AnyAsync(
            c => c.Id == connectionId && c.HouseholdId == householdId && c.UserId == userId, ct);
        if (!owned || await sync.SyncAsync(connectionId, ct) is not { } connection)
        {
            return Results.NotFound();
        }

        return Results.Ok(ToDto(connection));
    }

    /// <summary>
    /// Desliga a conta: apaga lá o calendário do Kubico (os eventos ficam no Kubico) e retira o acesso. Se a conta
    /// já não responder, desliga na mesma.
    /// </summary>
    private static async Task<IResult> Disconnect(
        Guid householdId,
        Guid connectionId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        CalendarSyncService sync,
        CalendarSecrets secrets,
        CalendarSyncLocks locks,
        HouseholdNotifier notifier,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, error) = await access.RequireMemberAsync(userId, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        using var _ = await locks.AcquireAsync(connectionId, ct);
        var connection = await db.CalendarConnections.SingleOrDefaultAsync(
            c => c.Id == connectionId && c.HouseholdId == householdId && c.UserId == userId, ct);
        if (connection is null)
        {
            return Results.NotFound();
        }

        if (sync.Provider(connection.Provider) is { IsConfigured: true } service)
        {
            try
            {
                var refreshToken = secrets.UnprotectToken(connection.ProtectedRefreshToken);
                var tokens = await service.RefreshAsync(refreshToken, ct);
                await service.DeleteCalendarAsync(tokens.AccessToken, connection.ExternalCalendarId, ct);
                await service.RevokeAsync(tokens.RefreshToken ?? refreshToken, ct);
            }
            catch (Exception e) when (e is CryptographicException or CalendarAuthException or CalendarProviderException
                or HttpRequestException)
            {
                // A cópia fica na conta; a pessoa pode apagá-la lá.
                loggers.CreateLogger(typeof(CalendarSyncEndpoints))
                    .LogInformation(e, "Não foi possível apagar o calendário da ligação {Connection}.", connection.Id);
            }
        }

        db.CalendarConnections.Remove(connection);
        await db.SaveChangesAsync(ct);
        await notifier.CalendarConnectionDeleted(userId, householdId, connection.Id, ct);
        return Results.NoContent();
    }

    public static CalendarConnectionDto ToDto(CalendarConnection c) =>
        new(c.Id, c.HouseholdId, c.Provider, c.AccountEmail, c.Status, c.LastSyncedAt, c.LastError);

    /// <summary>O endereço de regresso, que tem de estar registado na app OAuth do Google ou da Microsoft.</summary>
    public static string RedirectUri(HttpRequest http, IConfiguration configuration, CalendarProvider kind) =>
        $"{PublicUrls.BaseUrl(http, configuration)}/api/calendar-sync/callback/{kind.ToString().ToLowerInvariant()}";

    private static CalendarProvider? ParseProvider(string text) => text.ToLowerInvariant() switch
    {
        "google" => CalendarProvider.Google,
        "microsoft" => CalendarProvider.Microsoft,
        _ => null,
    };

    private static string ServiceName(CalendarProvider kind) =>
        kind == CalendarProvider.Google ? "Google Calendar" : "Outlook";

    /// <summary>Uma página simples para o navegador, em vez de JSON.</summary>
    private static IResult Page(string title, string message, int statusCode)
    {
        var html = $$"""
            <!doctype html>
            <html lang="pt-PT">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{Html(title)}} · Kubico</title>
            <style>
            body { font-family: system-ui, sans-serif; max-width: 32rem; margin: 15vh auto; padding: 0 1rem; line-height: 1.5;
                   background: #fffbfe; color: #1c1b1f; }
            @media (prefers-color-scheme: dark) { body { background: #1c1b1f; color: #e6e1e5; } }
            h1 { font-size: 1.4rem; }
            </style>
            </head>
            <body>
            <h1>{{Html(title)}}</h1>
            <p>{{Html(message)}}</p>
            </body>
            </html>
            """;
        return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8, statusCode);
    }

    /// <summary>Escapa só o que o HTML exige; os acentos vão tal e qual (a página é UTF-8).</summary>
    private static string Html(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
