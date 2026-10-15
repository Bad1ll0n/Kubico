using System.ComponentModel.DataAnnotations;

namespace Kubico.Api.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "kubico";

    [Required]
    public string Audience { get; set; } = "kubico-apps";

    /// <summary>Chave HMAC com pelo menos 32 caracteres. Em produção vem de uma variável de ambiente ou Key Vault.</summary>
    [Required, MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 365)]
    public int AccessTokenLifetimeDays { get; set; } = 7;
}
