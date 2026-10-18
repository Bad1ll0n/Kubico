using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Households;

/// <summary>O acesso de um papel a cada área da casa.</summary>
public record RolePermissionsDto(HouseholdRole Role, IReadOnlyDictionary<HouseholdModule, ModuleAccess> Access);

/// <summary>
/// O que cada papel pode fazer nesta casa (<c>Roles</c>) e o habitual (<c>Defaults</c>), para os papéis que se
/// escolhem: o administrador tem sempre acesso a tudo e não aparece.
/// </summary>
public record HouseholdPermissionsDto(IReadOnlyList<RolePermissionsDto> Roles, IReadOnlyList<RolePermissionsDto> Defaults);

public record SavePermissionsRequest([property: Required] IReadOnlyList<RolePermissionsDto> Roles);

/// <summary>
/// Permissões por área: o administrador escolhe, para adultos, crianças e convidados, se cada área (compras, tarefas,
/// calendário, recompensas) não aparece, só se vê, se usa ou se gere. Quem não tem acesso deixa de receber as
/// alterações dessa área em tempo real.
/// </summary>
public static class PermissionEndpoints
{
    public static RouteGroupBuilder MapPermissionEndpoints(this RouteGroupBuilder api)
    {
        var permissions = api.MapGroup("/households/{householdId:guid}/permissions")
            .WithTags("Households")
            .RequireAuthorization();

        permissions.MapGet("/", GetPermissions);
        permissions.MapPut("/", SavePermissions);
        return api;
    }

    private static async Task<IResult> GetPermissions(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct);
        if (error is not null)
        {
            return error;
        }

        return Results.Ok(await Describe(db, householdId, ct));
    }

    /// <summary>Só o administrador escolhe. Os papéis e as áreas que não vêm no pedido ficam como estavam.</summary>
    private static async Task<IResult> SavePermissions(
        Guid householdId,
        SavePermissionsRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct, r => r.CanManageMembers());
        if (error is not null)
        {
            return error;
        }

        foreach (var role in request.Roles)
        {
            if (!ModulePermissions.ConfigurableRoles.Contains(role.Role))
            {
                return Invalid("O administrador tem sempre acesso a tudo.");
            }

            foreach (var (module, level) in role.Access)
            {
                if (!Enum.IsDefined(module) || !ModulePermissions.IsAllowed(module, level))
                {
                    return Invalid(Enum.IsDefined(module)
                        ? $"Esse acesso não existe em {ModulePermissions.Label(module)}."
                        : "Essa área não existe.");
                }
            }
        }

        // Guarda-se só o que é diferente do habitual: se o habitual mudar numa versão nova, as casas acompanham.
        var chosen = await db.HouseholdPermissions.Where(p => p.HouseholdId == householdId).ToListAsync(ct);
        foreach (var role in request.Roles)
        {
            foreach (var (module, level) in role.Access)
            {
                var row = chosen.SingleOrDefault(p => p.Role == role.Role && p.Module == module);
                if (level == ModulePermissions.Default(role.Role, module))
                {
                    if (row is not null)
                    {
                        db.HouseholdPermissions.Remove(row);
                        chosen.Remove(row);
                    }
                }
                else if (row is null)
                {
                    row = new HouseholdPermission { HouseholdId = householdId, Role = role.Role, Module = module, Access = level };
                    db.HouseholdPermissions.Add(row);
                    chosen.Add(row);
                }
                else
                {
                    row.Access = level;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await notifier.PermissionsChanged(householdId, ct);
        return Results.Ok(await Describe(db, householdId, ct));
    }

    private static async Task<HouseholdPermissionsDto> Describe(KubicoDbContext db, Guid householdId, CancellationToken ct)
    {
        var chosen = await db.HouseholdPermissions.AsNoTracking().Where(p => p.HouseholdId == householdId).ToListAsync(ct);
        return new HouseholdPermissionsDto(
            [.. ModulePermissions.ConfigurableRoles.Select(r => new RolePermissionsDto(r, ModulePermissions.ResolveAll(r, chosen)))],
            [.. ModulePermissions.ConfigurableRoles.Select(r => new RolePermissionsDto(r, ModulePermissions.ResolveAll(r, [])))]);
    }

    private static IResult Invalid(string title) =>
        Results.Problem(title: title, statusCode: StatusCodes.Status400BadRequest);
}
