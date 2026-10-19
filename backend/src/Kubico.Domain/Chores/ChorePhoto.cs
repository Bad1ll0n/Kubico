namespace Kubico.Domain.Chores;

/// <summary>
/// Foto de prova de uma tarefa feita. Envia-se primeiro e depois conclui-se a tarefa com ela; quem gere as tarefas
/// vê-a no histórico e pode não aceitar a conclusão.
/// </summary>
public class ChorePhoto
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid UploadedByUserId { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>As fotos ficam este tempo; depois apagam-se (o histórico fica, sem foto).</summary>
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);

    /// <summary>Uma foto enviada e nunca usada para concluir uma tarefa apaga-se passado este tempo.</summary>
    public static readonly TimeSpan KeepUnusedFor = TimeSpan.FromDays(1);

    public const int MaxBytes = 5 * 1024 * 1024;

    /// <summary>O tipo da imagem pelos primeiros bytes (JPEG, PNG ou WebP), ou null se não for nenhum destes.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 8 && data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return "image/png";
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }
}
