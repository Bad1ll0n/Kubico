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

public record ChorePhotoDto(Guid Id);

/// <summary>Uma tarefa feita. <c>Rejected</c>: quem gere as tarefas não a aceitou (não conta para os pontos).</summary>
public record ChoreHistoryItemDto(
    Guid Id,
    Guid ChoreId,
    string ChoreTitle,
    Guid UserId,
    int Points,
    DateTimeOffset CompletedAt,
    Guid? PhotoId,
    bool Rejected);

/// <summary>
/// Fotos de prova e histórico: quem fez o quê, com a foto quando a tarefa a pede. Quem gere as tarefas pode não
/// aceitar uma conclusão: os pontos deixam de contar e a tarefa volta para quem a fez.
/// </summary>
public static class ChoreHistoryEndpoints
{
    public static RouteGroupBuilder MapChoreHistoryEndpoints(this RouteGroupBuilder api)
    {
        var chores = api.MapGroup("/households/{householdId:guid}/chores")
            .WithTags("Chores")
            .RequireAuthorization();

        // A app autentica-se com o token no cabeçalho (sem cookies): não há pedidos forjados por outro site.
        chores.MapPost("/photos", UploadPhoto).DisableAntiforgery();
        chores.MapGet("/photos/{photoId:guid}", GetPhoto);
        chores.MapGet("/history", GetHistory);
        chores.MapPost("/completions/{completionId:guid}/reject", Reject);
        return api;
    }

    private static async Task<IResult> UploadPhoto(
        Guid householdId,
        IFormFile photo,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(
            userId, householdId, HouseholdModule.Chores, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        if (photo.Length == 0 || photo.Length > ChorePhoto.MaxBytes)
        {
            return Invalid("A foto tem de ter no máximo 5 MB.");
        }

        using var buffer = new MemoryStream();
        await photo.CopyToAsync(buffer, ct);
        var data = buffer.ToArray();
        if (ChorePhoto.DetectContentType(data) is not { } contentType)
        {
            return Invalid("Isto não parece uma foto (JPEG, PNG ou WebP).");
        }

        var now = clock.GetUtcNow();
        await CleanUpAsync(db, householdId, now, ct);
        var stored = new ChorePhoto
        {
            HouseholdId = householdId, UploadedByUserId = userId, ContentType = contentType, Data = data, CreatedAt = now,
        };
        db.ChorePhotos.Add(stored);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/households/{householdId}/chores/photos/{stored.Id}", new ChorePhotoDto(stored.Id));
    }

    private static async Task<IResult> GetPhoto(
        Guid householdId, Guid photoId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(
            principal.GetUserId(), householdId, HouseholdModule.Chores, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var photo = await db.ChorePhotos.AsNoTracking()
            .Where(p => p.Id == photoId && p.HouseholdId == householdId)
            .Select(p => new { p.ContentType, p.Data })
            .SingleOrDefaultAsync(ct);
        return photo is null ? Results.NotFound() : Results.File(photo.Data, photo.ContentType);
    }

    /// <summary>As tarefas feitas nos últimos dias (por omissão 30), das mais recentes para as mais antigas.</summary>
    private static async Task<IResult> GetHistory(
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

        var since = clock.GetUtcNow().AddDays(-Math.Clamp(days ?? 30, 1, 365));
        var items = await db.ChoreCompletions.AsNoTracking()
            .Where(c => c.Chore.HouseholdId == householdId && c.CompletedAt >= since)
            .Select(c => new ChoreHistoryItemDto(
                c.Id, c.ChoreId, c.Chore.Title, c.UserId, c.Points, c.CompletedAt, c.PhotoId, c.RejectedAt != null))
            .ToListAsync(ct);

        // Ordena-se aqui: o SQLite não ordena por DateTimeOffset.
        return Results.Ok(items.OrderByDescending(i => i.CompletedAt).Take(200));
    }

    /// <summary>Não aceitar uma tarefa feita: os pontos deixam de contar e a tarefa volta para quem a fez, para hoje.</summary>
    private static async Task<IResult> Reject(
        Guid householdId,
        Guid completionId,
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

        var completion = await db.ChoreCompletions
            .Include(c => c.Chore).ThenInclude(c => c.Household)
            .SingleOrDefaultAsync(c => c.Id == completionId && c.Chore.HouseholdId == householdId, ct);
        if (completion is null)
        {
            return Results.NotFound();
        }

        if (completion.RejectedAt is not null)
        {
            return Results.Problem(title: "Esta tarefa já não foi aceite.", statusCode: StatusCodes.Status409Conflict);
        }

        var now = clock.GetUtcNow();
        var chore = completion.Chore;
        completion.RejectedAt = now;
        completion.RejectedByUserId = userId;
        chore.Reopen(completion.UserId, chore.Household.Today(now), now);
        await db.SaveChangesAsync(ct);

        await notifier.ChoreUpserted(householdId, ChoreEndpoints.ToDto(chore), ct);
        await notifications.NotifyAsync(
            householdId,
            [completion.UserId],
            userId,
            NotificationKind.ChoreRejected,
            $"Faz outra vez: {chore.Title}",
            completion.Points > 0 ? $"Não foi aceite, os {completion.Points} pontos não contam." : "Não foi aceite.",
            chore.Id,
            ct);
        return Results.Ok(new ChoreHistoryItemDto(
            completion.Id, chore.Id, chore.Title, completion.UserId, completion.Points, completion.CompletedAt,
            completion.PhotoId, true));
    }

    /// <summary>Apaga as fotos antigas e as que se enviaram mas nunca serviram para concluir uma tarefa.</summary>
    private static async Task CleanUpAsync(KubicoDbContext db, Guid householdId, DateTimeOffset now, CancellationToken ct)
    {
        var old = now - ChorePhoto.KeepFor;
        var unused = now - ChorePhoto.KeepUnusedFor;
        var photos = await db.ChorePhotos
            .Where(p => p.HouseholdId == householdId)
            .Select(p => new { p.Id, p.CreatedAt, Used = db.ChoreCompletions.Any(c => c.PhotoId == p.Id) })
            .ToListAsync(ct);
        var expired = photos.Where(p => p.CreatedAt < old || (!p.Used && p.CreatedAt < unused)).Select(p => p.Id).ToList();
        if (expired.Count == 0)
        {
            return;
        }

        await db.ChoreCompletions.Where(c => c.PhotoId != null && expired.Contains(c.PhotoId.Value))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.PhotoId, (Guid?)null), ct);
        await db.ChorePhotos.Where(p => expired.Contains(p.Id)).ExecuteDeleteAsync(ct);
    }

    private static IResult Invalid(string title) => Results.Problem(title: title, statusCode: StatusCodes.Status400BadRequest);
}
