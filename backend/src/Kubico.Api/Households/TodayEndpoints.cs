using System.Security.Claims;
using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Common;
using Kubico.Api.Rewards;
using Kubico.Api.Shopping;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Households;

/// <summary>
/// O ecrã Hoje (e o painel de cozinha) num só pedido, em vez de seis (F4-19). Cada parte é a resposta do endpoint da
/// sua área, com as mesmas permissões: o que a pessoa não vê vem a null.
/// </summary>
public sealed record TodayDto(
    object Household,
    object Absences,
    object? Chores,
    object? Calendar,
    object? Lists,
    object? Rewards);

public static class TodayEndpoints
{
    public static RouteGroupBuilder MapTodayEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/households/{householdId:guid}/today", GetToday)
            .WithTags("Households")
            .RequireAuthorization();
        return api;
    }

    private static async Task<IResult> GetToday(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct)
    {
        // Não-membros recebem 404, como em todos os endpoints da casa.
        var household = await HouseholdEndpoints.GetHousehold(householdId, principal, db, access, clock, ct);
        if (Value(household) is not { } details)
        {
            return household;
        }

        var today = (await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct))
            .Today(clock.GetUtcNow());
        var absences = await AbsenceEndpoints.GetAbsences(householdId, principal, db, access, clock, ct);
        var chores = await ChoreEndpoints.GetChores(householdId, null, principal, db, access, ct);
        var calendar = await CalendarEndpoints.GetCalendar(
            householdId, today, today.AddDays(1), principal, db, access, http, ct);
        var lists = await ShoppingEndpoints.GetLists(householdId, principal, db, access, ct);
        var rewards = await RewardEndpoints.GetRewards(householdId, principal, db, access, ct);

        return Results.Ok(new TodayDto(
            details, Value(absences) ?? Array.Empty<object>(), Value(chores), Value(calendar), Value(lists), Value(rewards)));
    }

    /// <summary>O conteúdo de uma resposta 200; null para as outras (sem acesso à área).</summary>
    private static object? Value(IResult result) =>
        result is IStatusCodeHttpResult { StatusCode: 200 } and IValueHttpResult { Value: var value } ? value : null;
}
