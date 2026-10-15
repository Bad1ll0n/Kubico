using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Common;
using Kubico.Api.Notifications;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Kubico.Domain.Rewards;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Rewards;

public record SaveRewardRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Title,
    [property: StringLength(500)] string? Description = null,
    [property: Range(1, 100_000)] int Cost = 10);

public record RewardDto(Guid Id, Guid HouseholdId, string Title, string? Description, int Cost, DateTimeOffset UpdatedAt);

public record RedemptionDto(
    Guid Id,
    Guid HouseholdId,
    Guid? RewardId,
    Guid UserId,
    string RewardTitle,
    int Points,
    RedemptionStatus Status,
    DateTimeOffset RequestedAt,
    Guid? DecidedByUserId,
    DateTimeOffset? DecidedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Saldo de um membro: pontos ganhos nas tarefas, já trocados (pedidos aprovados), reservados (pedidos pendentes)
/// e os que ainda pode usar.
/// </summary>
public record MemberPointsDto(
    Guid UserId,
    string DisplayName,
    string Color,
    int Earned,
    int Spent,
    int Reserved,
    int Available);

public record RewardsDto(
    IReadOnlyList<RewardDto> Rewards,
    IReadOnlyList<RedemptionDto> Redemptions,
    IReadOnlyList<MemberPointsDto> Points);

public static class RewardEndpoints
{
    /// <summary>Quantos pedidos recentes se mostram (os pendentes vêm sempre primeiro).</summary>
    private const int RecentRedemptions = 50;

    public static RouteGroupBuilder MapRewardEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup("/households/{householdId:guid}")
            .WithTags("Rewards")
            .RequireAuthorization();

        household.MapGet("/rewards", GetRewards);
        household.MapPost("/rewards", CreateReward);
        household.MapPut("/rewards/{rewardId:guid}", UpdateReward);
        household.MapDelete("/rewards/{rewardId:guid}", DeleteReward);
        household.MapPost("/rewards/{rewardId:guid}/redeem", Redeem);
        household.MapPost("/redemptions/{redemptionId:guid}/approve", Approve);
        household.MapPost("/redemptions/{redemptionId:guid}/reject", Reject);
        household.MapDelete("/redemptions/{redemptionId:guid}", Cancel);

        return api;
    }

    /// <summary>As recompensas, os pedidos recentes e o saldo de pontos de cada membro.</summary>
    private static async Task<IResult> GetRewards(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct);
        if (error is not null)
        {
            return error;
        }

        var rewards = await db.Rewards.AsNoTracking()
            .Where(r => r.HouseholdId == householdId)
            .ToListAsync(ct);

        var redemptions = await db.RewardRedemptions.AsNoTracking()
            .Where(r => r.HouseholdId == householdId)
            .OrderByDescending(r => r.Status == RedemptionStatus.Pending)
            .ThenByDescending(r => r.RequestedAt)
            .Take(RecentRedemptions)
            .ToListAsync(ct);

        return Results.Ok(new RewardsDto(
            [.. rewards.OrderBy(r => r.Cost).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).Select(ToDto)],
            [.. redemptions.Select(ToDto)],
            await Balances(db, householdId, ct)));
    }

    private static async Task<IResult> CreateReward(
        Guid householdId,
        SaveRewardRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, error) = await access.RequireMemberAsync(userId, householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var now = clock.GetUtcNow();
        var reward = new Reward
        {
            HouseholdId = householdId,
            Title = request.Title,
            CreatedByUserId = userId,
            CreatedAt = now,
        };
        Apply(reward, request, now);

        db.Rewards.Add(reward);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(reward);
        await notifier.RewardUpserted(householdId, dto, ct);
        return Results.Created($"/api/households/{householdId}/rewards/{reward.Id}", dto);
    }

    private static async Task<IResult> UpdateReward(
        Guid householdId,
        Guid rewardId,
        SaveRewardRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(
            principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var reward = await db.Rewards.SingleOrDefaultAsync(r => r.Id == rewardId && r.HouseholdId == householdId, ct);
        if (reward is null)
        {
            return Results.NotFound();
        }

        // Os pedidos já feitos mantêm o nome e o custo de quando foram feitos.
        Apply(reward, request, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        var dto = ToDto(reward);
        await notifier.RewardUpserted(householdId, dto, ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteReward(
        Guid householdId,
        Guid rewardId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(
            principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var reward = await db.Rewards.SingleOrDefaultAsync(r => r.Id == rewardId && r.HouseholdId == householdId, ct);
        if (reward is null)
        {
            return Results.NotFound();
        }

        db.Rewards.Remove(reward);
        await db.SaveChangesAsync(ct);
        await notifier.RewardDeleted(householdId, rewardId, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Pede uma recompensa. Os pedidos de crianças e convidados ficam à espera de um adulto (com os pontos
    /// reservados); os dos adultos ficam logo aprovados.
    /// </summary>
    private static async Task<IResult> Redeem(
        Guid householdId,
        Guid rewardId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        NotificationService notifications,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (role, error) = await access.RequireMemberAsync(userId, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        var reward = await db.Rewards.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == rewardId && r.HouseholdId == householdId, ct);
        if (reward is null)
        {
            return Results.NotFound();
        }

        var balance = (await Balances(db, householdId, ct)).Single(p => p.UserId == userId);
        if (balance.Available < reward.Cost)
        {
            return Results.Problem(
                title: $"Faltam {reward.Cost - balance.Available} pontos para esta recompensa.",
                statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetUtcNow();
        var redemption = new RewardRedemption
        {
            HouseholdId = householdId,
            RewardId = reward.Id,
            UserId = userId,
            RewardTitle = reward.Title,
            Points = reward.Cost,
            Status = RedemptionStatus.Pending,
            RequestedAt = now,
            UpdatedAt = now,
        };
        if (role!.Value.CanManageContent())
        {
            redemption.Decide(approve: true, userId, now);
        }

        db.RewardRedemptions.Add(redemption);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(redemption);
        await notifier.RedemptionUpserted(householdId, dto, ct);

        if (redemption.Status == RedemptionStatus.Pending)
        {
            var adults = await db.Memberships
                .Where(m => m.HouseholdId == householdId && (m.Role == HouseholdRole.Admin || m.Role == HouseholdRole.Adult))
                .Select(m => m.UserId)
                .ToListAsync(ct);
            var name = await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleAsync(ct);
            await notifications.NotifyAsync(
                householdId,
                adults,
                userId,
                NotificationKind.RewardRequested,
                $"{name} pediu: {reward.Title}",
                $"{reward.Cost} pontos. Aprova ou recusa na app.",
                redemption.Id,
                ct);
        }

        return Results.Created($"/api/households/{householdId}/redemptions/{redemption.Id}", dto);
    }

    private static Task<IResult> Approve(
        Guid householdId,
        Guid redemptionId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        NotificationService notifications,
        TimeProvider clock,
        CancellationToken ct) =>
        Decide(approve: true, householdId, redemptionId, principal, db, access, notifier, notifications, clock, ct);

    private static Task<IResult> Reject(
        Guid householdId,
        Guid redemptionId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        NotificationService notifications,
        TimeProvider clock,
        CancellationToken ct) =>
        Decide(approve: false, householdId, redemptionId, principal, db, access, notifier, notifications, clock, ct);

    private static async Task<IResult> Decide(
        bool approve,
        Guid householdId,
        Guid redemptionId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        NotificationService notifications,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, error) = await access.RequireMemberAsync(userId, householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var redemption = await db.RewardRedemptions
            .SingleOrDefaultAsync(r => r.Id == redemptionId && r.HouseholdId == householdId, ct);
        if (redemption is null)
        {
            return Results.NotFound();
        }

        if (redemption.Status != RedemptionStatus.Pending)
        {
            return AlreadyDecided();
        }

        redemption.Decide(approve, userId, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        var dto = ToDto(redemption);
        await notifier.RedemptionUpserted(householdId, dto, ct);
        await notifications.NotifyAsync(
            householdId,
            [redemption.UserId],
            userId,
            NotificationKind.RewardDecided,
            (approve ? "Aprovado: " : "Recusado: ") + redemption.RewardTitle,
            approve ? "Aproveita!" : "Os pontos voltaram para ti.",
            redemption.Id,
            ct);
        return Results.Ok(dto);
    }

    /// <summary>Cancela um pedido pendente (quem o fez ou um adulto); os pontos voltam.</summary>
    private static async Task<IResult> Cancel(
        Guid householdId,
        Guid redemptionId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (role, error) = await access.RequireMemberAsync(userId, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        var redemption = await db.RewardRedemptions
            .SingleOrDefaultAsync(r => r.Id == redemptionId && r.HouseholdId == householdId, ct);
        if (redemption is null)
        {
            return Results.NotFound();
        }

        if (redemption.UserId != userId && !role!.Value.CanManageContent())
        {
            return Results.Problem(
                title: "Só quem fez o pedido, ou um adulto, o pode cancelar.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (redemption.Status != RedemptionStatus.Pending)
        {
            return AlreadyDecided();
        }

        db.RewardRedemptions.Remove(redemption);
        await db.SaveChangesAsync(ct);
        await notifier.RedemptionDeleted(householdId, redemptionId, ct);
        return Results.NoContent();
    }

    /// <summary>Saldo de cada membro, do maior para o menor.</summary>
    private static async Task<List<MemberPointsDto>> Balances(
        KubicoDbContext db, Guid householdId, CancellationToken ct)
    {
        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.HouseholdId == householdId)
            .Select(m => new { m.UserId, m.User.DisplayName, m.Color })
            .ToListAsync(ct);

        var earned = await db.ChoreCompletions.AsNoTracking()
            .Where(c => c.Chore.HouseholdId == householdId)
            .GroupBy(c => c.UserId)
            .Select(g => new { UserId = g.Key, Points = g.Sum(c => c.Points) })
            .ToDictionaryAsync(x => x.UserId, x => x.Points, ct);

        var redeemed = await db.RewardRedemptions.AsNoTracking()
            .Where(r => r.HouseholdId == householdId && r.Status != RedemptionStatus.Rejected)
            .GroupBy(r => new { r.UserId, r.Status })
            .Select(g => new { g.Key.UserId, g.Key.Status, Points = g.Sum(r => r.Points) })
            .ToListAsync(ct);

        int Sum(Guid userId, RedemptionStatus status) =>
            redeemed.Where(r => r.UserId == userId && r.Status == status).Sum(r => r.Points);

        return
        [
            .. members
                .Select(m =>
                {
                    var won = earned.GetValueOrDefault(m.UserId);
                    var spent = Sum(m.UserId, RedemptionStatus.Approved);
                    var reserved = Sum(m.UserId, RedemptionStatus.Pending);
                    return new MemberPointsDto(m.UserId, m.DisplayName, m.Color, won, spent, reserved, won - spent - reserved);
                })
                .OrderByDescending(p => p.Available)
                .ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase),
        ];
    }

    private static IResult AlreadyDecided() =>
        Results.Problem(title: "Este pedido já foi decidido.", statusCode: StatusCodes.Status409Conflict);

    private static void Apply(Reward reward, SaveRewardRequest request, DateTimeOffset now)
    {
        reward.Title = request.Title.Trim();
        reward.Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        reward.Cost = request.Cost;
        reward.UpdatedAt = now;
    }

    private static RewardDto ToDto(Reward r) => new(r.Id, r.HouseholdId, r.Title, r.Description, r.Cost, r.UpdatedAt);

    private static RedemptionDto ToDto(RewardRedemption r) => new(
        r.Id, r.HouseholdId, r.RewardId, r.UserId, r.RewardTitle, r.Points, r.Status, r.RequestedAt,
        r.DecidedByUserId, r.DecidedAt, r.UpdatedAt);
}
