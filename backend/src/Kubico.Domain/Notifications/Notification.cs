namespace Kubico.Domain.Notifications;

public enum NotificationKind
{
    /// <summary>Uma tarefa passou a ser tua (atribuída ou a tua vez na rotação).</summary>
    ChoreAssigned = 0,

    /// <summary>Foste acrescentado a um evento.</summary>
    EventAssigned = 1,

    /// <summary>Tens de levar ou ir buscar alguém.</summary>
    RideAssigned = 2,

    /// <summary>Alguém pediu uma recompensa e um adulto tem de aprovar.</summary>
    RewardRequested = 3,

    /// <summary>O teu pedido de recompensa foi aprovado ou recusado.</summary>
    RewardDecided = 4,

    /// <summary>Alguém concluiu uma tarefa com foto de prova (para quem gere as tarefas ver).</summary>
    ChoreCompleted = 5,

    /// <summary>Uma tarefa que fizeste não foi aceite e voltou para ti.</summary>
    ChoreRejected = 6,

    /// <summary>A tua mesada foi paga.</summary>
    AllowancePaid = 7,

    /// <summary>Um perfil de criança ficou bloqueado por PINs errados (para quem gere a casa).</summary>
    ChildPinLocked = 8,

    /// <summary>Lembrete de um evento (minutos antes do início).</summary>
    EventReminder = 9,

    /// <summary>Lembrete de uma tarefa à hora escolhida.</summary>
    ChoreReminder = 10,

    /// <summary>O resumo da manhã: o que há hoje.</summary>
    MorningSummary = 11,

    /// <summary>O resumo da semana para quem gere as tarefas.</summary>
    WeeklySummary = 12,

    /// <summary>Há mesadas por pagar (para quem gere as recompensas).</summary>
    AllowanceDue = 13,

    /// <summary>A casa: garantia a acabar, manutenção, IUC, inspeção, IRS, documento a caducar, aniversário.</summary>
    HomeReminder = 14,
}

/// <summary>
/// Um lembrete ou resumo já enviado a uma pessoa: o índice único (pessoa, tipo, coisa, instante) impede que saia
/// duas vezes, mesmo com o serviço a correr em mais do que um sítio ou duas vezes no mesmo minuto.
/// </summary>
public class SentReminder
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public NotificationKind Kind { get; set; }

    /// <summary>O evento, a tarefa ou a casa (nos resumos).</summary>
    public Guid EntityId { get; set; }

    /// <summary>O instante a que o lembrete era devido (identifica a ocorrência).</summary>
    public DateTimeOffset DueAt { get; set; }

    public DateTimeOffset SentAt { get; set; }
}

/// <summary>
/// Um aviso para um membro. Aparece logo na app; o envio para o telemóvel segue as preferências (já, num resumo
/// diário, ou nunca) e espera pelo fim das horas de silêncio.
/// </summary>
public class Notification
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid HouseholdId { get; set; }
    public NotificationKind Kind { get; set; }
    public required string Title { get; set; }
    public string? Body { get; set; }

    /// <summary>A tarefa, o evento ou o pedido de que fala, para a app o abrir.</summary>
    public Guid? EntityId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }

    /// <summary>Quando saiu para o telemóvel (ou ficou resolvida sem envio). Null = ainda por tratar.</summary>
    public DateTimeOffset? PushedAt { get; set; }
}

public enum NotificationDelivery
{
    /// <summary>Cada aviso vai logo para o telemóvel (fora das horas de silêncio).</summary>
    Immediate = 0,

    /// <summary>Um resumo por dia, à hora escolhida.</summary>
    DailyDigest = 1,

    /// <summary>Só dentro da app.</summary>
    InAppOnly = 2,
}

/// <summary>Como cada membro quer receber os avisos no telemóvel. Quem nunca as mudou fica com os valores por omissão.</summary>
public class NotificationPreferences
{
    public Guid UserId { get; set; }
    public NotificationDelivery Delivery { get; set; } = NotificationDelivery.Immediate;

    /// <summary>A que horas sai o resumo diário.</summary>
    public TimeOnly DigestAt { get; set; } = new(19, 0);

    /// <summary>Horas de silêncio (podem passar a meia-noite, ex.: 22:00–08:00). Null = sem horas de silêncio.</summary>
    public TimeOnly? QuietFrom { get; set; }
    public TimeOnly? QuietUntil { get; set; }

