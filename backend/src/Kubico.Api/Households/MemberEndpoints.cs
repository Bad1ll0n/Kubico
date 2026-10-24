using System.Security.Claims;
using Kubico.Api.Activity;
using Kubico.Api.Auth;
using Kubico.Api.Calendar;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Households;

public record UpdateMemberRoleRequest(HouseholdRole Role);

public record TransferAdminRequest(Guid UserId);

/// <summary>
/// Gerir quem está na casa: mudar o papel, tirar alguém, sair, passar a administração. A casa tem sempre pelo menos
/// um administrador; quando sai o último membro, é apagada.
/// </summary>
public static class MemberEndpoints
{
    public static RouteGroupBuilder MapMemberEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup("/households/{householdId:guid}")
            .WithTags("Households")
            .RequireAuthorization();

        // Ações sensíveis: só com a sessão ativa (terminar a sessão corta-as logo).
        household.MapPut("/members/{userId:guid}/role", UpdateRole).RequireActiveSession();
        household.MapDelete("/members/{userId:guid}", RemoveMember).RequireActiveSession();
        household.MapPost("/leave", Leave);
        household.MapPost("/transfer-admin", TransferAdmin).RequireActiveSession();

        return api;
    }

    private static async Task<IResult> UpdateRole(
        Guid householdId,
        Guid userId,
        UpdateMemberRoleRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct, r => r.CanManageMembers());
        if (error is not null)
        {
            return error;
        }

        var membership = await FindMember(db, householdId, userId, ct);
        if (membership is null)
        {
            return Results.NotFound();
        }

        if (!Enum.IsDefined(request.Role))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["Role"] = ["Esse papel não existe."] });
        }

        if (membership.User.IsChildProfile && request.Role != HouseholdRole.Child)
        {
            return Problem("Um perfil de criança é sempre Criança. Para outro papel, a pessoa precisa de conta própria.",
                StatusCodes.Status400BadRequest);
        }

        if (membership.Role == HouseholdRole.Admin && request.Role != HouseholdRole.Admin
            && await AdminCount(db, householdId, ct) == 1)
        {
            return LastAdmin();
        }

        membership.Role = request.Role;
        activity.Capture(householdId, principal.GetUserId(), null, $"mudou o papel de {membership.User.DisplayName} para {RoleLabel(request.Role)}", undoable: false);
        await db.SaveChangesAsync(ct);
        await CalendarFeedEndpoints.RevokeFeedsWithoutAccessAsync(db, access, householdId, ct);

        var dto = ToDto(membership);
        await notifier.MemberUpserted(householdId, dto, ct);
        return Results.Ok(dto);
    }

    /// <summary>Um administrador tira alguém da casa. Para sair a própria pessoa, usa-se <c>/leave</c>.</summary>
    private static async Task<IResult> RemoveMember(
        Guid householdId,
        Guid userId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        MembershipService memberships,
        CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (_, error) = await access.RequireMemberAsync(me, householdId, ct, r => r.CanManageMembers());
        if (error is not null)
        {
            return error;
        }

        if (userId == me)
        {
            return Problem("Para saíres da casa, usa a opção Sair desta casa.", StatusCodes.Status400BadRequest);
        }

        var membership = await FindMember(db, householdId, userId, ct);
        if (membership is null)
        {
            return Results.NotFound();
        }

        activity.Capture(householdId, principal.GetUserId(), null, $"tirou {membership.User.DisplayName} da casa", undoable: false);
        await memberships.RemoveAsync(membership, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Sair da casa. O último administrador só sai depois de passar a administração, a menos que não haja mais
    /// ninguém com conta própria: aí a casa é apagada.
    /// </summary>
    private static async Task<IResult> Leave(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        MembershipService memberships,
        CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (role, error) = await access.RequireMemberAsync(me, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        var membership = (await FindMember(db, householdId, me, ct))!;
        if (membership.User.IsChildProfile)
        {
            return Problem("Pede a um adulto da casa para tirar o teu perfil.", StatusCodes.Status403Forbidden);
        }

        var others = await db.Memberships
            .Where(m => m.HouseholdId == householdId && m.UserId != me && m.User.ManagedByHouseholdId == null)
            .Select(m => m.Role)
            .ToListAsync(ct);

        if (others.Count == 0)
        {
            await memberships.DeleteHouseholdAsync(householdId, ct);
            return Results.NoContent();
        }

        if (role == HouseholdRole.Admin && !others.Contains(HouseholdRole.Admin))
        {
            return Problem("Antes de saíres, passa a administração da casa a outra pessoa.", StatusCodes.Status409Conflict);
        }

        activity.Capture(householdId, me, null, "saiu da casa", undoable: false);
        await memberships.RemoveAsync(membership, ct);
        return Results.NoContent();
    }

    /// <summary>Passa a administração: a outra pessoa fica administradora e quem passa fica adulto.</summary>
    private static async Task<IResult> TransferAdmin(
        Guid householdId,
        TransferAdminRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (_, error) = await access.RequireMemberAsync(me, householdId, ct, r => r.CanManageMembers());
        if (error is not null)
        {
            return error;
        }

        var target = await FindMember(db, householdId, request.UserId, ct);
        if (target is null || request.UserId == me)
        {
            return Results.NotFound();
        }

        if (target.User.IsChildProfile)
        {
            return Problem("A administração só pode passar para alguém com conta própria.", StatusCodes.Status400BadRequest);
        }

        var mine = (await FindMember(db, householdId, me, ct))!;
        target.Role = HouseholdRole.Admin;
        mine.Role = HouseholdRole.Adult;
        activity.Capture(householdId, me, null, $"passou a administração a {target.User.DisplayName}", undoable: false);
        await db.SaveChangesAsync(ct);

        await notifier.MemberUpserted(householdId, ToDto(target), ct);
        await notifier.MemberUpserted(householdId, ToDto(mine), ct);
        return Results.NoContent();
    }

    internal static string RoleLabel(HouseholdRole role) => role switch
    {
        HouseholdRole.Admin => "Administrador",
        HouseholdRole.Adult => "Adulto",
        HouseholdRole.Child => "Criança",
        _ => "Convidado",
    };

    private static Task<Membership?> FindMember(KubicoDbContext db, Guid householdId, Guid userId, CancellationToken ct) =>
        db.Memberships.Include(m => m.User).SingleOrDefaultAsync(m => m.HouseholdId == householdId && m.UserId == userId, ct);

    private static Task<int> AdminCount(KubicoDbContext db, Guid householdId, CancellationToken ct) =>
        db.Memberships.CountAsync(m => m.HouseholdId == householdId && m.Role == HouseholdRole.Admin, ct);

    private static IResult LastAdmin() => Problem(
        "A casa precisa de pelo menos um administrador. Passa a administração a outra pessoa primeiro.",
        StatusCodes.Status409Conflict);

    private static IResult Problem(string title, int statusCode) => Results.Problem(title: title, statusCode: statusCode);

    public static MemberDto ToDto(Membership m) =>
        new(m.UserId, m.User.DisplayName, m.Role, m.Color, m.User.IsChildProfile, m.User.BirthDate);
}
