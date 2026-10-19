using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Kubico.Api.Common;
using Kubico.Api.Notifications;
using Kubico.Api.Realtime;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Chores;

/// <summary>
/// Criar ou substituir uma tarefa (PUT envia o estado completo). Com <c>Steps</c> é uma rotina: uma lista de passos
/// que se vão marcando; <c>TimeOfDay</c> diz se é da manhã ou da noite.
/// </summary>
public record SaveChoreRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Title,
    [property: StringLength(1000)] string? Notes = null,
    [property: Range(0, 1000)] int Points = 0,
    DateOnly? DueDate = null,
    ChoreRecurrence Recurrence = ChoreRecurrence.None,
    Guid? AssigneeUserId = null,
    IReadOnlyList<Guid>? RotationUserIds = null,
    ChoreTimeOfDay TimeOfDay = ChoreTimeOfDay.Any,
    IReadOnlyList<string>? Steps = null);

public record SetChoreStepRequest(bool Done);

public record ChoreDto(
    Guid Id,
    Guid HouseholdId,
    string Title,
    string? Notes,
    int Points,
    DateOnly? DueDate,
    ChoreRecurrence Recurrence,
    Guid? AssigneeUserId,
    IReadOnlyList<Guid> RotationUserIds,
    bool IsDone,
    DateTimeOffset? CompletedAt,
    DateTimeOffset UpdatedAt,
    ChoreTimeOfDay TimeOfDay = ChoreTimeOfDay.Any,
    IReadOnlyList<string>? Steps = null,
    IReadOnlyList<int>? DoneSteps = null);

public record ChoreCompletedDto(ChoreDto Chore, int PointsEarned);

/// <summary>Um passo marcado. <c>Completed</c>: era o último e a rotina ficou concluída (com os pontos).</summary>
public record ChoreStepResultDto(ChoreDto Chore, bool Completed, int PointsEarned);

public record MemberChoreStatsDto(Guid UserId, string DisplayName, string Color, int Completed, int Points);

public record ChoreStatsDto(DateOnly From, DateOnly To, IReadOnlyList<MemberChoreStatsDto> Members);

public static class ChoreEndpoints
{
    public static RouteGroupBuilder MapChoreEndpoints(this RouteGroupBuilder api)
    {
        var chores = api.MapGroup("/households/{householdId:guid}/chores")
            .WithTags("Chores")
            .RequireAuthorization();

        chores.MapGet("/", GetChores);
        chores.MapPost("/", CreateChore);
        chores.MapPut("/{choreId:guid}", UpdateChore);
        chores.MapDelete("/{choreId:guid}", DeleteChore);
        chores.MapPost("/{choreId:guid}/complete", CompleteChore);
        chores.MapPost("/{choreId:guid}/steps/{index:int}", SetStep);
        chores.MapGet("/stats", GetStats);

        return api;
    }

