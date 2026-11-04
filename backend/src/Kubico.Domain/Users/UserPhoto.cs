namespace Kubico.Domain.Users;

/// <summary>A fotografia de perfil (sem metadados), à parte do <see cref="User"/> para não a ler com a conta.</summary>
public class UserPhoto
{
    public Guid UserId { get; set; }
    public required string ContentType { get; set; }
    public required byte[] Data { get; set; }
}
