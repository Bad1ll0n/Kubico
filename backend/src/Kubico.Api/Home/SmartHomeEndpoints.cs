using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Activity;
using Kubico.Api.Auth;
using Kubico.Api.Chores;
using Kubico.Api.Common;
using Kubico.Api.Notifications;
using Kubico.Api.Realtime;
using Kubico.Domain.Chores;
using Kubico.Domain.Home;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Home;

public record SmartHomeRuleDto(
    Guid Id, string EntityId, string? EntityName, string ToState, string ChoreTitle, Guid? AssigneeUserId, int Points,
    bool Enabled, string? LastState, DateTimeOffset? LastTriggeredAt);

/// <summary>A casa inteligente nesta casa: se está ligada, a que endereço, a última verificação e as regras.</summary>
public record SmartHomeDto(
    bool Connected, string? BaseUrl, DateTimeOffset? ConnectedAt, DateTimeOffset? LastCheckedAt, string? LastError,
    IReadOnlyList<SmartHomeRuleDto> Rules);

public record SmartHomeConnectionRequest(
    [property: Required, StringLength(300)] string BaseUrl,
    [property: Required, StringLength(1000, MinimumLength = 10)] string Token);

public record SaveSmartHomeRuleRequest(
    [property: Required, StringLength(255, MinimumLength = 3)] string EntityId,
    [property: Required, StringLength(100, MinimumLength = 1)] string ToState,
    [property: Required, StringLength(200, MinimumLength = 1)] string ChoreTitle,
    [property: StringLength(200)] string? EntityName = null,
    Guid? AssigneeUserId = null,
    [property: Range(0, 1000)] int Points = 0,
    bool Enabled = true);

/// <summary>
/// A casa inteligente (F4-16c): a ligação ao Home Assistant e as regras que criam tarefas. Tudo pede gerir a Casa;
/// ligar pede também a sessão ativa (é um segredo da casa).
/// </summary>
public static class SmartHomeEndpoints
{
    public const string TokenPurpose = "Kubico.SmartHome.Token.v1";
    private const int MaxRules = 50;

    public static RouteGroupBuilder MapSmartHomeEndpoints(this RouteGroupBuilder api)
    {
        var smart = api.MapGroup("/households/{householdId:guid}/smart-home").WithTags("Home").RequireAuthorization();
        smart.MapGet("/", Get);
        smart.MapPut("/connection", Connect).RequireActiveSession();
        smart.MapDelete("/connection", Disconnect);
        smart.MapGet("/entities", Entities);
        smart.MapPost("/check", CheckNow);
        smart.MapPost("/rules", CreateRule);
        smart.MapPut("/rules/{ruleId:guid}", UpdateRule);
        smart.MapDelete("/rules/{ruleId:guid}", DeleteRule);
        return api;
    }

    private static async Task<IResult> Get(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, HttpContext http, CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(principal.GetUserId(), householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        return error ?? Results.Ok(await DtoAsync(db, householdId, Texts.LanguageOf(http), ct));
    }

    private static async Task<IResult> Connect(
        Guid householdId,
        SmartHomeConnectionRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        IHomeAssistantClient homeAssistant,
        IDataProtectionProvider protection,
        ActivityLog activity,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var (url, invalid) = await HomeAssistantUrls.ValidateAsync(request.BaseUrl, ct);
        if (url is null)
        {
            return Invalid("baseUrl", invalid!);
        }

        var token = request.Token.Trim();
        try
        {
            await homeAssistant.CheckAsync(url, token, ct);
        }
        catch (HomeAssistantException e)
        {
            return Invalid(e.Unauthorized ? "token" : "baseUrl", e.Message);
        }

        var now = clock.GetUtcNow();
        var connection = await db.SmartHomeConnections.SingleOrDefaultAsync(c => c.HouseholdId == householdId, ct);
        var protectedToken = protection.CreateProtector(TokenPurpose).Protect(token);
        if (connection is null)
        {
            db.SmartHomeConnections.Add(new SmartHomeConnection
            {
                HouseholdId = householdId, BaseUrl = url.ToString(), ProtectedToken = protectedToken,
                ConnectedByUserId = userId, ConnectedAt = now,
            });
        }
        else
        {
            connection.BaseUrl = url.ToString();
            connection.ProtectedToken = protectedToken;
            connection.ConnectedByUserId = userId;
            connection.ConnectedAt = now;
            connection.LastError = null;
        }

        activity.Capture(householdId, userId, HouseholdModule.Home, "ligou a casa ao Home Assistant", undoable: false);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await DtoAsync(db, householdId, Texts.LanguageOf(http), ct));
    }

