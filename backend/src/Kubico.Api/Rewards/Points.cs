using Kubico.Domain.Households;
using Kubico.Domain.Rewards;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Rewards;

/// <summary>
/// O saldo de pontos de um membro numa casa e o "cadeado" que o protege. Quem tira pontos (pedir ou aprovar uma
/// recompensa, desfazer ou não aceitar uma tarefa feita) tranca primeiro o saldo — grava a <see cref="Membership"/>
/// da pessoa com uma versão nova na mesma gravação — e só depois o lê: dois pedidos em simultâneo não gastam os
/// mesmos pontos (o segundo falha com DbUpdateConcurrencyException e recebe 409).
/// </summary>
public static class Points
{
    /// <summary>O código (extensão "code" do ProblemDetails) de "os pontos já foram trocados".</summary>
    public const string SpentCode = "points-spent";

    /// <summary>Tranca o saldo de um membro (null se já não estiver na casa). Chama-se antes de ler o saldo.</summary>
    public static async Task<Membership?> LockAsync(KubicoDbContext db, Guid householdId, Guid userId, CancellationToken ct)
    {
        var membership = await db.Memberships.SingleOrDefaultAsync(m => m.HouseholdId == householdId && m.UserId == userId, ct);
        if (membership is not null)
        {
            db.Touch(membership);
        }

        return membership;
    }

    /// <summary>Os pontos que o membro ainda pode usar, segundo o que está gravado: ganhos − trocados − reservados.</summary>
    public static async Task<int> AvailableAsync(KubicoDbContext db, Guid householdId, Guid userId, CancellationToken ct)
    {
        var earned = await db.ChoreCompletions.AsNoTracking()
            .Where(c => c.Chore.HouseholdId == householdId && c.UserId == userId && c.RejectedAt == null)
            .SumAsync(c => c.Points, ct);
        var used = await db.RewardRedemptions.AsNoTracking()
            .Where(r => r.HouseholdId == householdId && r.UserId == userId && r.Status != RedemptionStatus.Rejected)
            .SumAsync(r => r.Points, ct);
        return earned - used;
    }

    /// <summary>409 "os pontos já foram trocados", com o código para a app perguntar se quer continuar.</summary>
    public static IResult SpentProblem(string name, int owed) => Results.Problem(
        title: $"Os pontos desta tarefa já foram trocados: {name} ficaria a dever {owed} pontos.",
        statusCode: StatusCodes.Status409Conflict,
        extensions: new Dictionary<string, object?> { ["code"] = SpentCode });
}
