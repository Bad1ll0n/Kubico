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
}