    /// <summary>Fuso em que se leem estas horas.</summary>
    public string TimeZoneId { get; set; } = "Europe/Lisbon";

    /// <summary>O resumo da manhã (o que há hoje na casa), à hora <see cref="MorningAt"/> no fuso da casa.</summary>
    public bool MorningSummary { get; set; } = true;

    public TimeOnly MorningAt { get; set; } = new(7, 30);

    /// <summary>O resumo da semana (domingo às 19h), para quem gere as tarefas.</summary>
    public bool WeeklySummary { get; set; } = true;

    public DateTimeOffset? LastDigestAt { get; set; }

    /// <summary>
    /// Antes deste instante não vale a pena voltar a olhar para os avisos por enviar desta pessoa (horas de silêncio,
    /// ou ainda não é a hora do resumo). O envio filtra por ele na própria consulta, em vez de ler e saltar estas
    /// pessoas a cada minuto. Null = sem espera. Volta a null quando as preferências mudam.
    /// </summary>
    public DateTimeOffset? NextPushAt { get; set; }

    /// <summary>
    /// O primeiro instante depois de <paramref name="now"/> em que <see cref="ShouldPushNow"/> pode passar a ser
    /// verdade: só muda no fim do silêncio, à hora do resumo e à meia-noite local (o resumo do dia seguinte). Numa
    /// hora que não existe ou que se repete (mudança de hora) usa-se uma hora antes: mais vale olhar cedo do que tarde.
    /// Null = nunca (só na app).
    /// </summary>
    public DateTimeOffset? NextPushAfter(DateTimeOffset now)
    {
        if (Delivery == NotificationDelivery.InAppOnly)
        {
            return null;
        }

        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        var times = new[] { QuietUntil, DigestAt, TimeOnly.MinValue }.OfType<TimeOnly>().ToList();
        var candidates = Enumerable.Range(0, 3)
            .SelectMany(d => times.Select(t => today.AddDays(d).ToDateTime(t)))
            .Select(local => (Local: local, Instant: Calendar.ZonedTime.ToInstant(local, zone)))
            .Where(c => c.Instant > now)
            .OrderBy(c => c.Instant);
        foreach (var (local, instant) in candidates)
        {
            if (ShouldPushNow(instant))
            {
                var early = zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local) ? instant.AddHours(-1) : instant;
                return early > now ? early : now.AddMinutes(1);
            }
        }

        // Não devia acontecer (há sempre um fim de silêncio ou um resumo em 3 dias); volta-se a ver daqui a uma hora.
        return now.AddHours(1);
    }

    public bool IsQuiet(TimeOnly local) => (QuietFrom, QuietUntil) switch
    {
        ({ } from, { } until) when from < until => local >= from && local < until,
        ({ } from, { } until) => local >= from || local < until,
        _ => false,
    };

    /// <summary>Se os avisos por enviar devem sair agora para o telemóvel.</summary>
    public bool ShouldPushNow(DateTimeOffset now)
    {
        var zone = TimeZoneInfo.TryFindSystemTimeZoneById(TimeZoneId, out var found) ? found : TimeZoneInfo.Utc;
        var local = TimeZoneInfo.ConvertTime(now, zone).DateTime;
        if (Delivery == NotificationDelivery.InAppOnly || IsQuiet(TimeOnly.FromDateTime(local)))
        {
            return false;
        }

        if (Delivery == NotificationDelivery.Immediate)
        {
            return true;
        }

        // Resumo: uma vez por dia, a partir da hora escolhida.
        var lastDay = LastDigestAt is { } last ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(last, zone).DateTime) : (DateOnly?)null;
        return TimeOnly.FromDateTime(local) >= DigestAt && lastDay != DateOnly.FromDateTime(local);
    }
}

/// <summary>Um telemóvel (ou browser) que recebe avisos push: o token que o Firebase lhe deu.</summary>
public class PushDevice
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public required string Token { get; set; }

    /// <summary>android, ios ou web.</summary>
    public required string Platform { get; set; }

    /// <summary>A sessão (família de refresh tokens) em que se registou: terminar essa sessão deixa de enviar avisos.</summary>
    public Guid? SessionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
