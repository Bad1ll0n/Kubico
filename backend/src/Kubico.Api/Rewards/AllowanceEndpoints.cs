using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Activity;
using Kubico.Api.Common;
using Kubico.Api.Notifications;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Kubico.Domain.Rewards;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Rewards;

public record SaveAllowanceRequest(
    [property: Range(0, 100_000)] int AmountCents,
    [property: Range(0, 10_000)] int BonusPerChoreCents = 0,
    AllowancePeriod Period = AllowancePeriod.Weekly);

public record PayAllowanceRequest(DateOnly PeriodStart);

/// <summary>
/// Um período da mesada. Os que já acabaram e não foram pagos estão "por pagar"; o atual (<c>IsCurrent</c>) vai
/// crescendo com as tarefas feitas. Os pagos mostram o que se pagou (<c>AmountCents</c>); se se pagou antes de o
/// período acabar e depois houve mais tarefas, <c>OwedCents</c> é o extra que ainda falta pagar.
/// </summary>
public record AllowancePeriodDto(
    DateOnly Start,
    DateOnly End,
    int ChoresDone,
    int AmountCents,
    bool IsCurrent,
    Guid? PaymentId,
    DateTimeOffset? PaidAt,
    int OwedCents = 0);

/// <summary>A mesada de um membro e os seus últimos períodos (do mais recente para o mais antigo).</summary>
public record AllowanceDto(
    Guid UserId,
    int AmountCents,
    int BonusPerChoreCents,
    AllowancePeriod Period,
    DateOnly StartDate,
    int OwedCents,
    IReadOnlyList<AllowancePeriodDto> Periods);

/// <summary>
/// Mesada: quem gere as recompensas define-a para cada membro e marca os períodos como pagos; cada um vê a sua.
/// </summary>
public static class AllowanceEndpoints
{
    /// <summary>Quantos períodos se mostram (meio ano de semanas, um ano de meses).</summary>
    private const int MaxWeeks = 26;
    private const int MaxMonths = 12;

    public static RouteGroupBuilder MapAllowanceEndpoints(this RouteGroupBuilder api)
    {
        var allowances = api.MapGroup("/households/{householdId:guid}/allowances")
            .WithTags("Rewards")
            .RequireAuthorization();

        allowances.MapGet("/", GetAllowances);
        allowances.MapPut("/{userId:guid}", SaveAllowance);
        allowances.MapDelete("/{userId:guid}", StopAllowance);
        allowances.MapPost("/{userId:guid}/payments", Pay);
        allowances.MapDelete("/{userId:guid}/payments/{paymentId:guid}", UndoPayment);
        return api;
    }

