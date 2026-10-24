using System.Security.Claims;
using Kubico.Api.Activity;
using Kubico.Api.Common;
using Kubico.Api.Notifications;
using Kubico.Api.Realtime;
using Kubico.Api.Rewards;
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
        // Confirma o tipo pelos bytes e tira os metadados (EXIF com a localização GPS, XMP, textos) antes de guardar.
        var received = buffer.ToArray();
        if (ChorePhoto.DetectContentType(received) is not { } contentType
            || PhotoMetadata.Strip(received, contentType) is not { } data)
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
            .OrderByDescending(c => c.CompletedAt)
            .Take(200)
            .Select(c => new ChoreHistoryItemDto(
                c.Id, c.ChoreId, c.Chore.Title, c.UserId, c.Points, c.CompletedAt, c.PhotoId, c.RejectedAt != null))
            .ToListAsync(ct);
        return Results.Ok(items);
    }

    /// <summary>
    /// Não aceitar uma tarefa feita: os pontos deixam de contar e a tarefa volta para quem a fez, para hoje. Se os
    /// pontos já foram trocados por recompensas, responde 409 (código "points-spent"); com <c>force=true</c>, quem
    /// gere as tarefas confirma e a pessoa fica a dever esses pontos.
    /// </summary>
    private static async Task<IResult> Reject(
        Guid householdId,
        Guid completionId,
        bool? force,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
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
        var doer = await db.Users.Where(u => u.Id == completion.UserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct) ?? "antigo membro";

        // Tranca o saldo de quem a fez antes de o ler (um pedido de recompensa em simultâneo não passa).
        if (completion.Points > 0 && await Points.LockAsync(db, householdId, completion.UserId, ct) is not null && force != true)
        {
            var left = await Points.AvailableAsync(db, householdId, completion.UserId, ct) - completion.Points;
            if (left < 0)
            {
                return Points.SpentProblem(doer, -left);
            }
        }

        completion.RejectedAt = now;
        completion.RejectedByUserId = userId;
        chore.Reopen(completion.UserId, chore.Household.Today(now), now);
        activity.Capture(householdId, userId, HouseholdModule.Chores, $"não aceitou «{chore.Title}», feita por {doer}");
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
