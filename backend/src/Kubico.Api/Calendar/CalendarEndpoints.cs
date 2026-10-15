using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;
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
    DateTimeOffset UpdatedAt);

/// <summary>
/// Uma ocorrência no intervalo pedido. <c>ConflictUserIds</c> são os membros que estão noutro evento
/// ao mesmo tempo (ou ocupados quando têm de levar ou ir buscar).
/// </summary>
public record CalendarOccurrenceDto(
    Guid EventId,
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
        TimeProvider clock,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(
            principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var calendarEvent = await db.CalendarEvents
            .SingleOrDefaultAsync(e => e.Id == eventId && e.HouseholdId == householdId, ct);
        if (calendarEvent is null)
        {
            return Results.NotFound();
        }

        if (await Validate(db, householdId, request, ct) is { } invalid)
        {
            return invalid;
        }

        Apply(calendarEvent, request, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        var dto = ToDto(calendarEvent);
        await notifier.CalendarEventUpserted(householdId, dto, ct);
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

        var calendarEvent = await db.CalendarEvents
            .SingleOrDefaultAsync(e => e.Id == eventId && e.HouseholdId == householdId, ct);
        if (calendarEvent is null)
        {
            return Results.NotFound();
        }

        db.CalendarEvents.Remove(calendarEvent);
        await db.SaveChangesAsync(ct);
        await notifier.CalendarEventDeleted(householdId, eventId, ct);
        return Results.NoContent();
    }

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

    private static CalendarEventDto ToDto(CalendarEvent e) => new(
        e.Id, e.HouseholdId, e.Title, e.Notes, e.Location, e.IsAllDay, e.StartsAt, e.EndsAt, e.StartDate, e.EndDate,
        e.Recurrence, e.ParticipantUserIds, e.DropOffUserId, e.PickUpUserId, e.UpdatedAt);

    private static CalendarOccurrenceDto ToDto(EventOccurrence o, IReadOnlySet<Guid> conflicts) => new(
        o.Event.Id,
        o.IsAllDay ? null : o.Start,
        o.IsAllDay ? null : o.End,
        o.StartDate,
        o.EndDate,
        [.. conflicts.Order()]);
}
