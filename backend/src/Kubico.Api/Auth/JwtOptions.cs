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

    /// <summary>
    /// Validade do token de acesso. É curta: terminar a sessão só revoga os refresh tokens, e o de acesso que já
    /// saiu continua a valer até expirar.
    /// </summary>
    [Range(1, 1440)]
    public int AccessTokenLifetimeMinutes { get; set; } = 15;

    /// <summary>Quanto tempo uma sessão pode estar parada antes de pedir a palavra-passe outra vez.</summary>
    [Range(1, 365)]
    public int RefreshTokenLifetimeDays { get; set; } = 60;
}
