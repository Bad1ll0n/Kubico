using Kubico.Api.Common;
using Kubico.Domain.Calendar;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Kubico.Domain.Rewards;
using Kubico.Domain.Shopping;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Notifications;

/// <summary>
/// O que a app diz sem ninguém pedir (F4-11, F4-21, F4-26): lembretes dos eventos (minutos antes) e das tarefas (à
/// hora escolhida), o resumo da manhã, o resumo da semana para quem gere as tarefas e o aviso de mesada por pagar.
/// Corre de minuto a minuto (<see cref="ReminderWorker"/>) e trata o que venceu nos últimos <see cref="Window"/>
/// (apanha um arranque atrasado). Cada envio reserva-se antes numa linha com índice único (<see cref="SentReminder"/>):
/// nunca sai duas vezes. Os avisos seguem as preferências de cada um (horas de silêncio, resumo diário) no envio.
/// </summary>
public class ReminderService(
    KubicoDbContext db,
    NotificationService notifications,
    HouseholdAccess access,
    TimeProvider clock,
    ILogger<ReminderService> logger)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Nos eventos de dia inteiro, os lembretes contam a partir desta hora do primeiro dia.</summary>
    public static readonly TimeOnly AllDayBase = new(9, 0);

    public static readonly TimeOnly WeeklySummaryAt = new(19, 0);
    public static readonly TimeOnly AllowanceReminderAt = new(18, 0);

    /// <summary>Os avisos da casa (garantias, manutenções, carro, IRS, documentos, aniversários) saem às 9h.</summary>
    public static readonly TimeOnly HomeRemindersAt = new(9, 0);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sent = 0;
        sent += await EventRemindersAsync(now, ct);
        sent += await ChoreRemindersAsync(now, ct);
        sent += await MorningSummariesAsync(now, ct);
        sent += await WeeklySummariesAsync(now, ct);
        sent += await AllowanceRemindersAsync(now, ct);
        sent += await HomeRemindersAsync(now, ct);
        return sent;
    }

    private static bool IsDue(DateTimeOffset dueAt, DateTimeOffset now) => dueAt <= now && dueAt > now - Window;

    private async Task<int> EventRemindersAsync(DateTimeOffset now, CancellationToken ct)
    {
        var horizon = now + Window + TimeSpan.FromMinutes(Calendar.CalendarEndpoints.MaxReminderMinutes) + TimeSpan.FromDays(1);
        var events = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.ReminderMinutes.Count > 0)
            .Where(e => (e.Recurrence != null && (e.RecurrenceEndDate == null || e.RecurrenceEndDate >= DateOnly.FromDateTime(now.UtcDateTime).AddDays(-2)))
                || e.EndsAt > now - Window
                || e.EndDate >= DateOnly.FromDateTime(now.UtcDateTime).AddDays(-2))
            .ToListAsync(ct);
        if (events.Count == 0)
        {
            return 0;
        }

        var zones = await ZonesAsync(events.Select(e => e.HouseholdId), ct);
        var sent = 0;
        foreach (var calendarEvent in events)
        {
            var zone = zones[calendarEvent.HouseholdId];
            foreach (var occurrence in calendarEvent.Occurrences(now - Window, horizon, zone))
            {
                var start = occurrence.IsAllDay
                    ? ZonedTime.ToInstant(occurrence.StartDate!.Value.ToDateTime(AllDayBase), zone)
                    : occurrence.Start;
                foreach (var minutes in calendarEvent.ReminderMinutes)
                {
                    var dueAt = start.AddMinutes(-minutes);
                    if (!IsDue(dueAt, now))
                    {
                        continue;
                    }

                    var recipients = await EventRecipientsAsync(calendarEvent, ct);
                    var local = TimeZoneInfo.ConvertTime(occurrence.Start, zone);
                    var title = occurrence.IsAllDay
                        ? $"Lembrete: {calendarEvent.Title} ({local:dd/MM})"
                        : $"Lembrete: {calendarEvent.Title} às {local:HH:mm}";
                    sent += await SendAsync(
                        calendarEvent.HouseholdId, recipients, NotificationKind.EventReminder, calendarEvent.Id, dueAt,
                        title, calendarEvent.Location, now, ct);
                }
            }
        }

        return sent;
    }

    /// <summary>Quem participa, leva ou vai buscar (ou toda a gente, se o evento não tiver ninguém), e ainda vê o calendário.</summary>
    private async Task<List<Guid>> EventRecipientsAsync(CalendarEvent calendarEvent, CancellationToken ct)
    {
        var canSee = await access.MembersWithAsync(calendarEvent.HouseholdId, HouseholdModule.Calendar, ModuleAccess.View, ct);
        var people = calendarEvent.ParticipantUserIds
            .Append(calendarEvent.DropOffUserId ?? Guid.Empty)
            .Append(calendarEvent.PickUpUserId ?? Guid.Empty)
            .Where(id => id != Guid.Empty)
            .ToHashSet();
        return people.Count == 0 ? canSee : [.. canSee.Where(people.Contains)];
    }

    private async Task<int> ChoreRemindersAsync(DateTimeOffset now, CancellationToken ct)
    {
        var chores = await db.Chores.AsNoTracking()
            .Where(c => c.RemindAt != null && c.ArchivedAt == null && c.CompletedAt == null)
            .ToListAsync(ct);
        if (chores.Count == 0)
        {
            return 0;
        }

        var households = await HouseholdsAsync(chores.Select(c => c.HouseholdId), ct);
        var sent = 0;
        foreach (var chore in chores)
        {
            var household = households[chore.HouseholdId];
            var today = household.Today(now);
            // Só no dia em que é para fazer (ou depois, se ficou em atraso), e se já se pode fazer.
            if (chore.DueDate is { } due && due > today || !chore.IsAvailableOn(today))
            {
                continue;
            }

            var dueAt = ZonedTime.ToInstant(today.ToDateTime(chore.RemindAt!.Value), household.TimeZone);
            if (!IsDue(dueAt, now))
            {
                continue;
            }

            var doers = await access.MembersWithAsync(chore.HouseholdId, HouseholdModule.Chores, ModuleAccess.Participate, ct);
            List<Guid> recipients = chore.AssigneeUserId is { } assignee
                ? doers.Contains(assignee) ? [assignee] : []
                : doers;
            sent += await SendAsync(
                chore.HouseholdId, recipients, NotificationKind.ChoreReminder, chore.Id, dueAt,
                $"Lembrete: {chore.Title}", null, now, ct);
        }

        return sent;
    }

    private async Task<int> MorningSummariesAsync(DateTimeOffset now, CancellationToken ct)
    {
        // Quem nunca mudou as preferências fica com os valores por omissão (resumo às 7h30).
        var memberships = await db.Memberships.AsNoTracking()
            .Select(m => new
            {
                m.UserId,
                m.HouseholdId,
                m.Household.TimeZoneId,
                m.User.Language,
                Preferences = db.NotificationPreferences.Where(p => p.UserId == m.UserId)
                    .Select(p => new { p.MorningSummary, p.MorningAt })
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);
        var defaults = new NotificationPreferences();
        var sent = 0;
        foreach (var m in memberships)
        {
            if (m.Preferences is { MorningSummary: false })
            {
                continue;
            }

            var household = new Household { Name = "", TimeZoneId = m.TimeZoneId };
            var today = household.Today(now);
            var dueAt = ZonedTime.ToInstant(today.ToDateTime(m.Preferences?.MorningAt ?? defaults.MorningAt), household.TimeZone);
            if (!IsDue(dueAt, now))
            {
                continue;
            }

            var summary = await MorningSummaryAsync(m.HouseholdId, m.UserId, household, today, m.Language, ct);
            if (summary is null)
            {
                continue;
            }

            sent += await SendAsync(m.HouseholdId, [m.UserId], NotificationKind.MorningSummary, m.HouseholdId, dueAt,
                "O teu dia", summary, now, ct);
        }

        return sent;
    }

    /// <summary>"Natação às 17:30 · 3 tarefas · 5 coisas por comprar", só com o que a pessoa vê; null se não houver nada.</summary>
    private async Task<string?> MorningSummaryAsync(
        Guid householdId, Guid userId, Household household, DateOnly today, string language, CancellationToken ct)
    {
        var parts = new List<string>();
        var zone = household.TimeZone;

        if (await CanAsync(userId, householdId, HouseholdModule.Calendar, ModuleAccess.View, ct))
        {
            var start = ZonedTime.StartOfDay(today, zone);
            var end = ZonedTime.StartOfDay(today.AddDays(1), zone);
            var events = await db.CalendarEvents.AsNoTracking()
                .Where(e => e.HouseholdId == householdId)
                .Where(e => (e.Recurrence != null && (e.RecurrenceEndDate == null || e.RecurrenceEndDate >= today))
                    || (e.StartsAt < end && e.EndsAt > start)
                    || (e.StartDate <= today && e.EndDate >= today))
                .ToListAsync(ct);
            var mine = events
                .SelectMany(e => e.Occurrences(start, end, zone))
                .Where(o => o.Event.ParticipantUserIds.Count == 0 || o.Event.ParticipantUserIds.Contains(userId)
                    || o.Event.DropOffUserId == userId || o.Event.PickUpUserId == userId)
                .OrderBy(o => o.Start)
                .ToList();
            parts.AddRange(mine.Take(3).Select(o => o.IsAllDay
                ? o.Event.Title
                : $"{o.Event.Title} às {TimeZoneInfo.ConvertTime(o.Start, zone):HH:mm}"));
            if (mine.Count > 3)
            {
                parts.Add($"+{mine.Count - 3} eventos");
            }
        }

        if (await CanAsync(userId, householdId, HouseholdModule.Chores, ModuleAccess.Participate, ct))
        {
            var chores = await db.Chores.AsNoTracking()
                .Where(c => c.HouseholdId == householdId && c.ArchivedAt == null && c.CompletedAt == null)
                .Where(c => c.AssigneeUserId == userId || c.AssigneeUserId == null)
                .Where(c => c.DueDate != null && c.DueDate <= today)
                .ToListAsync(ct);
            var count = chores.Count(c => c.IsAvailableOn(today));
            if (count > 0)
            {
                parts.Add(count == 1 ? "1 tarefa" : $"{count} tarefas");
            }
        }

        if (await CanAsync(userId, householdId, HouseholdModule.Shopping, ModuleAccess.View, ct))
        {
            var missing = await db.Set<ShoppingItem>().AsNoTracking()
                .CountAsync(i => i.List.HouseholdId == householdId && !i.IsChecked, ct);
            if (missing > 0)
            {
                parts.Add(missing == 1 ? "1 coisa por comprar" : $"{missing} coisas por comprar");
            }
        }

        // Cada parte traduz-se à parte (a frase toda não tem modelo no catálogo).
        return parts.Count == 0 ? null : string.Join(" · ", parts.Select(p => Texts.Translate(p, language)));
    }

    private async Task<int> WeeklySummariesAsync(DateTimeOffset now, CancellationToken ct)
    {
        var households = await db.Households.AsNoTracking().ToListAsync(ct);
        var sent = 0;
        foreach (var household in households)
        {
            var today = household.Today(now);
            if (today.DayOfWeek != DayOfWeek.Sunday)
            {
                continue;
            }

            var dueAt = ZonedTime.ToInstant(today.ToDateTime(WeeklySummaryAt), household.TimeZone);
            if (!IsDue(dueAt, now))
            {
                continue;
            }

            var managers = await access.MembersWithAsync(household.Id, HouseholdModule.Chores, ModuleAccess.Manage, ct);
            var wanting = await WantsAsync(managers, p => p.WeeklySummary, ct);
            if (wanting.Count == 0)
            {
                continue;
            }

            var from = ZonedTime.StartOfDay(today.AddDays(-6), household.TimeZone);
            var done = await db.Set<ChoreCompletion>().AsNoTracking()
                .Where(c => c.Chore.HouseholdId == household.Id && c.CompletedAt >= from && c.RejectedAt == null)
                .CountAsync(ct);
            var overdue = await db.Chores.AsNoTracking()
                .CountAsync(c => c.HouseholdId == household.Id && c.ArchivedAt == null && c.CompletedAt == null
                    && c.DueDate != null && c.DueDate < today, ct);
            var pending = await db.RewardRedemptions.AsNoTracking()
                .CountAsync(r => r.HouseholdId == household.Id && r.Status == RedemptionStatus.Pending, ct);
            var body = $"{done} tarefas feitas · {overdue} em atraso · {pending} pedidos à espera";
            sent += await SendAsync(household.Id, wanting, NotificationKind.WeeklySummary, household.Id, dueAt,
                "A semana da casa", body, now, ct);
        }

        return sent;
    }

    /// <summary>No último dia de cada período, às 18h, avisa quem gere as recompensas se a mesada ainda não foi paga.</summary>
    private async Task<int> AllowanceRemindersAsync(DateTimeOffset now, CancellationToken ct)
    {
        var allowances = await db.Allowances.AsNoTracking().ToListAsync(ct);
        if (allowances.Count == 0)
        {
            return 0;
        }

        var households = await HouseholdsAsync(allowances.Select(a => a.HouseholdId), ct);
        var sent = 0;
        foreach (var allowance in allowances)
        {
            var household = households[allowance.HouseholdId];
            var today = household.Today(now);
            var start = Allowance.PeriodStart(today, allowance.Period);
            if (Allowance.PeriodEnd(start, allowance.Period) != today || start < allowance.StartDate)
            {
                continue;
            }

            var dueAt = ZonedTime.ToInstant(today.ToDateTime(AllowanceReminderAt), household.TimeZone);
            if (!IsDue(dueAt, now)
                || await db.AllowancePayments.AnyAsync(p => p.HouseholdId == allowance.HouseholdId
                    && p.UserId == allowance.UserId && p.PeriodStart == start, ct))
            {
                continue;
            }

            var name = await db.Users.Where(u => u.Id == allowance.UserId).Select(u => u.DisplayName).SingleOrDefaultAsync(ct);
            if (name is null)
            {
                continue;
            }

            var managers = await access.MembersWithAsync(allowance.HouseholdId, HouseholdModule.Rewards, ModuleAccess.Manage, ct);
            sent += await SendAsync(allowance.HouseholdId, managers, NotificationKind.AllowanceDue, allowance.UserId, dueAt,
                $"Mesada de {name} por pagar", null, now, ct);
        }

        return sent;
    }

    /// <summary>
    /// A área Casa (F4-07, F4-08, F4-12, F4-13), às 9h no fuso da casa: garantia a acabar daqui a 30 dias, manutenção
    /// que vence hoje, IUC no primeiro dia do mês da matrícula, inspeção daqui a 30 dias, as datas do IRS (se a casa
    /// os quiser), documentos que caducam daqui a 60 e 30 dias (ao dono e a quem gere) e aniversários amanhã.
    /// </summary>
    private async Task<int> HomeRemindersAsync(DateTimeOffset now, CancellationToken ct)
    {
        var households = await db.Households.AsNoTracking().ToListAsync(ct);
        var sent = 0;
        foreach (var household in households)
        {
            var today = household.Today(now);
            var dueAt = ZonedTime.ToInstant(today.ToDateTime(HomeRemindersAt), household.TimeZone);
            if (!IsDue(dueAt, now))
            {
                continue;
            }

            var reminders = new List<(Guid Entity, string Title, Guid? Owner, bool Everyone)>();
            foreach (var a in await db.Appliances.AsNoTracking().Where(a => a.HouseholdId == household.Id).ToListAsync(ct))
            {
                if (a.WarrantyUntil == today.AddDays(30))
                {
                    reminders.Add((a.Id, $"A garantia de «{a.Name}» acaba daqui a 30 dias", null, false));
                }

                foreach (var task in a.Maintenance.Where(m => m.NextDue == today))
                {
                    reminders.Add((task.Id, $"Manutenção: {task.Title} ({a.Name})", null, false));
                }

                if (a.FirstRegistration is { } first)
                {
                    if (Domain.Home.PortugalCalendar.NextIuc(first, today) == today)
                    {
                        reminders.Add((a.Id, $"IUC de «{a.Name}» para pagar este mês", null, false));
                    }

                    if (Domain.Home.PortugalCalendar.NextInspection(first, today) == today.AddDays(30))
                    {
                        reminders.Add((a.Id, $"Inspeção de «{a.Name}» daqui a 30 dias", null, false));
                    }
                }
            }

            if (household.IrsReminders)
            {
                reminders.AddRange(Domain.Home.PortugalCalendar.IrsDates(today.Year)
                    .Where(d => d.Date == today)
                    .Select(d => (household.Id, d.Title, (Guid?)null, false)));
            }

            foreach (var d in await db.HomeDocuments.AsNoTracking()
                         .Where(d => d.HouseholdId == household.Id && d.ExpiresOn != null).ToListAsync(ct))
            {
                if (d.ExpiresOn == today.AddDays(60) || d.ExpiresOn == today.AddDays(30))
                {
                    var days = d.ExpiresOn!.Value.DayNumber - today.DayNumber;
                    reminders.Add((d.Id, $"«{d.Name}» caduca daqui a {days} dias", d.OwnerUserId, false));
                }
            }

            foreach (var c in await db.HouseContacts.AsNoTracking()
                         .Where(c => c.HouseholdId == household.Id && c.Birthday != null).ToListAsync(ct))
            {
                if (Home.HomeEndpoints.NextBirthday(c.Birthday!.Value, today) == today.AddDays(1))
                {
                    reminders.Add((c.Id, $"Amanhã faz anos: {c.Name}", null, true));
                }
            }

            if (reminders.Count == 0)
            {
                continue;
            }

            var managers = await access.MembersWithAsync(household.Id, HouseholdModule.Home, ModuleAccess.Manage, ct);
            var viewers = await access.MembersWithAsync(household.Id, HouseholdModule.Home, ModuleAccess.View, ct);
            foreach (var (entity, title, owner, everyone) in reminders)
            {
                // Os documentos vão ao dono (se ainda vir a área) e a quem gere; os aniversários a quem vê a área; o
                // resto a quem gere.
                List<Guid> recipients = owner is { } who
                    ? [.. managers.Append(who).Where(viewers.Contains).Distinct()]
                    : everyone ? viewers : managers;
                sent += await SendAsync(household.Id, recipients, NotificationKind.HomeReminder, entity, dueAt, title, null, now, ct);
            }
        }

        return sent;
    }

    private async Task<bool> CanAsync(Guid userId, Guid householdId, HouseholdModule module, ModuleAccess needed, CancellationToken ct) =>
        (await access.MembersWithAsync(householdId, module, needed, ct)).Contains(userId);

    private async Task<List<Guid>> WantsAsync(List<Guid> users, Func<NotificationPreferences, bool> wants, CancellationToken ct)
    {
        var preferences = await db.NotificationPreferences.AsNoTracking()
            .Where(p => users.Contains(p.UserId))
            .ToDictionaryAsync(p => p.UserId, ct);
        var defaults = new NotificationPreferences();
        return [.. users.Where(u => wants(preferences.GetValueOrDefault(u) ?? defaults))];
    }

    private async Task<Dictionary<Guid, TimeZoneInfo>> ZonesAsync(IEnumerable<Guid> householdIds, CancellationToken ct) =>
        (await HouseholdsAsync(householdIds, ct)).ToDictionary(h => h.Key, h => h.Value.TimeZone);

    private async Task<Dictionary<Guid, Household>> HouseholdsAsync(IEnumerable<Guid> householdIds, CancellationToken ct)
    {
        var ids = householdIds.Distinct().ToList();
        return await db.Households.AsNoTracking().Where(h => ids.Contains(h.Id)).ToDictionaryAsync(h => h.Id, ct);
    }

    /// <summary>
    /// Reserva o envio a cada pessoa (uma linha com índice único) e só a quem conseguiu reservar manda o aviso. Uma
    /// reserva que já existe (outra volta, outro processo) salta-se sem erro.
    /// </summary>
    private async Task<int> SendAsync(
        Guid householdId,
        IReadOnlyCollection<Guid> recipients,
        NotificationKind kind,
        Guid entityId,
        DateTimeOffset dueAt,
        string title,
        string? body,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var reserved = new List<Guid>();
        foreach (var userId in recipients.Distinct())
        {
            var reservation = new SentReminder { UserId = userId, Kind = kind, EntityId = entityId, DueAt = dueAt, SentAt = now };
            db.SentReminders.Add(reservation);
            try
            {
                await db.SaveChangesAsync(ct);
                reserved.Add(userId);
            }
            catch (DbUpdateException e) when (ApiExceptionHandler.IsUniqueViolation(e))
            {
                db.Entry(reservation).State = EntityState.Detached;
            }
        }

        if (reserved.Count == 0)
        {
            return 0;
        }

        await notifications.NotifyAsync(householdId, reserved, Guid.Empty, kind, title, body, entityId, ct);
        logger.LogDebug("{Kind}: {Count} avisos para {Entity}", kind, reserved.Count, entityId);
        return reserved.Count;
    }
}

/// <summary>Corre o <see cref="ReminderService"/> de minuto a minuto (<c>Reminders:Enabled=false</c> desliga-o, como nos testes).</summary>
public class ReminderWorker(IServiceScopeFactory scopes, ILogger<ReminderWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ReminderService>().RunAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<Shopping.RecurringItemsService>().RunAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Falha nos lembretes; volta a tentar daqui a um minuto.");
            }
        }
    }
}
