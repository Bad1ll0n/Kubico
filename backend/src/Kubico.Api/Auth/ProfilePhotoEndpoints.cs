using System.Security.Claims;
using Kubico.Api.Common;
using Kubico.Api.Households;
using Kubico.Api.Realtime;
using Kubico.Domain.Chores;
using Kubico.Domain.Users;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Auth;

/// <summary>
/// A fotografia de perfil (F4-03). Só para quem tem conta: os perfis das crianças ficam com o emoji (não se guardam
/// fotografias de crianças). Como as fotos de prova, confirma-se que é uma imagem e tiram-se os metadados (EXIF com a
/// localização, XMP, textos). Só a veem a própria pessoa e quem está numa casa com ela.
/// </summary>
public static class ProfilePhotoEndpoints
{
    public static RouteGroupBuilder MapProfilePhotoEndpoints(this RouteGroupBuilder api)
    {
        api.MapPut("/account/photo", Upload).RequireAuthorization().DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(ChorePhoto.MaxBytes + 64 * 1024));
        api.MapDelete("/account/photo", Remove).RequireAuthorization();
        api.MapGet("/users/{userId:guid}/photo", Get).RequireAuthorization();
        return api;
    }

    private static async Task<IResult> Upload(
        IFormFile photo, ClaimsPrincipal principal, KubicoDbContext db, HouseholdNotifier notifier, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (user.IsChildProfile)
        {
            return Results.Problem(title: "Os perfis das crianças usam um emoji em vez de fotografia.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (photo.Length == 0 || photo.Length > ChorePhoto.MaxBytes)
        {
            return Invalid("A foto tem de ter no máximo 5 MB.");
        }

        using var buffer = new MemoryStream();
        await photo.CopyToAsync(buffer, ct);
        var received = buffer.ToArray();
        if (ChorePhoto.DetectContentType(received) is not { } contentType
            || PhotoMetadata.Strip(received, contentType) is not { } data
            || PhotoMetadata.ReadSize(data, contentType) is not { Width: > 0, Height: > 0 } size)
        {
            return Invalid("Isto não parece uma foto (JPEG, PNG ou WebP).");
        }

        if (size.Width > PhotoMetadata.MaxSide || size.Height > PhotoMetadata.MaxSide)
        {
            return Invalid("A foto é grande demais (no máximo 4096 × 4096 píxeis).");
        }

        var stored = await db.UserPhotos.SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (stored is null)
        {
            db.UserPhotos.Add(new UserPhoto { UserId = userId, ContentType = contentType, Data = data });
        }
        else
        {
            stored.ContentType = contentType;
            stored.Data = data;
        }

        user.PhotoVersion = Guid.NewGuid();
        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, notifier, userId, ct);
        return Results.Ok(user.ToDto());
    }

    private static async Task<IResult> Remove(
        ClaimsPrincipal principal, KubicoDbContext db, HouseholdNotifier notifier, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        await db.UserPhotos.Where(p => p.UserId == userId).ExecuteDeleteAsync(ct);
        user.PhotoVersion = null;
        await db.SaveChangesAsync(ct);
        await NotifyAsync(db, notifier, userId, ct);
        return Results.NoContent();
    }

    /// <summary>A fotografia de alguém: a própria pessoa ou quem partilha uma casa com ela (os outros recebem 404).</summary>
    private static async Task<IResult> Get(
        Guid userId, ClaimsPrincipal principal, KubicoDbContext db, HttpContext http, CancellationToken ct)
    {
        var me = principal.GetUserId();
        var visible = me == userId || await db.Memberships.AnyAsync(
            m => m.UserId == me && db.Memberships.Any(o => o.HouseholdId == m.HouseholdId && o.UserId == userId), ct);
        var version = visible
            ? await db.Users.Where(u => u.Id == userId).Select(u => u.PhotoVersion).SingleOrDefaultAsync(ct)
            : null;
        if (version is not { } current)
        {
            return Results.NotFound();
        }

        var etag = HttpCaching.ETagOf(current);
        HttpCaching.PrivateWithETag(http, etag);
        if (HttpCaching.ClientHas(http, etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var photo = await db.UserPhotos.AsNoTracking().Where(p => p.UserId == userId)
            .Select(p => new { p.ContentType, p.Data }).SingleOrDefaultAsync(ct);
        return photo is null ? Results.NotFound() : Results.File(photo.Data, photo.ContentType);
    }

    private static async Task NotifyAsync(KubicoDbContext db, HouseholdNotifier notifier, Guid userId, CancellationToken ct)
    {
        foreach (var membership in await db.Memberships.Include(m => m.User).Where(m => m.UserId == userId).ToListAsync(ct))
        {
            await notifier.MemberUpserted(membership.HouseholdId, MemberEndpoints.ToDto(membership), ct);
        }
    }

    private static IResult Invalid(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["photo"] = [message] });
}
