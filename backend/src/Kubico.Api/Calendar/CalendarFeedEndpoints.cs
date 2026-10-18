using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Kubico.Api.Common;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Calendar;

/// <summary>Endereço pessoal para subscrever o calendário da casa. <c>WebcalUrl</c> abre logo a app de calendário.</summary>
public record CalendarFeedDto(string Url, string WebcalUrl);

/// <summary>
/// Calendário da casa em iCalendar, para ver os eventos no Google Calendar, no Outlook ou no iPhone. Cada membro tem
/// o seu endereço secreto (pode gerar outro a qualquer momento); quem o tiver vê os eventos, mas não os altera.
/// </summary>
public static class CalendarFeedEndpoints
{
    public static RouteGroupBuilder MapCalendarFeedEndpoints(this RouteGroupBuilder api)
    {
        var feed = api.MapGroup("/households/{householdId:guid}/calendar-feed")
            .WithTags("Calendar")
            .RequireAuthorization();

        feed.MapPost("/", GetOrCreateFeed);
        feed.MapDelete("/", RevokeFeed);

        // Os calendários externos não sabem iniciar sessão: o segredo vai no endereço.
        api.MapGet("/calendar-feeds/{token}.ics", GetFeed).WithTags("Calendar").AllowAnonymous();

        return api;
    }

    private static async Task<IResult> GetOrCreateFeed(
        Guid householdId,
        ClaimsPrincipal principal,
        HttpRequest http,
        IConfiguration configuration,
        KubicoDbContext db,
        HouseholdAccess access,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(
            userId, householdId, HouseholdModule.Calendar, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var membership = await db.Memberships.SingleAsync(m => m.UserId == userId && m.HouseholdId == householdId, ct);
        if (membership.CalendarFeedToken is null)
        {
            membership.CalendarFeedToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
            await db.SaveChangesAsync(ct);
        }

        var url = $"{PublicUrls.BaseUrl(http, configuration)}/api/calendar-feeds/{membership.CalendarFeedToken}.ics";
        return Results.Ok(new CalendarFeedDto(url, "webcal" + url[url.IndexOf("://", StringComparison.Ordinal)..]));
    }

    /// <summary>O endereço antigo deixa de funcionar; o próximo pedido gera um novo.</summary>
    private static async Task<IResult> RevokeFeed(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(
            userId, householdId, HouseholdModule.Calendar, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var membership = await db.Memberships.SingleAsync(m => m.UserId == userId && m.HouseholdId == householdId, ct);
        membership.CalendarFeedToken = null;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetFeed(string token, KubicoDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var membership = await db.Memberships.AsNoTracking()
            .Include(m => m.Household)
            .SingleOrDefaultAsync(m => m.CalendarFeedToken == token, ct);
        if (membership is null)
        {
            return Results.NotFound();
        }

        // Séries e exceções vão sempre; eventos únicos, só os do último ano em diante.
        var household = membership.Household;
        var now = clock.GetUtcNow();
        var since = now.AddYears(-1);
        var sinceDate = household.Today(now).AddYears(-1);
        var events = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.HouseholdId == household.Id)
            .Where(e => e.Recurrence != null || e.SeriesId != null || e.EndsAt >= since || e.EndDate >= sinceDate)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(ct);

        var names = await db.Memberships.AsNoTracking()
            .Where(m => m.HouseholdId == household.Id)
            .Select(m => new { m.UserId, m.User.DisplayName })
            .ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);

        var ics = new IcsWriter(household, names).Write(events, now);
        return Results.Text(ics, "text/calendar", Encoding.UTF8);
    }
}
