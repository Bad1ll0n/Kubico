using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Kubico.Api.Activity;
using Kubico.Api.Chores;
using Kubico.Api.Common;
using Kubico.Api.Notifications;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Households;

public record SaveAbsenceRequest(
    Guid UserId,
    DateOnly StartDate,
    DateOnly EndDate,
    [property: StringLength(200)] string? Reason = null);

public record AbsenceDto(Guid Id, Guid HouseholdId, Guid UserId, DateOnly StartDate, DateOnly EndDate, string? Reason);

/// <summary>
/// Ausências: quem está fora e até quando. Cada membro marca as suas (as das crianças, marca-as um adulto); quem gere
/// as tarefas marca as de qualquer pessoa, porque mexem nas tarefas da casa: ao marcar, as tarefas da pessoa que caem
/// nesses dias passam a quem vem a seguir na rotação, e as que não têm a quem passar ficam em pausa até ela voltar.
/// </summary>
public static class AbsenceEndpoints
{
    /// <summary>Uma ausência dura no máximo um ano.</summary>
    private const int MaxDays = 366;

    public static RouteGroupBuilder MapAbsenceEndpoints(this RouteGroupBuilder api)
    {
        var absences = api.MapGroup("/households/{householdId:guid}/absences")
            .WithTags("Households")
            .RequireAuthorization();

        absences.MapGet("/", GetAbsences);
        absences.MapPost("/", CreateAbsence);
        absences.MapDelete("/{absenceId:guid}", DeleteAbsence);
        return api;
    }

    /// <summary>As ausências que ainda não acabaram (as de hoje e as futuras), pela ordem em que começam.</summary>
    private static async Task<IResult> GetAbsences(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, TimeProvider clock,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct);
        if (error is not null)
        {
            return error;
        }

        var today = (await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct)).Today(clock.GetUtcNow());
        var absences = await db.MemberAbsences.AsNoTracking()
            .Where(a => a.HouseholdId == householdId && a.EndDate >= today)
            .OrderBy(a => a.StartDate)
            .ToListAsync(ct);
        return Results.Ok(absences.Select(ToDto));
    }

    private static async Task<IResult> CreateAbsence(
        Guid householdId,
        SaveAbsenceRequest request,
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
        var (role, error) = await access.RequireMemberAsync(me, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        if (await CannotChange(access, householdId, role!.Value, me, request.UserId, ct) is { } refused)
        {
            return refused;
        }

        var member = await db.Memberships.Include(m => m.User)
            .SingleOrDefaultAsync(m => m.HouseholdId == householdId && m.UserId == request.UserId, ct);
        if (member is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["UserId"] = ["Escolhe um membro desta casa."],
            });
        }

        if (request.EndDate < request.StartDate || request.EndDate.DayNumber - request.StartDate.DayNumber >= MaxDays)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["EndDate"] = ["Indica até quando (no máximo um ano, e não antes do primeiro dia)."],
            });
        }

        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        var now = clock.GetUtcNow();
        var today = household.Today(now);
        if (request.EndDate < today)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["EndDate"] = ["Essa ausência já acabou."],
            });
        }

        // Tranca o membro (dois pedidos iguais em simultâneo não criam duas ausências) e não deixa sobrepor dias.
        db.Touch(member);
        if (await db.MemberAbsences.AnyAsync(
            a => a.HouseholdId == householdId && a.UserId == request.UserId
                && a.StartDate <= request.EndDate && a.EndDate >= request.StartDate, ct))
        {
            return Results.Problem(
                title: "Já está marcada uma ausência nesses dias.", statusCode: StatusCodes.Status409Conflict);
        }

        var absence = new MemberAbsence
        {
            HouseholdId = householdId,
            UserId = request.UserId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
            CreatedByUserId = me,
            CreatedAt = now,
        };
        db.MemberAbsences.Add(absence);

        // As tarefas que lhe cabiam nesses dias passam a quem vem a seguir na rotação e está cá; as outras ficam em
        // pausa até ao dia a seguir ao regresso.
        var all = await db.MemberAbsences
            .Where(a => a.HouseholdId == householdId && a.EndDate >= today)
            .ToListAsync(ct);
        var isAway = MemberAbsence.Checker([.. all, absence]);
        var chores = await db.Chores
            .Where(c => c.HouseholdId == householdId && c.CompletedAt == null && c.AssigneeUserId == request.UserId)
            .ToListAsync(ct);
        var moved = chores.Where(c => c.SkipAway(isAway, today, now) || c.PauseFor(absence, today, now)).ToList();
        activity.Capture(householdId, me, null, request.UserId == me
            ? $"vai estar fora de {Day(absence.StartDate)} a {Day(absence.EndDate)}"
            : $"marcou a ausência de {member.User.DisplayName} de {Day(absence.StartDate)} a {Day(absence.EndDate)}", undoable: false);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(absence);
        await notifier.AbsenceUpserted(householdId, dto, ct);
        var name = member.User.DisplayName;
        foreach (var chore in moved)
        {
            await notifier.ChoreUpserted(householdId, ChoreEndpoints.ToDto(chore), ct);
            await ChoreEndpoints.NotifyAssignee(
                notifications, chore, request.UserId, me, $"Tarefa para ti: {chore.Title} ({name} está fora)", ct);
        }

        return Results.Created($"/api/households/{householdId}/absences/{absence.Id}", dto);
    }

    /// <summary>Cancela a ausência (por exemplo, a pessoa voltou mais cedo). As tarefas que já mudaram ficam.</summary>
    private static async Task<IResult> DeleteAbsence(
        Guid householdId,
        Guid absenceId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (role, error) = await access.RequireMemberAsync(me, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        var absence = await db.MemberAbsences.SingleOrDefaultAsync(a => a.Id == absenceId && a.HouseholdId == householdId, ct);
        if (absence is null)
        {
            return Results.NotFound();
        }

        if (await CannotChange(access, householdId, role!.Value, me, absence.UserId, ct) is { } refused)
        {
            return refused;
        }

        db.MemberAbsences.Remove(absence);
        var name = await db.Users.Where(u => u.Id == absence.UserId).Select(u => u.DisplayName).SingleAsync(ct);
        activity.Capture(householdId, me, null,
            absence.UserId == me ? "cancelou a sua ausência" : $"cancelou a ausência de {name}", undoable: false);
        await db.SaveChangesAsync(ct);
        await notifier.AbsenceDeleted(householdId, absenceId, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// As ausências mexem nas tarefas da casa: as dos outros só as marca (ou cancela) quem gere as tarefas; as próprias,
    /// cada um, menos as crianças (marca-as um adulto).
    /// </summary>
    private static async Task<IResult?> CannotChange(
        HouseholdAccess access, Guid householdId, HouseholdRole role, Guid me, Guid absentUserId, CancellationToken ct)
    {
        var manages = await access.GetAccessAsync(householdId, role, HouseholdModule.Chores, ct) >= ModuleAccess.Manage;
        if (absentUserId != me && !manages)
        {
            return Results.Problem(
                title: "Só quem gere as tarefas marca ou cancela as ausências dos outros.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (absentUserId == me && role == HouseholdRole.Child && !manages)
        {
            return Results.Problem(
                title: "As ausências das crianças são marcadas por um adulto da casa.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static string Day(DateOnly date) => date.ToString("dd/MM", CultureInfo.InvariantCulture);

    private static AbsenceDto ToDto(MemberAbsence a) => new(a.Id, a.HouseholdId, a.UserId, a.StartDate, a.EndDate, a.Reason);
}
