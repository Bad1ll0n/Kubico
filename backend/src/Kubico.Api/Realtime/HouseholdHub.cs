using System.Collections.Concurrent;
using Kubico.Api.Common;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Kubico.Api.Realtime;

/// <summary>
/// Canal de tempo real de um agregado. Cada cliente entra no grupo dos agregados que está a ver (membros, ausências)
/// e nos grupos das áreas a que tem acesso (compras, tarefas, calendário, recompensas), e recebe as alterações feitas
/// pelos outros membros.
/// </summary>
[Authorize]
public class HouseholdHub(HouseholdAccess access, HubConnections connections, HouseholdGroups groups) : Hub
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
        connections.Connected(Context.ConnectionId, userId);
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(userId));
        await base.OnConnectedAsync();
    }

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

/// <summary>Que ligações estão abertas, de quem, e em que casas entraram (para mudar os grupos quando o acesso muda).</summary>
public sealed class HubConnections
{
    private readonly ConcurrentDictionary<string, (Guid UserId, ConcurrentDictionary<Guid, byte> Households)> _connections = new();

    public void Connected(string connectionId, Guid userId) => _connections[connectionId] = (userId, new());

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
