using Kubico.Api.Activity;
using Kubico.Api.Chores;
using Kubico.Domain.Calendar;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Common;

/// <summary>O que uma limpeza apagou ou acertou (para o registo e os testes).</summary>
public record MaintenanceReport(int Activity, int Notifications, int RefreshTokens, int Photos, int RecurrenceEnds);

/// <summary>
/// Limpezas que não dependem de ninguém abrir um ecrã (P13): o histórico com mais de 30 dias, os avisos com mais de
/// 90, os refresh tokens expirados (de quem nunca mais voltou a entrar) e as fotos de prova expiradas, de todas as
/// casas. Também acerta o fim das séries do calendário (<see cref="CalendarEvent.RecurrenceEndDate"/>) que vieram de
/// antes da coluna existir.
/// </summary>
public class MaintenanceService(KubicoDbContext db, TimeProvider clock)
{
    /// <summary>Os avisos guardam-se este tempo (a app mostra os 50 mais recentes).</summary>
    public static readonly TimeSpan KeepNotificationsFor = TimeSpan.FromDays(90);

    public async Task<MaintenanceReport> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var oldNotifications = now - KeepNotificationsFor;
        return new MaintenanceReport(
            await ActivityLog.PruneAsync(db, now, ct),
            await db.Notifications.Where(n => n.CreatedAt < oldNotifications).ExecuteDeleteAsync(ct),

            // Um token expirado já não serve para nada; os revogados e ainda válidos ficam (detetam reutilizações).
            await db.RefreshTokens.Where(t => t.ExpiresAt < now).ExecuteDeleteAsync(ct),
            await ChoreHistoryEndpoints.CleanUpAsync(db, null, now, ct),
            await BackfillRecurrenceEndsAsync(ct));
    }

    /// <summary>
    /// As séries gravadas antes de haver <see cref="CalendarEvent.RecurrenceEndDate"/> ficam com ele calculado (até lá
    /// a consulta do calendário inclui-as sempre, por isso nada se perde). Por lotes de 500, sem mudar o UpdatedAt.
    /// </summary>
    private async Task<int> BackfillRecurrenceEndsAsync(CancellationToken ct)
    {
        // As que continuam sem fim (repetição inválida) ficam à frente das que faltam (ordem do Id): saltam-se.
        var done = 0;
        var invalid = 0;
        while (true)
        {
            var batch = await db.CalendarEvents
                .Where(e => e.Recurrence != null && e.RecurrenceEndDate == null)
                .OrderBy(e => e.Id)
                .Skip(invalid)
                .Take(500)
                .ToListAsync(ct);
            if (batch.Count == 0)
            {
                return done;
            }

            foreach (var calendarEvent in batch)
            {
                calendarEvent.UpdateRecurrenceEnd();
            }

            var computed = batch.Count(e => e.RecurrenceEndDate is not null);
            (done, invalid) = (done + computed, invalid + batch.Count - computed);
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
        }
    }
}

/// <summary>Corre o <see cref="MaintenanceService"/> ao arrancar e depois uma vez por dia (desligável com Maintenance:Enabled).</summary>
public class MaintenanceWorker(IServiceScopeFactory scopes, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var report = await scope.ServiceProvider.GetRequiredService<MaintenanceService>().RunAsync(stoppingToken);
                logger.LogInformation("Limpeza diária: {Report}", report);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Falha na limpeza diária; volta a tentar amanhã.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