    private static async Task<IResult> GetChores(
        Guid householdId,
        bool? includeDone,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Chores, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var query = db.Chores.AsNoTracking().Where(c => c.HouseholdId == householdId);
        if (includeDone != true)
        {
            query = query.Where(c => c.CompletedAt == null);
        }

        var chores = await query.ToListAsync(ct);

        // Com prazo primeiro (mais próximo no topo), depois sem prazo, por título.
        var ordered = chores
            .OrderBy(c => c.IsDone)
            .ThenBy(c => c.DueDate is null)
            .ThenBy(c => c.DueDate)
            .ThenBy(c => c.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(ToDto)
            .ToList();

        return Results.Ok(ordered);
    }

    private static async Task<IResult> CreateChore(
        Guid householdId,
        SaveChoreRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        NotificationService notifications,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(
            userId, householdId, HouseholdModule.Chores, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        if (await ValidateMembers(db, householdId, request, ct) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        var chore = new Chore
        {
            HouseholdId = householdId,
            Title = request.Title,
            CreatedByUserId = userId,
            CreatedAt = now,
        };
        Apply(chore, request, now);

        db.Chores.Add(chore);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(chore);
        await notifier.ChoreUpserted(householdId, dto, ct);
        await NotifyAssignee(notifications, chore, null, userId, $"Tarefa para ti: {chore.Title}", ct);
        return Results.Created($"/api/households/{householdId}/chores/{chore.Id}", dto);
    }

    private static async Task<IResult> UpdateChore(
        Guid householdId,
        Guid choreId,
        SaveChoreRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        NotificationService notifications,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Chores, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var chore = await db.Chores.SingleOrDefaultAsync(c => c.Id == choreId && c.HouseholdId == householdId, ct);
        if (chore is null)
        {
            return Results.NotFound();
        }

        if (await ValidateMembers(db, householdId, request, ct) is { } invalid)
        {
            return invalid;
        }

        var previousAssignee = chore.AssigneeUserId;
        Apply(chore, request, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        var dto = ToDto(chore);
        await notifier.ChoreUpserted(householdId, dto, ct);
        await NotifyAssignee(
            notifications, chore, previousAssignee, principal.GetUserId(), $"Tarefa para ti: {chore.Title}", ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteChore(
        Guid householdId,
        Guid choreId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Chores, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var chore = await db.Chores.SingleOrDefaultAsync(c => c.Id == choreId && c.HouseholdId == householdId, ct);
        if (chore is null)
        {
            return Results.NotFound();
        }

        db.Chores.Remove(chore);
        await db.SaveChangesAsync(ct);
        await notifier.ChoreDeleted(householdId, choreId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> CompleteChore(
        Guid householdId,
        Guid choreId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        NotificationService notifications,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(
            userId, householdId, HouseholdModule.Chores, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var chore = await db.Chores
            .Include(c => c.Household)
            .SingleOrDefaultAsync(c => c.Id == choreId && c.HouseholdId == householdId, ct);
        if (chore is null)
        {
            return Results.NotFound();
        }

        if (CannotAct(chore, userId, level) is { } refused)
        {
            return refused;
        }

        var completion = await CompleteAsync(db, notifier, notifications, chore, userId, clock.GetUtcNow(), ct);
        return Results.Ok(new ChoreCompletedDto(ToDto(chore), completion.Points));
    }

    /// <summary>Marca ou desmarca um passo de uma rotina; o último passo conclui-a.</summary>
    private static async Task<IResult> SetStep(
        Guid householdId,
        Guid choreId,
        int index,
        SetChoreStepRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        NotificationService notifications,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(
            userId, householdId, HouseholdModule.Chores, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var chore = await db.Chores
            .Include(c => c.Household)
            .SingleOrDefaultAsync(c => c.Id == choreId && c.HouseholdId == householdId, ct);
        if (chore is null)
        {
            return Results.NotFound();
        }

        if (CannotAct(chore, userId, level) is { } refused)
        {
            return refused;
        }

        var now = clock.GetUtcNow();
        if (!chore.SetStep(index, request.Done, now))
        {
            return Results.NotFound();
        }

        if (chore.AllStepsDone)
        {
            var completion = await CompleteAsync(db, notifier, notifications, chore, userId, now, ct);
            return Results.Ok(new ChoreStepResultDto(ToDto(chore), true, completion.Points));
        }

        await db.SaveChangesAsync(ct);
        var dto = ToDto(chore);
        await notifier.ChoreUpserted(householdId, dto, ct);
        return Results.Ok(new ChoreStepResultDto(dto, false, 0));
    }

    /// <summary>Já concluída, ou (para quem não gere as tarefas) de outra pessoa.</summary>
    private static IResult? CannotAct(Chore chore, Guid userId, ModuleAccess level)
    {
        if (chore.IsDone)
        {
            return Results.Problem(title: "Esta tarefa já está concluída.", statusCode: StatusCodes.Status409Conflict);
        }

        // Crianças e convidados só concluem as suas tarefas ou as que são de qualquer pessoa.
        if (level < ModuleAccess.Manage && chore.AssigneeUserId is { } assignee && assignee != userId)
        {
            return Results.Problem(
                title: "Esta tarefa está atribuída a outra pessoa.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    /// <summary>Conclui a tarefa (a rotação salta quem está fora), grava e avisa a casa e o próximo responsável.</summary>
    private static async Task<ChoreCompletion> CompleteAsync(
        KubicoDbContext db,
        HouseholdNotifier notifier,
        NotificationService notifications,
        Chore chore,
        Guid userId,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var today = chore.Household.Today(now);
        var previousAssignee = chore.AssigneeUserId;
        var absences = await db.MemberAbsences.AsNoTracking()
            .Where(a => a.HouseholdId == chore.HouseholdId && a.EndDate >= today)
            .ToListAsync(ct);
        var completion = chore.Complete(userId, now, today, MemberAbsence.Checker(absences));

        // O Id já vem preenchido; sem Add explícito o EF trataria o registo como existente (UPDATE).
        db.ChoreCompletions.Add(completion);
        await db.SaveChangesAsync(ct);

        await notifier.ChoreUpserted(chore.HouseholdId, ToDto(chore), ct);
        await NotifyAssignee(notifications, chore, previousAssignee, userId, $"É a tua vez: {chore.Title}", ct);
        return completion;
    }

    private static async Task<IResult> GetStats(
        Guid householdId,
        int? days,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Chores, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var period = Math.Clamp(days ?? 30, 1, 365);
        var now = clock.GetUtcNow();
        var since = now.AddDays(-period);

        var members = await db.Memberships.AsNoTracking()
            .Where(m => m.HouseholdId == householdId)
            .Select(m => new { m.UserId, m.User.DisplayName, m.Color })
            .ToListAsync(ct);

        var completions = await db.ChoreCompletions.AsNoTracking()
            .Where(c => c.Chore.HouseholdId == householdId && c.CompletedAt >= since)
            .Select(c => new { c.UserId, c.Points })
            .ToListAsync(ct);

        var byUser = completions.GroupBy(c => c.UserId).ToDictionary(g => g.Key, g => (Count: g.Count(), Points: g.Sum(c => c.Points)));

        var stats = members
            .Select(m =>
            {
                var (count, points) = byUser.GetValueOrDefault(m.UserId);
                return new MemberChoreStatsDto(m.UserId, m.DisplayName, m.Color, count, points);
            })
            .OrderByDescending(s => s.Points)
            .ThenByDescending(s => s.Completed)
            .ThenBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        var today = household.Today(now);
        return Results.Ok(new ChoreStatsDto(today.AddDays(-period), today, stats));
    }

    /// <summary>Avisa quem passou a ser responsável pela tarefa (atribuída, ou a sua vez na rotação).</summary>
    internal static Task NotifyAssignee(
        NotificationService notifications, Chore chore, Guid? previousAssignee, Guid actorId, string title, CancellationToken ct)
    {
        if (chore.AssigneeUserId is not { } assignee || assignee == previousAssignee)
        {
            return Task.CompletedTask;
        }

        var due = chore.DueDate is { } date ? "Prazo: " + date.ToString("dd/MM", CultureInfo.InvariantCulture) : null;
        return notifications.NotifyAsync(
            chore.HouseholdId, [assignee], actorId, NotificationKind.ChoreAssigned, title, due, chore.Id, ct);
    }

    /// <summary>O responsável e a rotação têm de ser membros desta casa.</summary>
    private static async Task<IResult?> ValidateMembers(
        KubicoDbContext db, Guid householdId, SaveChoreRequest request, CancellationToken ct)
    {
        var rotation = request.RotationUserIds ?? [];
        var referenced = rotation.Select(id => (Guid?)id).Append(request.AssigneeUserId).OfType<Guid>().Distinct().ToList();
        var memberIds = referenced.Count == 0
            ? []
            : await db.Memberships
                .Where(m => m.HouseholdId == householdId && referenced.Contains(m.UserId))
                .Select(m => m.UserId)
                .ToListAsync(ct);

        var errors = new Dictionary<string, string[]>();
        if (referenced.Except(memberIds).Any())
        {
            errors["AssigneeUserId"] = ["O responsável e a rotação têm de ser membros desta casa."];
        }

        if (rotation.Count != rotation.Distinct().Count())
        {
            errors["RotationUserIds"] = ["Cada membro só pode aparecer uma vez na rotação."];
        }

        if (rotation.Count > 0 && request.Recurrence == ChoreRecurrence.None)
        {
            errors["RotationUserIds"] = ["A rotação só faz sentido em tarefas que se repetem."];
        }

        var steps = CleanSteps(request.Steps);
        if (steps.Count > MaxSteps)
        {
            errors["Steps"] = [$"Uma rotina tem no máximo {MaxSteps} passos."];
        }
        else if (steps.Any(s => s.Length > 100))
        {
            errors["Steps"] = ["Cada passo tem no máximo 100 letras."];
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private static void Apply(Chore chore, SaveChoreRequest request, DateTimeOffset now)
    {
        chore.Title = request.Title.Trim();
        chore.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        chore.Points = request.Points;
        chore.DueDate = request.DueDate;
        chore.Recurrence = request.Recurrence;
        chore.RotationUserIds = [.. request.RotationUserIds ?? []];
        chore.TimeOfDay = request.TimeOfDay;
        chore.SetSteps(CleanSteps(request.Steps));

        // Com rotação e sem responsável indicado, começa pelo primeiro da lista.
        chore.AssigneeUserId = request.AssigneeUserId
            ?? (chore.RotationUserIds.Count > 0 ? chore.RotationUserIds[0] : null);

        chore.UpdatedAt = now;
    }

    private const int MaxSteps = 20;

    private static List<string> CleanSteps(IReadOnlyList<string>? steps) =>
        [.. (steps ?? []).Select(s => s.Trim()).Where(s => s.Length > 0)];

    internal static ChoreDto ToDto(Chore c) => new(
        c.Id, c.HouseholdId, c.Title, c.Notes, c.Points, c.DueDate, c.Recurrence,
        c.AssigneeUserId, c.RotationUserIds, c.IsDone, c.CompletedAt, c.UpdatedAt, c.TimeOfDay, c.Steps, c.DoneSteps);
}
