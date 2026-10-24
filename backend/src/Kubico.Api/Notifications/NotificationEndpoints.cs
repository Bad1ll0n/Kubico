using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Notifications;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Notifications;

public record NotificationsDto(IReadOnlyList<NotificationDto> Items, int UnreadCount);

/// <summary>Marcar como lidos: os avisos indicados ou, sem <c>Ids</c>, todos.</summary>
public record MarkReadRequest(IReadOnlyList<Guid>? Ids = null);

public record NotificationPreferencesDto(
    NotificationDelivery Delivery,
    TimeOnly DigestAt,
    TimeOnly? QuietFrom,
    TimeOnly? QuietUntil,
    string TimeZoneId);

public record RegisterDeviceRequest(
    [property: Required, StringLength(512, MinimumLength = 1)] string Token,
    [property: Required, RegularExpression("^(android|ios|web)$")] string Platform);

public record UnregisterDeviceRequest([property: Required] string Token);

/// <summary>Os avisos são de cada pessoa (de todas as casas a que pertence), por isso não ficam debaixo de uma casa.</summary>
public static class NotificationEndpoints
{
    private const int Recent = 50;

    public static RouteGroupBuilder MapNotificationEndpoints(this RouteGroupBuilder api)
    {
        var notifications = api.MapGroup("/notifications")
            .WithTags("Notifications")
            .RequireAuthorization();

        notifications.MapGet("/", GetNotifications);
        notifications.MapPost("/read", MarkRead);
        notifications.MapGet("/preferences", GetPreferences);
        notifications.MapPut("/preferences", SavePreferences);
        notifications.MapPost("/devices", RegisterDevice);
        notifications.MapPost("/devices/unregister", UnregisterDevice);

        return api;
    }

    private static async Task<IResult> GetNotifications(ClaimsPrincipal principal, KubicoDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var items = await db.Notifications.AsNoTracking()
            .Where(n => n.UserId == userId)
            .OrderByDescending(n => n.CreatedAt)
            .Take(Recent)
            .ToListAsync(ct);
        var unread = await db.Notifications.CountAsync(n => n.UserId == userId && n.ReadAt == null, ct);
        return Results.Ok(new NotificationsDto([.. items.Select(NotificationService.ToDto)], unread));
    }

    private static async Task<IResult> MarkRead(
        MarkReadRequest request, ClaimsPrincipal principal, KubicoDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var query = db.Notifications.Where(n => n.UserId == userId && n.ReadAt == null);
        if (request.Ids is { Count: > 0 } ids)
        {
            query = query.Where(n => ids.Contains(n.Id));
        }

        var now = clock.GetUtcNow();
        foreach (var notification in await query.ToListAsync(ct))
        {
            notification.ReadAt = now;
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPreferences(ClaimsPrincipal principal, KubicoDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var preferences = await db.NotificationPreferences.AsNoTracking().SingleOrDefaultAsync(p => p.UserId == userId, ct)
            ?? new NotificationPreferences { UserId = userId };
        return Results.Ok(ToDto(preferences));
    }

    private static async Task<IResult> SavePreferences(
        NotificationPreferencesDto request, ClaimsPrincipal principal, KubicoDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();
        if ((request.QuietFrom is null) != (request.QuietUntil is null) || (request.QuietFrom is { } from && from == request.QuietUntil))
        {
            errors["QuietFrom"] = ["Indica quando começam e quando acabam as horas de silêncio (horas diferentes)."];
        }

        if (!TimeZoneInfo.TryFindSystemTimeZoneById(request.TimeZoneId, out _))
        {
            errors["TimeZoneId"] = ["Fuso horário desconhecido."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var userId = principal.GetUserId();
        var preferences = await db.NotificationPreferences.SingleOrDefaultAsync(p => p.UserId == userId, ct);
        if (preferences is null)
        {
            preferences = new NotificationPreferences { UserId = userId };
            db.NotificationPreferences.Add(preferences);
        }

        preferences.Delivery = request.Delivery;
        preferences.DigestAt = request.DigestAt;
        preferences.QuietFrom = request.QuietFrom;
        preferences.QuietUntil = request.QuietUntil;
        preferences.TimeZoneId = request.TimeZoneId;
        preferences.NextPushAt = null; // As horas mudaram: o envio volta a olhar para os avisos desta pessoa já.
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToDto(preferences));
    }

    /// <summary>
    /// Guarda o token push deste telemóvel, ligado à sessão em que se registou: terminar essa sessão (aqui, noutro
    /// aparelho ou em todos) deixa de lhe enviar avisos. Um token só pertence a uma pessoa (a última que entrou nele).
    /// </summary>
    private static async Task<IResult> RegisterDevice(
        RegisterDeviceRequest request, ClaimsPrincipal principal, KubicoDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var sessionId = HouseholdHub.SessionOf(principal);
        var now = clock.GetUtcNow();
        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Token == request.Token, ct);
        if (device is null)
        {
            db.PushDevices.Add(new PushDevice
            {
                UserId = userId,
                Token = request.Token,
                Platform = request.Platform,
                SessionId = sessionId,
                CreatedAt = now,
                LastSeenAt = now,
            });
        }
        else
        {
            device.UserId = userId;
            device.Platform = request.Platform;
            device.SessionId = sessionId;
            device.LastSeenAt = now;
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Ao terminar a sessão no telemóvel: deixa de receber avisos.</summary>
    private static async Task<IResult> UnregisterDevice(
        UnregisterDeviceRequest request, ClaimsPrincipal principal, KubicoDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var device = await db.PushDevices.SingleOrDefaultAsync(d => d.Token == request.Token && d.UserId == userId, ct);
        if (device is not null)
        {
            db.PushDevices.Remove(device);
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static NotificationPreferencesDto ToDto(NotificationPreferences p) =>
        new(p.Delivery, p.DigestAt, p.QuietFrom, p.QuietUntil, p.TimeZoneId);
}
