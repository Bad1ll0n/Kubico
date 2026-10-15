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

/// <summary>Criar ou substituir uma tarefa (PUT envia o estado completo).</summary>
public record SaveChoreRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Title,
    [property: StringLength(1000)] string? Notes = null,
    [property: Range(0, 1000)] int Points = 0,
    DateOnly? DueDate = null,
    ChoreRecurrence Recurrence = ChoreRecurrence.None,
    Guid? AssigneeUserId = null,
    IReadOnlyList<Guid>? RotationUserIds = null);

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
    DateTimeOffset UpdatedAt);

public record ChoreCompletedDto(ChoreDto Chore, int PointsEarned);

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
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct);
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
        var (_, error) = await access.RequireMemberAsync(userId, householdId, ct, r => r.CanManageContent());
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
        var (_, error) = await access.RequireMemberAsync(
            principal.GetUserId(), householdId, ct, r => r.CanManageContent());
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
        var (_, error) = await access.RequireMemberAsync(
            principal.GetUserId(), householdId, ct, r => r.CanManageContent());
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
        var (role, error) = await access.RequireMemberAsync(userId, householdId, ct);
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

        if (chore.IsDone)
        {
            return Results.Problem(title: "Esta tarefa já está concluída.", statusCode: StatusCodes.Status409Conflict);
        }

        // Crianças e convidados só concluem as suas tarefas ou as que são de qualquer pessoa.
        if (!role!.Value.CanManageContent() && chore.AssigneeUserId is { } assignee && assignee != userId)
        {
            return Results.Problem(
                title: "Esta tarefa está atribuída a outra pessoa.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var now = clock.GetUtcNow();
        var previousAssignee = chore.AssigneeUserId;
        var completion = chore.Complete(userId, now, chore.Household.Today(now));

        // O Id já vem preenchido; sem Add explícito o EF trataria o registo como existente (UPDATE).
        db.ChoreCompletions.Add(completion);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(chore);
        await notifier.ChoreUpserted(householdId, dto, ct);
        await NotifyAssignee(notifications, chore, previousAssignee, userId, $"É a tua vez: {chore.Title}", ct);
        return Results.Ok(new ChoreCompletedDto(dto, completion.Points));
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
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct);
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
    private static Task NotifyAssignee(
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
        if (referenced.Count == 0)
        {
            return null;
        }

        var memberIds = await db.Memberships
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

        // Com rotação e sem responsável indicado, começa pelo primeiro da lista.
        chore.AssigneeUserId = request.AssigneeUserId
            ?? (chore.RotationUserIds.Count > 0 ? chore.RotationUserIds[0] : null);

        chore.UpdatedAt = now;
    }

    private static ChoreDto ToDto(Chore c) => new(
        c.Id, c.HouseholdId, c.Title, c.Notes, c.Points, c.DueDate, c.Recurrence,
        c.AssigneeUserId, c.RotationUserIds, c.IsDone, c.CompletedAt, c.UpdatedAt);
}
