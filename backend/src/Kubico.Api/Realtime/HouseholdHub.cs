using System.Collections.Concurrent;
using System.Security.Claims;
using Kubico.Api.Auth;
using Kubico.Api.Common;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Kubico.Api.Realtime;

/// <summary>
/// Canal de tempo real de um agregado. Cada cliente entra no grupo dos agregados que está a ver (membros, ausências)
/// e nos grupos das áreas a que tem acesso (compras, tarefas, calendário, recompensas), e recebe as alterações feitas
/// pelos outros membros. Só liga quem tem a sessão ativa; a ligação fecha quando o token expira (o cliente volta a
/// ligar com um novo) e logo que a sessão termina (noutro dispositivo, ao mudar o PIN, ao apagar a conta…).
/// </summary>
[Authorize]
public class HouseholdHub(HouseholdAccess access, HubConnections connections, HouseholdGroups groups, SessionService sessions) : Hub
{
    public const string Path = "/hubs/household";

    public static string GroupName(Guid householdId) => $"household:{householdId}";

    /// <summary>As alterações de uma área só chegam a quem tem acesso a ela.</summary>
    public static string ModuleGroupName(Guid householdId, HouseholdModule module) => $"household:{householdId}:{module}";

    /// <summary>Canal pessoal: os avisos de cada membro (de qualquer casa) chegam por aqui.</summary>
    public static string UserGroupName(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        var userId = Context.User!.GetUserId();
        var sessionId = SessionOf(Context.User!);
        if (!await sessions.IsActiveAsync(userId, sessionId, Context.ConnectionAborted))
        {
            // Um token de acesso ainda válido de uma sessão que já terminou não volta a ligar.
            Context.Abort();
            return;
        }

        connections.Connected(Context, userId, sessionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(userId));
        await base.OnConnectedAsync();
    }

    /// <summary>A sessão (família de refresh tokens) do token com que ligou.</summary>
    public static Guid? SessionOf(ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(TokenService.SessionClaim), out var sid) ? sid : null;

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Disconnected(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task JoinHousehold(Guid householdId)
    {
        var userId = Context.User!.GetUserId();
        var role = await access.GetRoleAsync(userId, householdId, Context.ConnectionAborted);
        if (role is null)
        {
            throw new HubException("Não pertences a este agregado.");
        }

        connections.Joined(Context.ConnectionId, householdId);
        await groups.ApplyAsync(Context.ConnectionId, householdId, role, Context.ConnectionAborted);
    }

    public async Task LeaveHousehold(Guid householdId)
    {
        connections.Left(Context.ConnectionId, householdId);
        await groups.ApplyAsync(Context.ConnectionId, householdId, null, Context.ConnectionAborted);
    }
}

/// <summary>
/// Que ligações estão abertas, de quem, de que sessão e em que casas entraram: para mudar os grupos quando o acesso
/// muda, e para fechar as de uma sessão que terminou.
/// </summary>
public sealed class HubConnections
{
    private readonly ConcurrentDictionary<string, Connection> _connections = new();

    /// <summary>Uma ligação aberta: de quem, de que sessão (o <c>sid</c> do token) e em que casas entrou.</summary>
    public sealed record Connection(Guid UserId, Guid? SessionId, HubCallerContext Context)
    {
        public ConcurrentDictionary<Guid, byte> Households { get; } = new();
    }

    public void Connected(HubCallerContext context, Guid userId, Guid? sessionId) =>
        _connections[context.ConnectionId] = new Connection(userId, sessionId, context);

    /// <summary>Fecha já as ligações escolhidas (sessão terminada, PIN mudado, conta apagada).</summary>
    public void Abort(Func<Connection, bool> which)
    {
        foreach (var (id, connection) in _connections.Where(c => which(c.Value)).ToList())
        {
            _connections.TryRemove(id, out _);
            connection.Context.Abort();
        }
    }

    public void Disconnected(string connectionId) => _connections.TryRemove(connectionId, out _);

    public void Joined(string connectionId, Guid householdId)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
        {
            connection.Households[householdId] = 0;
        }
    }

    public void Left(string connectionId, Guid householdId)
    {
        if (_connections.TryGetValue(connectionId, out var connection))
        {
            connection.Households.TryRemove(householdId, out _);
        }
    }

    /// <summary>As ligações que entraram nesta casa (de uma pessoa, ou de todas).</summary>
    public List<(string ConnectionId, Guid UserId)> InHousehold(Guid householdId, Guid? userId = null) =>
        [.. _connections
            .Where(c => c.Value.Households.ContainsKey(householdId) && (userId is null || c.Value.UserId == userId))
            .Select(c => (c.Key, c.Value.UserId))];
}

/// <summary>Põe cada ligação nos grupos certos: a casa, e as áreas a que o papel dá acesso.</summary>
public class HouseholdGroups(IHubContext<HouseholdHub> hub, HouseholdAccess access, HubConnections connections)
{
    /// <summary>
    /// Volta a acertar os grupos das ligações abertas nesta casa (de uma pessoa, ou de todas), depois de mudar o
    /// papel, as permissões, ou de alguém sair.
    /// </summary>
    public async Task RefreshAsync(Guid householdId, CancellationToken ct, Guid? userId = null)
    {
        foreach (var (connectionId, owner) in connections.InHousehold(householdId, userId))
        {
            var role = await access.GetRoleAsync(owner, householdId, ct);
            if (role is null)
            {
                connections.Left(connectionId, householdId);
            }

            await ApplyAsync(connectionId, householdId, role, ct);
        }
    }

    /// <summary>Sem papel (saiu da casa), a ligação sai de todos os grupos da casa.</summary>
    public async Task ApplyAsync(string connectionId, Guid householdId, HouseholdRole? role, CancellationToken ct)
    {
        var levels = role is null ? null : await access.GetAllAccessAsync(householdId, role.Value, ct);
        await Set(connectionId, HouseholdHub.GroupName(householdId), role is not null, ct);
        foreach (var module in ModulePermissions.Modules)
        {
            var visible = levels is not null && levels[module] >= ModuleAccess.View;
            await Set(connectionId, HouseholdHub.ModuleGroupName(householdId, module), visible, ct);
        }
    }

    private Task Set(string connectionId, string group, bool member, CancellationToken ct) => member
        ? hub.Groups.AddToGroupAsync(connectionId, group, ct)
        : hub.Groups.RemoveFromGroupAsync(connectionId, group, ct);
}