    private static async Task<IResult> Disconnect(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, ActivityLog activity, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var connection = await db.SmartHomeConnections.SingleOrDefaultAsync(c => c.HouseholdId == householdId, ct);
        if (connection is not null)
        {
            db.SmartHomeConnections.Remove(connection);
            activity.Capture(householdId, userId, HouseholdModule.Home, "desligou a casa do Home Assistant", undoable: false);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    /// <summary>As entidades do Home Assistant (para escolher numa regra), pelo nome.</summary>
    private static async Task<IResult> Entities(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        IHomeAssistantClient homeAssistant,
        IDataProtectionProvider protection,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(principal.GetUserId(), householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var connection = await db.SmartHomeConnections.AsNoTracking().SingleOrDefaultAsync(c => c.HouseholdId == householdId, ct);
        if (connection is null || Unprotect(protection, connection.ProtectedToken) is not { } token)
        {
            return NotConnected();
        }

        try
        {
            var states = await homeAssistant.StatesAsync(new Uri(connection.BaseUrl), token, ct);
            return Results.Ok(states
                .OrderBy(s => s.Name ?? s.EntityId, StringComparer.CurrentCultureIgnoreCase)
                .Take(1000)
                .ToList());
        }
        catch (HomeAssistantException e)
        {
            return Results.Problem(title: e.Message, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<IResult> CheckNow(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        SmartHomeService service,
        HttpContext http,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(principal.GetUserId(), householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        if (!await db.SmartHomeConnections.AnyAsync(c => c.HouseholdId == householdId, ct))
        {
            return NotConnected();
        }

        await service.CheckAsync(householdId, ct);
        return Results.Ok(await DtoAsync(db, householdId, Texts.LanguageOf(http), ct));
    }

    private static async Task<IResult> CreateRule(
        Guid householdId,
        SaveSmartHomeRuleRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        ActivityLog activity,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        if (await ValidateAsync(db, householdId, request, ct) is { } invalid)
        {
            return invalid;
        }

        if (await db.SmartHomeRules.CountAsync(r => r.HouseholdId == householdId, ct) >= MaxRules)
        {
            return Results.Problem(title: "A casa já tem o máximo de regras (50).", statusCode: StatusCodes.Status409Conflict);
        }

        var rule = new SmartHomeRule
        {
            HouseholdId = householdId,
            EntityId = request.EntityId.Trim(),
            ToState = request.ToState.Trim(),
            ChoreTitle = request.ChoreTitle.Trim(),
            CreatedByUserId = userId,
            CreatedAt = clock.GetUtcNow(),
        };
        Apply(rule, request);
        db.SmartHomeRules.Add(rule);
        activity.Capture(householdId, userId, HouseholdModule.Home, $"criou a regra «{rule.ChoreTitle}» da casa inteligente", undoable: false);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/households/{householdId}/smart-home/rules/{rule.Id}", ToDto(rule));
    }

    private static async Task<IResult> UpdateRule(
        Guid householdId,
        Guid ruleId,
        SaveSmartHomeRuleRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(principal.GetUserId(), householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var rule = await db.SmartHomeRules.SingleOrDefaultAsync(r => r.Id == ruleId && r.HouseholdId == householdId, ct);
        if (rule is null)
        {
            return Results.NotFound();
        }

        if (await ValidateAsync(db, householdId, request, ct) is { } invalid)
        {
            return invalid;
        }

        var entity = request.EntityId.Trim();
        if (entity != rule.EntityId)
        {
            // Outra entidade: o estado que se tinha visto já não serve.
            rule.LastState = null;
        }

        rule.EntityId = entity;
        rule.ToState = request.ToState.Trim();
        rule.ChoreTitle = request.ChoreTitle.Trim();
        Apply(rule, request);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(rule));
    }

    private static async Task<IResult> DeleteRule(
        Guid householdId, Guid ruleId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(principal.GetUserId(), householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var deleted = await db.SmartHomeRules.Where(r => r.Id == ruleId && r.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? Results.NotFound() : Results.NoContent();
    }

    private static void Apply(SmartHomeRule rule, SaveSmartHomeRuleRequest request)
    {
        rule.EntityName = string.IsNullOrWhiteSpace(request.EntityName) ? null : request.EntityName.Trim();
        rule.AssigneeUserId = request.AssigneeUserId;
        rule.Points = request.Points;
        rule.Enabled = request.Enabled;
    }

    private static async Task<IResult?> ValidateAsync(
        KubicoDbContext db, Guid householdId, SaveSmartHomeRuleRequest request, CancellationToken ct)
    {
        // "dominio.nome": o formato dos ids do Home Assistant.
        var entity = request.EntityId.Trim();
        if (entity.Count(c => c == '.') != 1 || entity.StartsWith('.') || entity.EndsWith('.') || entity.Any(char.IsWhiteSpace))
        {
            return Invalid("entityId", "Escolhe uma entidade do Home Assistant (por exemplo sensor.maquina_de_lavar).");
        }

        if (request.AssigneeUserId is { } assignee
            && !await db.Memberships.AnyAsync(m => m.HouseholdId == householdId && m.UserId == assignee, ct))
        {
            return Invalid("assigneeUserId", "A pessoa escolhida não é desta casa.");
        }

        return null;
    }

    internal static string? Unprotect(IDataProtectionProvider protection, string value)
    {
        try
        {
            return protection.CreateProtector(TokenPurpose).Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static async Task<SmartHomeDto> DtoAsync(KubicoDbContext db, Guid householdId, string language, CancellationToken ct)
    {
        var connection = await db.SmartHomeConnections.AsNoTracking().SingleOrDefaultAsync(c => c.HouseholdId == householdId, ct);
        var rules = await db.SmartHomeRules.AsNoTracking().Where(r => r.HouseholdId == householdId)
            .OrderBy(r => r.CreatedAt).ToListAsync(ct);
        return new SmartHomeDto(
            connection is not null, connection?.BaseUrl, connection?.ConnectedAt, connection?.LastCheckedAt,
            Texts.Translate(connection?.LastError, language), [.. rules.Select(ToDto)]);
    }

    private static SmartHomeRuleDto ToDto(SmartHomeRule r) => new(
        r.Id, r.EntityId, r.EntityName, r.ToState, r.ChoreTitle, r.AssigneeUserId, r.Points, r.Enabled, r.LastState, r.LastTriggeredAt);

    private static IResult NotConnected() =>
        Results.Problem(title: "A casa ainda não está ligada ao Home Assistant.", statusCode: StatusCodes.Status409Conflict);

    private static IResult Invalid(string field, string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] });
}

/// <summary>
/// Lê os estados do Home Assistant de cada casa ligada e aplica as regras: quando uma entidade passa para o estado da
/// regra, cria a tarefa (uma vez: o estado visto muda com um <c>ExecuteUpdateAsync</c> condicional, e há um intervalo
/// mínimo entre disparos). Corre a cada minuto no <see cref="SmartHomeWorker"/>.
/// </summary>
public class SmartHomeService(
    KubicoDbContext db,
    IHomeAssistantClient homeAssistant,
    IDataProtectionProvider protection,
    HouseholdNotifier notifier,
    NotificationService notifications,
    ActivityLog activity,
    TimeProvider clock,
    ILogger<SmartHomeService> logger)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var households = await db.SmartHomeConnections.AsNoTracking()
            .Where(c => db.SmartHomeRules.Any(r => r.HouseholdId == c.HouseholdId && r.Enabled))
            .Select(c => c.HouseholdId)
            .ToListAsync(ct);
        foreach (var householdId in households)
        {
            try
            {
                await CheckAsync(householdId, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "A casa inteligente falhou na casa {HouseholdId}.", householdId);
            }
        }
    }

    /// <summary>Verifica uma casa: lê os estados e cria as tarefas das regras que dispararam. Devolve quantas criou.</summary>
    public async Task<int> CheckAsync(Guid householdId, CancellationToken ct)
    {
        var connection = await db.SmartHomeConnections.SingleOrDefaultAsync(c => c.HouseholdId == householdId, ct);
        if (connection is null)
        {
            return 0;
        }

        var now = clock.GetUtcNow();
        connection.LastCheckedAt = now;
        IReadOnlyList<HomeAssistantEntity> states;
        try
        {
            var token = SmartHomeEndpoints.Unprotect(protection, connection.ProtectedToken)
                ?? throw new HomeAssistantException("O token do Home Assistant tem de voltar a ser escrito.", unauthorized: true);
            states = await homeAssistant.StatesAsync(new Uri(connection.BaseUrl), token, ct);
            connection.LastError = null;
        }
        catch (HomeAssistantException e)
        {
            connection.LastError = e.Message;
            await db.SaveChangesAsync(ct);
            return 0;
        }

        await db.SaveChangesAsync(ct);
        var byId = states.GroupBy(s => s.EntityId).ToDictionary(g => g.Key, g => g.First().State);
        var rules = await db.SmartHomeRules.AsNoTracking().Where(r => r.HouseholdId == householdId && r.Enabled).ToListAsync(ct);
        var created = 0;
        foreach (var rule in rules)
        {
            if (!byId.TryGetValue(rule.EntityId, out var state) || state == rule.LastState)
            {
                continue;
            }

            // Só quem muda o estado visto conta a mudança (duas verificações ao mesmo tempo não criam duas tarefas).
            var previous = rule.LastState;
            var claimed = await db.SmartHomeRules
                .Where(r => r.Id == rule.Id && r.LastState == previous)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.LastState, state), ct);
            var fires = claimed == 1
                && previous is not null
                && string.Equals(state, rule.ToState, StringComparison.OrdinalIgnoreCase)
                && (rule.LastTriggeredAt is not { } last || now - last >= SmartHomeRule.Cooldown);
            if (fires && await CreateChoreAsync(rule, now, ct))
            {
                created++;
            }
        }

        return created;
    }

    private async Task<bool> CreateChoreAsync(SmartHomeRule rule, DateTimeOffset now, CancellationToken ct)
    {
        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == rule.HouseholdId, ct);
        var assignee = rule.AssigneeUserId is { } id
            && await db.Memberships.AnyAsync(m => m.HouseholdId == rule.HouseholdId && m.UserId == id, ct)
                ? id
                : (Guid?)null;
        var chore = new Chore
        {
            HouseholdId = rule.HouseholdId,
            Title = rule.ChoreTitle,
            Points = rule.Points,
            DueDate = household.Today(now),
            AssigneeUserId = assignee,
            CreatedByUserId = rule.CreatedByUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Chores.Add(chore);
        activity.Capture(rule.HouseholdId, rule.CreatedByUserId, HouseholdModule.Chores,
            $"a casa inteligente criou a tarefa «{chore.Title}»", undoable: false);
        await db.SaveChangesAsync(ct);
        await db.SmartHomeRules.Where(r => r.Id == rule.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.LastTriggeredAt, now), ct);

        await notifier.ChoreUpserted(rule.HouseholdId, ChoreEndpoints.ToDto(chore), ct);
        // Avisos com "ninguém" como autor: vão também para quem criou a regra.
        await ChoreEndpoints.NotifyAssignee(notifications, chore, null, Guid.Empty, $"Tarefa para ti: {chore.Title}", ct);
        return true;
    }
}

public class SmartHomeWorker(IServiceScopeFactory scopes, ILogger<SmartHomeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SmartHomeService>().RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Falha na casa inteligente; volta a tentar daqui a um minuto.");
            }
        }
    }
}
