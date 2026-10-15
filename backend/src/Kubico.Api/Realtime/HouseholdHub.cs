using Kubico.Api.Common;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Kubico.Api.Realtime;

/// <summary>
/// Canal de tempo real de um agregado. Cada cliente entra no grupo dos agregados
/// que está a ver e recebe as alterações feitas pelos outros membros.
/// </summary>
[Authorize]
public class HouseholdHub(HouseholdAccess access) : Hub
{
    public const string Path = "/hubs/household";

    public static string GroupName(Guid householdId) => $"household:{householdId}";

    /// <summary>Canal pessoal: os avisos de cada membro (de qualquer casa) chegam por aqui.</summary>
    public static string UserGroupName(Guid userId) => $"user:{userId}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, UserGroupName(Context.User!.GetUserId()));
        await base.OnConnectedAsync();
    }

    public async Task JoinHousehold(Guid householdId)
    {
        var role = await access.GetRoleAsync(Context.User!.GetUserId(), householdId, Context.ConnectionAborted);
        if (role is null)
        {
            throw new HubException("Não pertences a este agregado.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(householdId));
    }

    public Task LeaveHousehold(Guid householdId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(householdId));
}
