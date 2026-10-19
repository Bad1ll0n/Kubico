using System.Security.Cryptography;
using System.Text;
using Kubico.Domain.Users;
using Microsoft.AspNetCore.DataProtection;

namespace Kubico.Api.Auth;

/// <summary>
/// Verificação em dois passos: guarda o segredo cifrado (com as chaves do Data Protection), confere os códigos das
/// apps de autenticação e os de recuperação, e emite o "bilhete" curto que liga a palavra-passe certa ao código.
/// </summary>
public sealed class TwoFactorService(IDataProtectionProvider protection)
{
    public const int RecoveryCodeCount = 8;

    /// <summary>Quanto tempo se tem para escrever o código depois da palavra-passe.</summary>
    public static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    // Sem 0/O, 1/I/L: fáceis de copiar à mão.
    private const string RecoveryAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly IDataProtector _secrets = protection.CreateProtector("Kubico.TwoFactor.Secret.v1");
    private readonly ITimeLimitedDataProtector _challenges =
        protection.CreateProtector("Kubico.TwoFactor.Challenge.v1").ToTimeLimitedDataProtector();

    /// <summary>Começa a ligar: um segredo novo, à espera de ser confirmado com um código. Devolve-o em base32.</summary>
    public string BeginSetup(User user)
    {
        var secret = Totp.NewSecret();
        user.PendingTotpSecret = _secrets.Protect(Totp.ToBase32(secret));
        return Totp.ToBase32(secret);
    }

    /// <summary>
    /// Confirma o segredo novo com um código da app. Se servir, a verificação fica ligada e devolve os códigos de
    /// recuperação (só se mostram desta vez); senão, null.
    /// </summary>
    public IReadOnlyList<string>? Enable(User user, string code, DateTimeOffset now)
    {
        if (user.PendingTotpSecret is null || Totp.Match(Secret(user.PendingTotpSecret), code, now, null) is not { } step)
        {
            return null;
        }

        user.TotpSecret = user.PendingTotpSecret;
        user.PendingTotpSecret = null;
        user.LastTotpStep = step;
        var codes = Enumerable.Range(0, RecoveryCodeCount).Select(_ => NewRecoveryCode()).ToList();
        user.RecoveryCodeHashes = [.. codes.Select(Hash)];
        return codes;
    }

    public static void Disable(User user)
    {
        user.TotpSecret = null;
        user.PendingTotpSecret = null;
        user.LastTotpStep = null;
        user.RecoveryCodeHashes = [];
    }

    /// <summary>Um código da app (que ainda não tenha sido usado) ou um de recuperação (que deixa de servir).</summary>
    public bool Verify(User user, string? code, DateTimeOffset now)
    {
        if (user.TotpSecret is null || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        if (Totp.Match(Secret(user.TotpSecret), code, now, user.LastTotpStep) is { } step)
        {
            user.LastTotpStep = step;
            return true;
        }

        var hash = Hash(NormalizeRecoveryCode(code));
        if (user.RecoveryCodeHashes.Contains(hash))
        {
            user.RecoveryCodeHashes = [.. user.RecoveryCodeHashes.Where(h => h != hash)];
            return true;
        }

        return false;
    }

    /// <summary>
    /// O bilhete que prova que a palavra-passe estava certa, para trocar por uma sessão com o código. Expira pelo
    /// relógio real (como os tokens), não pelo da casa.
    /// </summary>
    public string IssueChallenge(Guid userId, string? device) =>
        _challenges.Protect($"{userId}|{device}", ChallengeLifetime);

    /// <summary>De quem é o bilhete (e de que dispositivo), ou null se expirou ou não foi o Kubico a criá-lo.</summary>
    public (Guid UserId, string? Device)? ReadChallenge(string? challenge)
    {
        if (string.IsNullOrEmpty(challenge))
        {
            return null;
        }

        try
        {
            var parts = _challenges.Unprotect(challenge).Split('|', 2);
            return (Guid.Parse(parts[0]), parts[1].Length == 0 ? null : parts[1]);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    private byte[] Secret(string protectedSecret) => Totp.FromBase32(_secrets.Unprotect(protectedSecret));

    private static string NewRecoveryCode()
    {
        var raw = RandomNumberGenerator.GetString(RecoveryAlphabet, 8);
        return $"{raw[..4]}-{raw[4..]}";
    }

    private static string NormalizeRecoveryCode(string code) =>
        new string(code.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray()) is { Length: 8 } clean
            ? $"{clean[..4]}-{clean[4..]}"
            : code.Trim();

    private static string Hash(string code) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
}
