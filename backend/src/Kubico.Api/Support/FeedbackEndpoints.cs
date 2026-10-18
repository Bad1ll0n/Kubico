using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Auth;
using Kubico.Api.Common;
using Kubico.Domain.Support;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Support;

public record SendFeedbackRequest(
    FeedbackKind Kind,
    [property: Required, StringLength(2000, MinimumLength = 5)] string Message,
    Guid? HouseholdId = null);

public record FeedbackDto(
    Guid Id, FeedbackKind Kind, string Message, string? Device, string? UserName, string? UserEmail, DateTimeOffset CreatedAt);

/// <summary>
/// Quem mantém o Kubico: os emails em <c>Support:AdminEmails</c>. Só essas contas leem as mensagens recebidas.
/// </summary>
public class SupportOptions
{
    public const string SectionName = "Support";

    public string[] AdminEmails { get; set; } = [];

    public bool IsAdmin(string? email) =>
        email is not null && AdminEmails.Any(a => string.Equals(a.Trim(), email, StringComparison.OrdinalIgnoreCase));
}

/// <summary>"Reportar um problema" e "Sugerir uma ideia", a partir da ajuda da app.</summary>
public static class FeedbackEndpoints
{
    /// <summary>Mensagens por pessoa e por dia (para um erro na app não encher a caixa).</summary>
    private const int DailyLimit = 20;

    public static RouteGroupBuilder MapFeedbackEndpoints(this RouteGroupBuilder api)
    {
        var feedback = api.MapGroup("/feedback").WithTags("Support").RequireAuthorization();
        feedback.MapPost("/", Send);
        feedback.MapGet("/", List);
        return api;
    }

    private static async Task<IResult> Send(
        SendFeedbackRequest request,
        ClaimsPrincipal principal,
        HttpRequest http,
        KubicoDbContext db,
        HouseholdAccess access,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var now = clock.GetUtcNow();
        var since = now.AddDays(-1);
        if (await db.Feedback.CountAsync(f => f.UserId == userId && f.CreatedAt >= since, ct) >= DailyLimit)
        {
            return Results.Problem(
                title: "Já enviaste muitas mensagens hoje. Obrigado! Tenta outra vez amanhã.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        // A casa só fica registada se a pessoa for mesmo membro dela.
        var householdId = request.HouseholdId is { } id && await access.GetRoleAsync(userId, id, ct) is not null ? id : (Guid?)null;
        db.Feedback.Add(new Feedback
        {
            UserId = userId,
            HouseholdId = householdId,
            Kind = request.Kind,
            Message = request.Message.Trim(),
            Device = AuthEndpoints.Device(http),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>As mensagens mais recentes, para quem mantém o Kubico.</summary>
    private static async Task<IResult> List(
        ClaimsPrincipal principal,
        KubicoDbContext db,
        IOptions<SupportOptions> support,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var email = await db.Users.Where(u => u.Id == userId).Select(u => u.Email).SingleOrDefaultAsync(ct);
        if (!support.Value.IsAdmin(email))
        {
            return Results.Problem(
                title: "Só quem mantém o Kubico vê as mensagens recebidas.", statusCode: StatusCodes.Status403Forbidden);
        }

        var items = await db.Feedback.AsNoTracking()
            .OrderByDescending(f => f.CreatedAt)
            .Take(200)
            .Select(f => new
            {
                f.Id,
                f.Kind,
                f.Message,
                f.Device,
                f.CreatedAt,
                User = db.Users.Where(u => u.Id == f.UserId).Select(u => new { u.DisplayName, u.Email }).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return Results.Ok(items.Select(f =>
            new FeedbackDto(f.Id, f.Kind, f.Message, f.Device, f.User?.DisplayName, f.User?.Email, f.CreatedAt)));
    }
}
