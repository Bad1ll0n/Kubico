namespace Kubico.Domain.Support;

public enum FeedbackKind
{
    /// <summary>Alguma coisa não funciona como devia.</summary>
    Problem = 0,

    /// <summary>Uma sugestão.</summary>
    Idea = 1,
}

/// <summary>Uma mensagem enviada de dentro da app para quem mantém o Kubico: um problema ou uma ideia.</summary>
public class Feedback
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }

    /// <summary>A casa que estava aberta quando a mensagem foi escrita (ajuda a perceber o contexto).</summary>
    public Guid? HouseholdId { get; set; }

    public FeedbackKind Kind { get; set; }
    public required string Message { get; set; }

    /// <summary>De onde veio: android, ios, windows, macos, linux ou web.</summary>
    public string? Device { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
