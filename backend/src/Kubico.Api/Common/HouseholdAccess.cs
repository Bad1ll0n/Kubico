using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Common;

/// <summary>
/// Ponto único para verificar se um utilizador pertence a um agregado e com que papel.
/// Todos os endpoints de dados partilhados passam por aqui.
/// </summary>
public class HouseholdAccess(KubicoDbContext db)
{
    public Task<HouseholdRole?> GetRoleAsync(Guid userId, Guid householdId, CancellationToken ct) =>
        db.Memberships
            .Where(m => m.UserId == userId && m.HouseholdId == householdId)
            .Select(m => (HouseholdRole?)m.Role)
            .SingleOrDefaultAsync(ct);

    /// <summary>
    /// Devolve o papel do utilizador, ou um resultado de erro. Quem não é membro recebe 404
    /// (em vez de 403) para não revelar que o agregado existe.
    /// </summary>
    public async Task<(HouseholdRole? Role, IResult? Error)> RequireMemberAsync(
        Guid userId, Guid householdId, CancellationToken ct, Func<HouseholdRole, bool>? requirement = null)
    {
        var role = await GetRoleAsync(userId, householdId, ct);
        if (role is null)
        {
            return (null, Results.NotFound());
        }

        if (requirement is not null && !requirement(role.Value))
        {
            return (role, Results.Problem(
                title: "Não tens permissão para esta ação neste agregado.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (role, null);
    }

    /// <summary>
    /// Membro da casa com pelo menos <paramref name="needed"/> na área: devolve o papel e o acesso, ou o erro (404 a
    /// quem não é membro, 403 a quem não tem acesso suficiente).
    /// </summary>
    public async Task<(HouseholdRole? Role, ModuleAccess Access, IResult? Error)> RequireAsync(
        Guid userId, Guid householdId, HouseholdModule module, ModuleAccess needed, CancellationToken ct)
    {
        var role = await GetRoleAsync(userId, householdId, ct);
        if (role is null)
        {
            return (null, ModuleAccess.None, Results.NotFound());
        }

        var access = await GetAccessAsync(householdId, role.Value, module, ct);
        if (access < needed)
        {
            return (role, access, Results.Problem(
                title: $"Não tens permissão para isto em {ModulePermissions.Label(module)}.",
                statusCode: StatusCodes.Status403Forbidden));
        }

        return (role, access, null);
    }

    public async Task<ModuleAccess> GetAccessAsync(
        Guid householdId, HouseholdRole role, HouseholdModule module, CancellationToken ct)
    {
        if (role == HouseholdRole.Admin)
        {
            return ModuleAccess.Manage;
        }

        var chosen = await db.HouseholdPermissions
            .Where(p => p.HouseholdId == householdId && p.Role == role && p.Module == module)
            .Select(p => (ModuleAccess?)p.Access)
            .SingleOrDefaultAsync(ct);
        return chosen ?? ModulePermissions.Default(role, module);
    }

    /// <summary>Quem tem pelo menos este acesso a uma área (por exemplo, quem aprova pedidos ou vê as fotos).</summary>
    public async Task<List<Guid>> MembersWithAsync(
        Guid householdId, HouseholdModule module, ModuleAccess needed, CancellationToken ct)
    {
        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.HouseholdId == householdId)
            .Select(m => new { m.UserId, m.Role })
            .ToListAsync(ct);
        var chosen = await db.HouseholdPermissions.AsNoTracking().Where(p => p.HouseholdId == householdId).ToListAsync(ct);
        return [.. members.Where(m => ModulePermissions.Resolve(m.Role, module, chosen) >= needed).Select(m => m.UserId)];
    }

    /// <summary>O acesso de um papel a todas as áreas da casa.</summary>
    public async Task<Dictionary<HouseholdModule, ModuleAccess>> GetAllAccessAsync(
        Guid householdId, HouseholdRole role, CancellationToken ct)
    {
        var chosen = await db.HouseholdPermissions.AsNoTracking().Where(p => p.HouseholdId == householdId).ToListAsync(ct);
        return ModulePermissions.ResolveAll(role, chosen);
    }
}
