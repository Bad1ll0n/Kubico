using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Activity;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Expenses;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Expenses;

public record ShareDto(Guid UserId, int AmountCents);

/// <summary>
/// Uma despesa. Com <c>Shares</c>, a parte de cada um (tem de somar o total); sem elas, divide-se em partes iguais
/// por <c>SplitWith</c> (por omissão, quem pagou).
/// </summary>
public record SaveExpenseRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Description,
    [property: Range(1, 100_000_000)] int AmountCents,
    Guid PaidByUserId,
    DateOnly Date,
    [property: StringLength(50)] string? Category = null,
    IReadOnlyList<Guid>? SplitWith = null,
    IReadOnlyList<ShareDto>? Shares = null);

public record ExpenseDto(
    Guid Id,
    string Description,
    int AmountCents,
    Guid PaidByUserId,
    DateOnly Date,
    string? Category,
    IReadOnlyList<ShareDto> Shares,
    Guid CreatedByUserId,
    DateTimeOffset UpdatedAt);

public record SaveSettlementRequest(Guid FromUserId, Guid ToUserId, [property: Range(1, 100_000_000)] int AmountCents, DateOnly Date);

public record SettlementDto(Guid Id, Guid FromUserId, Guid ToUserId, int AmountCents, DateOnly Date);

public record BalanceDto(Guid UserId, int NetCents);

/// <summary>As despesas recentes, os acertos, o saldo de cada um e quem paga a quem para ficarem acertados.</summary>
public record ExpensesOverviewDto(
    IReadOnlyList<ExpenseDto> Expenses,
    IReadOnlyList<SettlementDto> Settlements,
    IReadOnlyList<BalanceDto> Balances,
    IReadOnlyList<Transfer> Transfers);

/// <summary>
/// Despesas partilhadas (F4-10): ver é <c>View</c>; registar despesas e acertos é <c>Participate</c> (e mudar ou
/// apagar as que registou); <c>Manage</c> muda e apaga todas. Os saldos contam tudo, desde sempre.
/// </summary>
public static class ExpenseEndpoints
{
    private const int Recent = 100;

    public static RouteGroupBuilder MapExpenseEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup("/households/{householdId:guid}")
            .WithTags("Expenses")
            .RequireAuthorization();

