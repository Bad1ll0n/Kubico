using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using Kubico.Api.Common;
using Kubico.Api.Notifications;
using Kubico.Api.Realtime;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Calendar;

/// <summary>
/// Criar ou substituir um evento (PUT envia o estado completo). Com hora: <c>StartsAt</c>/<c>EndsAt</c>;
/// de dia inteiro: <c>StartDate</c>/<c>EndDate</c> (inclusive; sem <c>EndDate</c>, é um só dia).
/// </summary>
public record SaveEventRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Title,
    [property: StringLength(1000)] string? Notes = null,
    [property: StringLength(200)] string? Location = null,
    bool IsAllDay = false,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null,
    DateOnly? StartDate = null,
    DateOnly? EndDate = null,
    [property: StringLength(200)] string? Recurrence = null,
    IReadOnlyList<Guid>? ParticipantUserIds = null,
    Guid? DropOffUserId = null,
    Guid? PickUpUserId = null);

public record CalendarEventDto(
    Guid Id,
    Guid HouseholdId,
    string Title,
    string? Notes,
    string? Location,
    bool IsAllDay,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string? Recurrence,
    IReadOnlyList<Guid> ParticipantUserIds,
    Guid? DropOffUserId,
    Guid? PickUpUserId,
    Guid? SeriesId,
    DateOnly? OriginalDate,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Uma ocorrência no intervalo pedido. <c>OccurrenceDate</c> identifica-a dentro da série (para alterar ou apagar
/// só esta). <c>ConflictUserIds</c> são os membros que estão noutro evento ao mesmo tempo (ou ocupados quando
/// têm de levar ou ir buscar).
/// </summary>
public record CalendarOccurrenceDto(
    Guid EventId,
    DateOnly OccurrenceDate,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    DateOnly? StartDate,
    DateOnly? EndDate,
    IReadOnlyList<Guid> ConflictUserIds);

public record HolidayDto(DateOnly Date, string Name);

public record CalendarDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<CalendarEventDto> Events,
    IReadOnlyList<CalendarOccurrenceDto> Occurrences,
    IReadOnlyList<HolidayDto> Holidays);

public static class CalendarEndpoints
{
    /// <summary>Chega para a vista de mês com as semanas à volta (42 dias) e para uma agenda trimestral.</summary>
    public const int MaxRangeDays = 92;

    public static RouteGroupBuilder MapCalendarEndpoints(this RouteGroupBuilder api)
    {
        var events = api.MapGroup("/households/{householdId:guid}/events")
            .WithTags("Calendar")
            .RequireAuthorization();

        events.MapGet("/", GetCalendar);
        events.MapPost("/", CreateEvent);
        events.MapPut("/{eventId:guid}", UpdateEvent);
        events.MapDelete("/{eventId:guid}", DeleteEvent);
        events.MapPut("/{eventId:guid}/occurrences/{date}", UpdateOccurrence);
        events.MapDelete("/{eventId:guid}/occurrences/{date}", DeleteOccurrence);

        return api;
    }

    /// <summary>Ocorrências, eventos e feriados entre dois dias (inclusive, no fuso da casa).</summary>
    private static async Task<IResult> GetCalendar(
        Guid householdId,
        DateOnly? from,
        DateOnly? to,
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

        if (from is not { } first || to is not { } last || last < first
            || last.DayNumber - first.DayNumber >= MaxRangeDays || !IsSupported(first) || !IsSupported(last))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["to"] = [$"Indica um intervalo de datas válido, com {MaxRangeDays} dias no máximo."],
            });
        }

        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        var zone = household.TimeZone;

        // Uma margem de um dia de cada lado apanha conflitos com eventos mesmo à beira do intervalo.
        var (windowFirst, windowLast) = (first.AddDays(-1), last.AddDays(1));
        var windowStart = ZonedTime.StartOfDay(windowFirst, zone);
        var windowEnd = ZonedTime.StartOfDay(windowLast.AddDays(1), zone);

        var events = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.HouseholdId == householdId)
            .Where(e => (e.Recurrence != null && (e.StartsAt < windowEnd || e.StartDate <= windowLast))
                || (e.StartsAt < windowEnd && e.EndsAt > windowStart)
                || (e.StartDate <= windowLast && e.EndDate >= windowFirst))
            .ToListAsync(ct);

        var occurrences = events
            .SelectMany(e => e.Occurrences(windowStart, windowEnd, zone))
            .OrderBy(o => o.Start)
            .ThenByDescending(o => o.IsAllDay)
            .ThenBy(o => o.Event.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var conflicts = CalendarConflicts.Find(occurrences);

        var rangeStart = ZonedTime.StartOfDay(first, zone);
        var rangeEnd = ZonedTime.StartOfDay(last.AddDays(1), zone);
        var visible = occurrences.Index()
            .Where(x => x.Item.Start < rangeEnd && x.Item.End > rangeStart)
            .ToList();

        return Results.Ok(new CalendarDto(
            first,
            last,
            [.. visible.Select(x => x.Item.Event).DistinctBy(e => e.Id).Select(ToDto)],
            [.. visible.Select(x => ToDto(x.Item, conflicts[x.Index]))],
            [.. PortugueseHolidays.Between(first, last).Select(h => new HolidayDto(h.Date, h.Name))]));
    }

    private static async Task<IResult> CreateEvent(
        Guid householdId,
        SaveEventRequest request,
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

        if (await Validate(db, householdId, request, ct) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        var calendarEvent = new CalendarEvent
        {
            HouseholdId = householdId,
            Title = request.Title,
            CreatedByUserId = userId,
            CreatedAt = now,
        };
        Apply(calendarEvent, request, now);

        db.CalendarEvents.Add(calendarEvent);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(calendarEvent);
        await notifier.CalendarEventUpserted(householdId, dto, ct);
        await NotifyInvolved(notifications, db, calendarEvent, [], userId, ct);
        return Results.Created($"/api/households/{householdId}/events/{calendarEvent.Id}", dto);
    }

    private static async Task<IResult> UpdateEvent(
        Guid householdId,
        Guid eventId,
        SaveEventRequest request,
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

        var calendarEvent = await FindEvent(db, householdId, eventId, ct);
        if (calendarEvent is null)
        {
            return Results.NotFound();
        }

        if (await Validate(db, householdId, request, ct) is { } invalid)
        {
            return invalid;
        }

        if (calendarEvent.SeriesId is not null && !string.IsNullOrWhiteSpace(request.Recurrence))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Recurrence"] = ["Uma alteração a um só dia de uma série não se pode repetir."],
            });
        }

        var before = Involved(calendarEvent);
        var removed = await UpdateSeries(db, calendarEvent, request, clock.GetUtcNow(), ct);
        await db.SaveChangesAsync(ct);

        var dto = ToDto(calendarEvent);
        await notifier.CalendarEventUpserted(householdId, dto, ct);
        await NotifyDeleted(notifier, householdId, removed, ct);
        await NotifyInvolved(notifications, db, calendarEvent, before, principal.GetUserId(), ct);
        return Results.Ok(dto);
    }

    private static async Task<IResult> DeleteEvent(
        Guid householdId,
        Guid eventId,
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

        var calendarEvent = await FindEvent(db, householdId, eventId, ct);
        if (calendarEvent is null)
        {
            return Results.NotFound();
        }

        var removed = await RemoveExceptions(db, calendarEvent.Id, _ => true, ct);
        db.CalendarEvents.Remove(calendarEvent);
        await db.SaveChangesAsync(ct);
        await NotifyDeleted(notifier, householdId, [.. removed, eventId], ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Altera uma ocorrência de uma série: só essa (<c>scope=this</c>, que passa a ser uma exceção sem repetição)
    /// ou essa e as seguintes (<c>scope=following</c>: a série termina antes e começa outra com os dados enviados).
    /// </summary>
    private static async Task<IResult> UpdateOccurrence(
        Guid householdId,
        Guid eventId,
        DateOnly date,
        string? scope,
        SaveEventRequest request,
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

        if (ParseScope(scope) is not { } following)
        {
            return InvalidScope();
        }

        var (series, zone) = await FindSeries(db, householdId, eventId, date, ct);
        if (series is null)
        {
            return Results.NotFound();
        }

        if (await Validate(db, householdId, request, ct) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        var before = Involved(series);
        if (following && !series.EndBefore(date, zone))
        {
            // É a primeira ocorrência: "esta e as seguintes" é a série toda.
            var removedAll = await UpdateSeries(db, series, request, now, ct);
            await db.SaveChangesAsync(ct);
            var seriesDto = ToDto(series);
            await notifier.CalendarEventUpserted(householdId, seriesDto, ct);
            await NotifyDeleted(notifier, householdId, removedAll, ct);
            await NotifyInvolved(notifications, db, series, before, userId, ct);
            return Results.Ok(seriesDto);
        }

        series.UpdatedAt = now;
        List<Guid> removed = [];
        CalendarEvent changed;
        var isNew = true;
        if (following)
        {
            removed = await RemoveExceptions(db, series.Id, d => d >= date, ct);
            changed = new CalendarEvent
            {
                HouseholdId = householdId,
                Title = request.Title,
                CreatedByUserId = userId,
                CreatedAt = now,
            };
            Apply(changed, request, now);
        }
        else
        {
            // Só esta: sai da série e passa a ser um evento próprio, que se lembra do dia que substitui.
            var existing = await db.CalendarEvents
                .SingleOrDefaultAsync(e => e.SeriesId == series.Id && e.OriginalDate == date, ct);
            isNew = existing is null;
            changed = existing ?? new CalendarEvent
            {
                HouseholdId = householdId,
                Title = request.Title,
                SeriesId = series.Id,
                OriginalDate = date,
                CreatedByUserId = userId,
                CreatedAt = now,
            };
            Apply(changed, request with { Recurrence = null }, now);
            series.Exclude(date);
        }

        if (isNew)
        {
            db.CalendarEvents.Add(changed);
        }

        await db.SaveChangesAsync(ct);

        var dto = ToDto(changed);
        await notifier.CalendarEventUpserted(householdId, ToDto(series), ct);
        await notifier.CalendarEventUpserted(householdId, dto, ct);
        await NotifyDeleted(notifier, householdId, removed, ct);
        await NotifyInvolved(notifications, db, changed, before, userId, ct);
        return isNew ? Results.Created($"/api/households/{householdId}/events/{changed.Id}", dto) : Results.Ok(dto);
    }

    /// <summary>Apaga uma ocorrência de uma série: só essa (<c>scope=this</c>) ou essa e as seguintes.</summary>
    private static async Task<IResult> DeleteOccurrence(
        Guid householdId,
        Guid eventId,
        DateOnly date,
        string? scope,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(
            principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        if (ParseScope(scope) is not { } following)
        {
            return InvalidScope();
        }

        var (series, zone) = await FindSeries(db, householdId, eventId, date, ct);
        if (series is null)
        {
            return Results.NotFound();
        }

        if (following && !series.EndBefore(date, zone))
        {
            // Desde a primeira ocorrência: não sobra nada da série.
            var removedAll = await RemoveExceptions(db, series.Id, _ => true, ct);
            db.CalendarEvents.Remove(series);
            await db.SaveChangesAsync(ct);
            await NotifyDeleted(notifier, householdId, [.. removedAll, series.Id], ct);
            return Results.NoContent();
        }

        if (!following)
        {
            series.Exclude(date);
        }

        var removed = await RemoveExceptions(db, series.Id, d => following ? d >= date : d == date, ct);
        series.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await notifier.CalendarEventUpserted(householdId, ToDto(series), ct);
        await NotifyDeleted(notifier, householdId, removed, ct);
        return Results.NoContent();
    }

    private static Task<CalendarEvent?> FindEvent(
        KubicoDbContext db, Guid householdId, Guid eventId, CancellationToken ct) =>
        db.CalendarEvents.SingleOrDefaultAsync(e => e.Id == eventId && e.HouseholdId == householdId, ct);

    /// <summary>A série com uma ocorrência nesse dia e o fuso da casa; a série vem null se não existir.</summary>
    private static async Task<(CalendarEvent? Series, TimeZoneInfo Zone)> FindSeries(
        KubicoDbContext db, Guid householdId, Guid eventId, DateOnly date, CancellationToken ct)
    {
        var zone = (await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct)).TimeZone;
        var series = await FindEvent(db, householdId, eventId, ct);
        return series is { IsRecurring: true } && series.HasOccurrenceOn(date, zone) ? (series, zone) : (null, zone);
    }

    /// <summary>
    /// Aplica um PUT a um evento inteiro. Mudar o início ou a repetição de uma série desfaz as exceções, porque os
    /// dias deixam de corresponder às ocorrências; devolve os ids das exceções apagadas.
    /// </summary>
    private static async Task<List<Guid>> UpdateSeries(
        KubicoDbContext db, CalendarEvent calendarEvent, SaveEventRequest request, DateTimeOffset now, CancellationToken ct)
    {
        var schedule = (calendarEvent.StartsAt, calendarEvent.StartDate, calendarEvent.Recurrence);
        Apply(calendarEvent, request, now);
        if (schedule == (calendarEvent.StartsAt, calendarEvent.StartDate, calendarEvent.Recurrence))
        {
            return [];
        }

        calendarEvent.ExcludedDates = [];
        return await RemoveExceptions(db, calendarEvent.Id, _ => true, ct);
    }

    /// <summary>Apaga as exceções de uma série cujo dia original satisfaz <paramref name="which"/>.</summary>
    private static async Task<List<Guid>> RemoveExceptions(
        KubicoDbContext db, Guid seriesId, Func<DateOnly, bool> which, CancellationToken ct)
    {
        var exceptions = (await db.CalendarEvents.Where(e => e.SeriesId == seriesId).ToListAsync(ct))
            .Where(e => which(e.OriginalDate!.Value))
            .ToList();
        db.CalendarEvents.RemoveRange(exceptions);
        return [.. exceptions.Select(e => e.Id)];
    }

    /// <summary>Quem está no evento: participantes e quem leva ou vai buscar.</summary>
    private static HashSet<Guid> Involved(CalendarEvent e) =>
        [.. e.ParticipantUserIds, .. new[] { e.DropOffUserId, e.PickUpUserId }.OfType<Guid>()];

    /// <summary>Avisa quem passou a estar no evento (e não estava antes), com o dia e a hora.</summary>
    private static async Task NotifyInvolved(
        NotificationService notifications,
        KubicoDbContext db,
        CalendarEvent e,
        IEnumerable<Guid> before,
        Guid actorId,
        CancellationToken ct)
    {
        var added = Involved(e).Except(before).ToList();
        if (added.Count == 0)
        {
            return;
        }

        var zone = (await db.Households.AsNoTracking().SingleAsync(h => h.Id == e.HouseholdId, ct)).TimeZone;
        var when = e.IsAllDay
            ? e.StartDate!.Value.ToString("dd/MM", CultureInfo.InvariantCulture) + " (todo o dia)"
            : TimeZoneInfo.ConvertTime(e.StartsAt!.Value, zone).ToString("dd/MM 'às' HH:mm", CultureInfo.InvariantCulture);
        foreach (var userId in added)
        {
            var (kind, title) = (userId == e.DropOffUserId, userId == e.PickUpUserId) switch
            {
                (true, true) => (NotificationKind.RideAssigned, $"Tens de levar e ir buscar: {e.Title}"),
                (true, false) => (NotificationKind.RideAssigned, $"Tens de levar: {e.Title}"),
                (false, true) => (NotificationKind.RideAssigned, $"Tens de ir buscar: {e.Title}"),
                _ => (NotificationKind.EventAssigned, $"Novo evento: {e.Title}"),
            };
            await notifications.NotifyAsync(e.HouseholdId, [userId], actorId, kind, title, when, e.Id, ct);
        }
    }

    private static async Task NotifyDeleted(
        HouseholdNotifier notifier, Guid householdId, IEnumerable<Guid> eventIds, CancellationToken ct)
    {
        foreach (var eventId in eventIds)
        {
            await notifier.CalendarEventDeleted(householdId, eventId, ct);
        }
    }

    /// <summary>true = "esta e as seguintes"; false = "só esta"; null = valor inválido.</summary>
    private static bool? ParseScope(string? scope) => scope?.ToLowerInvariant() switch
    {
        null or "this" => false,
        "following" => true,
        _ => null,
    };

    private static IResult InvalidScope() => Results.ValidationProblem(new Dictionary<string, string[]>
    {
        ["scope"] = ["Indica se é só este evento (this) ou este e os seguintes (following)."],
    });

    /// <summary>Datas coerentes, repetição suportada, e participantes e boleias que são membros desta casa.</summary>
    private static async Task<IResult?> Validate(
        KubicoDbContext db, Guid householdId, SaveEventRequest request, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.IsAllDay)
        {
            if (request.StartDate is not { } startDate)
            {
                errors["StartDate"] = ["Indica o dia do evento."];
            }
            else if (request.EndDate is { } endDate && endDate < startDate)
            {
                errors["EndDate"] = ["O último dia não pode ser antes do primeiro."];
            }
            else if (!IsSupported(startDate) || !IsSupported(request.EndDate ?? startDate))
            {
                errors["StartDate"] = [OutOfRangeMessage];
            }
        }
        else if (request.StartsAt is not { } startsAt || request.EndsAt is not { } endsAt)
        {
            errors["StartsAt"] = ["Indica quando o evento começa e acaba."];
        }
        else if (endsAt <= startsAt)
        {
            errors["EndsAt"] = ["O fim tem de ser depois do início."];
        }
        else if (!IsSupported(DateOnly.FromDateTime(startsAt.UtcDateTime))
            || !IsSupported(DateOnly.FromDateTime(endsAt.UtcDateTime)))
        {
            errors["StartsAt"] = [OutOfRangeMessage];
        }

        if (!string.IsNullOrWhiteSpace(request.Recurrence) && !RecurrenceRule.TryParse(request.Recurrence, out _))
        {
            errors["Recurrence"] = ["Esta repetição não é válida ou ainda não é suportada."];
        }

        var referenced = (request.ParticipantUserIds ?? [])
            .Select(id => (Guid?)id)
            .Append(request.DropOffUserId)
            .Append(request.PickUpUserId)
            .OfType<Guid>()
            .Distinct()
            .ToList();
        if (referenced.Count > 0)
        {
            var members = await db.Memberships
                .CountAsync(m => m.HouseholdId == householdId && referenced.Contains(m.UserId), ct);
            if (members != referenced.Count)
            {
                errors["ParticipantUserIds"] = ["Os participantes e as boleias têm de ser membros desta casa."];
            }
        }

        return errors.Count == 0 ? null : Results.ValidationProblem(errors);
    }

    private const string OutOfRangeMessage = "As datas têm de estar entre 1900 e 2200.";

    private static bool IsSupported(DateOnly date) => date.Year is >= 1900 and <= 2200;

    private static void Apply(CalendarEvent calendarEvent, SaveEventRequest request, DateTimeOffset now)
    {
        calendarEvent.Title = request.Title.Trim();
        calendarEvent.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        calendarEvent.Location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim();

        if (request.IsAllDay)
        {
            calendarEvent.SetAllDay(request.StartDate!.Value, request.EndDate ?? request.StartDate.Value);
        }
        else
        {
            calendarEvent.SetTimed(request.StartsAt!.Value, request.EndsAt!.Value);
        }

        // Guarda-se a forma canónica, para todos os clientes lerem o mesmo texto.
        calendarEvent.Recurrence = RecurrenceRule.TryParse(request.Recurrence, out var rule) ? rule.ToString() : null;
        calendarEvent.ParticipantUserIds = [.. (request.ParticipantUserIds ?? []).Distinct()];
        calendarEvent.DropOffUserId = request.DropOffUserId;
        calendarEvent.PickUpUserId = request.PickUpUserId;
        calendarEvent.UpdatedAt = now;
    }

    internal static CalendarEventDto ToDto(CalendarEvent e) => new(
        e.Id, e.HouseholdId, e.Title, e.Notes, e.Location, e.IsAllDay, e.StartsAt, e.EndsAt, e.StartDate, e.EndDate,
        e.Recurrence, e.ParticipantUserIds, e.DropOffUserId, e.PickUpUserId, e.SeriesId, e.OriginalDate, e.UpdatedAt);

    private static CalendarOccurrenceDto ToDto(EventOccurrence o, IReadOnlySet<Guid> conflicts) => new(
        o.Event.Id,
        o.Date,
        o.IsAllDay ? null : o.Start,
        o.IsAllDay ? null : o.End,
        o.StartDate,
        o.EndDate,
        [.. conflicts.Order()]);
}