    /// <summary>Quem gere as recompensas vê todas; os outros, só a sua.</summary>
    private static async Task<IResult> GetAllowances(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        TimeProvider clock,
        CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(me, householdId, HouseholdModule.Rewards, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var allowances = await db.Allowances.AsNoTracking()
            .Where(a => a.HouseholdId == householdId && (level >= ModuleAccess.Manage || a.UserId == me))
            .ToListAsync(ct);
        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        var result = new List<AllowanceDto>();
        foreach (var allowance in allowances)
        {
            result.Add(await DescribeAsync(db, household, allowance, clock.GetUtcNow(), ct));
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> SaveAllowance(
        Guid householdId,
        Guid userId,
        SaveAllowanceRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Rewards, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        if (!Enum.IsDefined(request.Period))
        {
            return Invalid("Escolhe semanal ou mensal.");
        }

        if (!await db.Memberships.AnyAsync(m => m.HouseholdId == householdId && m.UserId == userId, ct))
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        var today = household.Today(now);
        var allowance = await db.Allowances.SingleOrDefaultAsync(a => a.HouseholdId == householdId && a.UserId == userId, ct);
        if (allowance is null)
        {
            allowance = new Allowance { HouseholdId = householdId, UserId = userId, StartDate = Allowance.PeriodStart(today, request.Period) };
            db.Allowances.Add(allowance);
        }
        else if (allowance.Period != request.Period)
        {
            // Passar de semanal para mensal (ou o contrário) começa a contar do período atual.
            allowance.StartDate = Allowance.PeriodStart(today, request.Period);
        }

        allowance.AmountCents = request.AmountCents;
        allowance.BonusPerChoreCents = request.BonusPerChoreCents;
        allowance.Period = request.Period;
        allowance.UpdatedAt = now;
        activity.Capture(householdId, principal.GetUserId(), HouseholdModule.Rewards,
            $"definiu a mesada de {await NameAsync(db, userId, ct)}", undoable: false, privateTo: userId);
        await db.SaveChangesAsync(ct);

        await notifier.AllowanceChanged(householdId, userId, ct);
        return Results.Ok(await DescribeAsync(db, household, allowance, now, ct));
    }

    /// <summary>Deixa de haver mesada; o que já se pagou fica no histórico.</summary>
    private static async Task<IResult> StopAllowance(
        Guid householdId,
        Guid userId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Rewards, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var allowance = await db.Allowances.SingleOrDefaultAsync(a => a.HouseholdId == householdId && a.UserId == userId, ct);
        if (allowance is null)
        {
            return Results.NotFound();
        }

        db.Allowances.Remove(allowance);
        activity.Capture(householdId, principal.GetUserId(), HouseholdModule.Rewards,
            $"acabou com a mesada de {await NameAsync(db, userId, ct)}", undoable: false, privateTo: userId);
        await db.SaveChangesAsync(ct);
        await notifier.AllowanceChanged(householdId, userId, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Marca um período como pago, com o valor que tem agora; a pessoa é avisada. Um período já pago só se volta a
    /// pagar se depois houve mais tarefas (o extra que falta). Dois pagamentos em simultâneo: um passa, o outro 409.
    /// </summary>
    private static async Task<IResult> Pay(
        Guid householdId,
        Guid userId,
        PayAllowanceRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        NotificationService notifications,
        TimeProvider clock,
        CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(me, householdId, HouseholdModule.Rewards, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var allowance = await db.Allowances.AsNoTracking().SingleOrDefaultAsync(a => a.HouseholdId == householdId && a.UserId == userId, ct);
        if (allowance is null)
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        var start = request.PeriodStart;
        if (Allowance.PeriodStart(start, allowance.Period) != start
            || start < Allowance.PeriodStart(allowance.StartDate, allowance.Period)
            || start > household.Today(now))
        {
            return Invalid("Esse período não é desta mesada.");
        }

        var name = await NameAsync(db, userId, ct);
        var paid = await db.AllowancePayments.AsNoTracking()
            .SingleOrDefaultAsync(p => p.HouseholdId == householdId && p.UserId == userId && p.PeriodStart == start, ct);
        if (paid is not null)
        {
            return await PayRestAsync(db, activity, notifier, notifications, household, allowance, paid, name, me, now, ct);
        }

        var end = Allowance.PeriodEnd(start, allowance.Period);
        var done = await ChoresDoneAsync(db, household, userId, start, end, ct);
        var payment = new AllowancePayment
        {
            HouseholdId = householdId,
            UserId = userId,
            PeriodStart = start,
            PeriodEnd = end,
            ChoresDone = done,
            AmountCents = allowance.AmountFor(done),
            BonusPerChoreCents = allowance.BonusPerChoreCents,
            PaidAt = now,
            PaidByUserId = me,
        };
        db.AllowancePayments.Add(payment);
        activity.Capture(householdId, me, HouseholdModule.Rewards,
            $"pagou a mesada de {name}: {Allowance.Format(payment.AmountCents)}", undoable: false, privateTo: userId);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e is not DbUpdateConcurrencyException)
        {
            // Outro pagamento do mesmo período chegou ao mesmo tempo (índice único): fica esse.
            db.ChangeTracker.Clear();
            if (!await db.AllowancePayments.AnyAsync(p => p.HouseholdId == householdId && p.UserId == userId && p.PeriodStart == start, ct))
            {
                throw;
            }

            return AlreadyPaid();
        }

        await notifier.AllowanceChanged(householdId, userId, ct);
        await notifications.NotifyAsync(
            householdId, [userId], me, NotificationKind.AllowancePaid,
            $"Recebeste a mesada: {Allowance.Format(payment.AmountCents)}",
            done > 0 ? $"Com {done} {(done == 1 ? "tarefa feita" : "tarefas feitas")}." : null,
            null, ct);
        return Results.Ok(await DescribeAsync(db, household, allowance, now, ct));
    }

    /// <summary>
    /// Um período pago antes de acabar teve mais tarefas depois: paga-se o extra delas (ao valor do pagamento). A
    /// atualização só passa se o pagamento não mudou entretanto.
    /// </summary>
    private static async Task<IResult> PayRestAsync(
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdNotifier notifier,
        NotificationService notifications,
        Household household,
        Allowance allowance,
        AllowancePayment paid,
        string name,
        Guid me,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var done = await ChoresDoneAsync(db, household, paid.UserId, paid.PeriodStart, paid.PeriodEnd, ct);
        var extra = Math.Max(0, done - paid.ChoresDone) * paid.BonusPerChoreCents;
        if (extra == 0)
        {
            return AlreadyPaid();
        }

        var updated = await db.AllowancePayments
            .Where(p => p.Id == paid.Id && p.ChoresDone == paid.ChoresDone)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.AmountCents, p => p.AmountCents + extra)
                .SetProperty(p => p.ChoresDone, done)
                .SetProperty(p => p.PaidAt, now)
                .SetProperty(p => p.PaidByUserId, me), ct);
        if (updated == 0)
        {
            return AlreadyPaid();
        }

        activity.Capture(household.Id, me, HouseholdModule.Rewards,
            $"pagou o resto da mesada de {name}: {Allowance.Format(extra)}", undoable: false, privateTo: paid.UserId);
        await db.SaveChangesAsync(ct);
        await notifier.AllowanceChanged(household.Id, paid.UserId, ct);
        await notifications.NotifyAsync(
            household.Id, [paid.UserId], me, NotificationKind.AllowancePaid,
            $"Recebeste o resto da mesada: {Allowance.Format(extra)}", null, null, ct);
        return Results.Ok(await DescribeAsync(db, household, allowance, now, ct));
    }

    /// <summary>Desfaz um pagamento marcado por engano: o período volta a estar por pagar.</summary>
    private static async Task<IResult> UndoPayment(
        Guid householdId,
        Guid userId,
        Guid paymentId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Rewards, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var payment = await db.AllowancePayments.SingleOrDefaultAsync(
            p => p.Id == paymentId && p.HouseholdId == householdId && p.UserId == userId, ct);
        if (payment is null)
        {
            return Results.NotFound();
        }

        db.AllowancePayments.Remove(payment);
        activity.Capture(householdId, principal.GetUserId(), HouseholdModule.Rewards,
            $"anulou um pagamento da mesada de {await NameAsync(db, userId, ct)}", undoable: false, privateTo: userId);
        await db.SaveChangesAsync(ct);
        await notifier.AllowanceChanged(householdId, userId, ct);
        return Results.NoContent();
    }

    private static async Task<AllowanceDto> DescribeAsync(
        KubicoDbContext db, Household household, Allowance allowance, DateTimeOffset now, CancellationToken ct)
    {
        var today = household.Today(now);
        var starts = allowance.PeriodStarts(today, allowance.Period == AllowancePeriod.Weekly ? MaxWeeks : MaxMonths).ToList();
        var oldest = starts.Count == 0 ? today : starts[^1];
        var payments = await db.AllowancePayments.AsNoTracking()
            .Where(p => p.HouseholdId == household.Id && p.UserId == allowance.UserId && p.PeriodStart >= oldest)
            .ToListAsync(ct);
        var completions = await CompletionTimesAsync(db, household.Id, allowance.UserId, StartOfDay(household, oldest), ct);

        var periods = starts.Select(start =>
        {
            var end = Allowance.PeriodEnd(start, allowance.Period);
            var (from, to) = (StartOfDay(household, start), StartOfDay(household, end.AddDays(1)));
            var done = completions.Count(t => t >= from && t < to);
            if (payments.FirstOrDefault(p => p.PeriodStart == start) is { } paid)
            {
                // Pago antes de acabar: as tarefas feitas depois ficam por pagar (ao extra desse pagamento).
                var paidTo = StartOfDay(household, paid.PeriodEnd.AddDays(1));
                var doneNow = completions.Count(t => t >= from && t < paidTo);
                var rest = Math.Max(0, doneNow - paid.ChoresDone) * paid.BonusPerChoreCents;
                return new AllowancePeriodDto(
                    start, paid.PeriodEnd, Math.Max(doneNow, paid.ChoresDone), paid.AmountCents, end >= today, paid.Id, paid.PaidAt, rest);
            }

            var amount = allowance.AmountFor(done);
            return new AllowancePeriodDto(start, end, done, amount, end >= today, null, null, amount);
        }).ToList();

        // Por pagar: o que falta dos períodos que já acabaram (não pagos, ou com tarefas feitas depois de pagos).
        var owed = periods.Where(p => !p.IsCurrent).Sum(p => p.OwedCents);
        return new AllowanceDto(
            allowance.UserId, allowance.AmountCents, allowance.BonusPerChoreCents, allowance.Period, allowance.StartDate,
            owed, periods);
    }

    private static async Task<int> ChoresDoneAsync(
        KubicoDbContext db, Household household, Guid userId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var (from, to) = (StartOfDay(household, start), StartOfDay(household, end.AddDays(1)));
        return (await CompletionTimesAsync(db, household.Id, userId, from, ct)).Count(t => t >= from && t < to);
    }

    /// <summary>Quando a pessoa concluiu tarefas (aceites) desde <paramref name="since"/>.</summary>
    private static async Task<List<DateTimeOffset>> CompletionTimesAsync(
        KubicoDbContext db, Guid householdId, Guid userId, DateTimeOffset since, CancellationToken ct)
    {
        return await db.ChoreCompletions.AsNoTracking()
            .Where(c => c.Chore.HouseholdId == householdId && c.UserId == userId && c.RejectedAt == null && c.CompletedAt >= since)
            .Select(c => c.CompletedAt)
            .ToListAsync(ct);
    }

    /// <summary>A meia-noite deste dia no fuso da casa, em UTC (como os instantes se guardam e comparam).</summary>
    private static DateTimeOffset StartOfDay(Household household, DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, household.TimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    private static IResult Invalid(string title) => Results.Problem(title: title, statusCode: StatusCodes.Status400BadRequest);

    private static IResult AlreadyPaid() =>
        Results.Problem(title: "Esse período já está pago.", statusCode: StatusCodes.Status409Conflict);

    private static async Task<string> NameAsync(KubicoDbContext db, Guid userId, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == userId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct) ?? "antigo membro";
}
