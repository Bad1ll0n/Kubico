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

    public DateTimeOffset? LastDigestAt { get; set; }

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

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}