        household.MapGet("/expenses", GetOverview);
        household.MapPost("/expenses", CreateExpense);
        household.MapPut("/expenses/{expenseId:guid}", UpdateExpense);
        household.MapDelete("/expenses/{expenseId:guid}", DeleteExpense);
        household.MapPost("/settlements", CreateSettlement);
        household.MapDelete("/settlements/{settlementId:guid}", DeleteSettlement);
        return api;
    }

    private static async Task<IResult> GetOverview(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(principal.GetUserId(), householdId, HouseholdModule.Expenses, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var expenses = await db.Expenses.AsNoTracking().Where(e => e.HouseholdId == householdId).ToListAsync(ct);
        var settlements = await db.Settlements.AsNoTracking().Where(s => s.HouseholdId == householdId).ToListAsync(ct);
        var balances = ExpenseMath.Balances(expenses, settlements);
        return Results.Ok(new ExpensesOverviewDto(
            [.. expenses.OrderByDescending(e => e.Date).ThenByDescending(e => e.CreatedAt).Take(Recent).Select(ToDto)],
            [.. settlements.OrderByDescending(s => s.Date).ThenByDescending(s => s.CreatedAt).Take(Recent).Select(ToDto)],
            [.. balances.Where(b => b.Value != 0).OrderByDescending(b => b.Value).Select(b => new BalanceDto(b.Key, b.Value))],
            ExpenseMath.Settle(balances)));
    }

    private static async Task<IResult> CreateExpense(
        Guid householdId,
        SaveExpenseRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Expenses, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        if (await SharesAsync(db, householdId, request, ct) is not { } shares)
        {
            return Invalid();
        }

        var now = clock.GetUtcNow();
        var expense = new Expense { HouseholdId = householdId, Description = request.Description.Trim(), CreatedByUserId = userId, CreatedAt = now };
        Apply(expense, request, shares, now);
        db.Expenses.Add(expense);
        activity.Capture(householdId, userId, HouseholdModule.Expenses,
            $"registou «{expense.Description}» ({Euros(expense.AmountCents)})", undoable: false, access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);
        await notifier.ExpensesChanged(householdId, ct);
        return Results.Created($"/api/households/{householdId}/expenses/{expense.Id}", ToDto(expense));
    }

    private static async Task<IResult> UpdateExpense(
        Guid householdId,
        Guid expenseId,
        SaveExpenseRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Expenses, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var expense = await db.Expenses.SingleOrDefaultAsync(e => e.Id == expenseId && e.HouseholdId == householdId, ct);
        if (expense is null)
        {
            return Results.NotFound();
        }

        if (level < ModuleAccess.Manage && expense.CreatedByUserId != userId)
        {
            return Results.Forbid();
        }

        if (await SharesAsync(db, householdId, request, ct) is not { } shares)
        {
            return Invalid();
        }

        Apply(expense, request, shares, clock.GetUtcNow());
        activity.Capture(householdId, userId, HouseholdModule.Expenses, $"mudou a despesa «{expense.Description}»", undoable: false,
            access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);
        await notifier.ExpensesChanged(householdId, ct);
        return Results.Ok(ToDto(expense));
    }

    private static async Task<IResult> DeleteExpense(
        Guid householdId,
        Guid expenseId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Expenses, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var expense = await db.Expenses.SingleOrDefaultAsync(e => e.Id == expenseId && e.HouseholdId == householdId, ct);
        if (expense is null)
        {
            return Results.NotFound();
        }

        if (level < ModuleAccess.Manage && expense.CreatedByUserId != userId)
        {
            return Results.Forbid();
        }

        db.Expenses.Remove(expense);
        activity.Capture(householdId, userId, HouseholdModule.Expenses, $"apagou a despesa «{expense.Description}»", undoable: false,
            access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);
        await notifier.ExpensesChanged(householdId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CreateSettlement(
        Guid householdId,
        SaveSettlementRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Expenses, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var people = new[] { request.FromUserId, request.ToUserId };
        if (request.FromUserId == request.ToUserId || !SupportedDates.IsSupported(request.Date)
            || await db.Memberships.CountAsync(m => m.HouseholdId == householdId && people.Contains(m.UserId), ct) != 2)
        {
            return Invalid();
        }

        var settlement = new Settlement
        {
            HouseholdId = householdId,
            FromUserId = request.FromUserId,
            ToUserId = request.ToUserId,
            AmountCents = request.AmountCents,
            Date = request.Date,
            CreatedByUserId = userId,
            CreatedAt = clock.GetUtcNow(),
        };
        db.Settlements.Add(settlement);
        activity.Capture(householdId, userId, HouseholdModule.Expenses, $"registou um acerto de {Euros(settlement.AmountCents)}",
            undoable: false, access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);
        await notifier.ExpensesChanged(householdId, ct);
        return Results.Created($"/api/households/{householdId}/settlements/{settlement.Id}", ToDto(settlement));
    }

    private static async Task<IResult> DeleteSettlement(
        Guid householdId,
        Guid settlementId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Expenses, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var settlement = await db.Settlements.SingleOrDefaultAsync(s => s.Id == settlementId && s.HouseholdId == householdId, ct);
        if (settlement is null)
        {
            return Results.NotFound();
        }

        if (level < ModuleAccess.Manage && settlement.CreatedByUserId != userId)
        {
            return Results.Forbid();
        }

        db.Settlements.Remove(settlement);
        activity.Capture(householdId, userId, HouseholdModule.Expenses, $"apagou um acerto de {Euros(settlement.AmountCents)}",
            undoable: false, access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);
        await notifier.ExpensesChanged(householdId, ct);
        return Results.NoContent();
    }

    /// <summary>As partes de cada um, já validadas (membros da casa, somam o total), ou null se o pedido não serve.</summary>
    private static async Task<List<ExpenseShare>?> SharesAsync(
        KubicoDbContext db, Guid householdId, SaveExpenseRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Description) || !SupportedDates.IsSupported(request.Date))
        {
            return null;
        }

        List<ExpenseShare> shares;
        if (request.Shares is { Count: > 0 } exact)
        {
            if (exact.Any(s => s.AmountCents < 0) || exact.Sum(s => (long)s.AmountCents) != request.AmountCents
                || exact.Select(s => s.UserId).Distinct().Count() != exact.Count)
            {
                return null;
            }

            shares = [.. exact.Where(s => s.AmountCents > 0).Select(s => new ExpenseShare { UserId = s.UserId, AmountCents = s.AmountCents })];
        }
        else
        {
            var people = (request.SplitWith is { Count: > 0 } with ? with : [request.PaidByUserId]).Distinct().ToList();
            shares = ExpenseMath.SplitEqually(request.AmountCents, people);
        }

        var involved = shares.Select(s => s.UserId).Append(request.PaidByUserId).Distinct().ToList();
        var members = await db.Memberships.CountAsync(m => m.HouseholdId == householdId && involved.Contains(m.UserId), ct);
        return members == involved.Count ? shares : null;
    }

    private static void Apply(Expense expense, SaveExpenseRequest request, List<ExpenseShare> shares, DateTimeOffset now)
    {
        expense.Description = request.Description.Trim();
        expense.AmountCents = request.AmountCents;
        expense.PaidByUserId = request.PaidByUserId;
        expense.Date = request.Date;
        expense.Category = string.IsNullOrWhiteSpace(request.Category) ? null : request.Category.Trim();
        expense.Shares = shares;
        expense.UpdatedAt = now;
    }

    private static IResult Invalid() => Results.ValidationProblem(new Dictionary<string, string[]>
    {
        ["Shares"] = ["Confirma quem pagou e entre quem se divide: têm de ser pessoas da casa e as partes têm de somar o total."],
    });

    private static string Euros(int cents) => $"{cents / 100m:0.00} €";

    private static ExpenseDto ToDto(Expense e) => new(
        e.Id, e.Description, e.AmountCents, e.PaidByUserId, e.Date, e.Category,
        [.. e.Shares.Select(s => new ShareDto(s.UserId, s.AmountCents))], e.CreatedByUserId, e.UpdatedAt);

    private static SettlementDto ToDto(Settlement s) => new(s.Id, s.FromUserId, s.ToUserId, s.AmountCents, s.Date);
}
