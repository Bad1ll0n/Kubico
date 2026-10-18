using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Kubico.Domain.Users;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Kubico.Api.Auth;

public class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    /// <summary>Claim com a sessão (família de refresh tokens) de onde veio o token: diz qual é "este dispositivo".</summary>
    public const string SessionClaim = "sid";

    public (string Token, DateTimeOffset ExpiresAt) CreateAccessToken(User user, Guid? sessionId = null)
    {
        var now = clock.GetUtcNow();
        var expiresAt = now.AddMinutes(_options.AccessTokenLifetimeMinutes);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
                .. user.Email is { } email ? [new Claim(JwtRegisteredClaimNames.Email, email)] : Array.Empty<Claim>(),
                new Claim(JwtRegisteredClaimNames.Name, user.DisplayName),
                .. sessionId is { } sid ? [new Claim(SessionClaim, sid.ToString())] : Array.Empty<Claim>(),
            ],
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
