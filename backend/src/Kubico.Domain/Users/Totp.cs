using System.Security.Cryptography;
using System.Text;

namespace Kubico.Domain.Users;

/// <summary>
/// Códigos de 6 algarismos que mudam a cada 30 segundos (TOTP, RFC 6238), como os do Google Authenticator ou do
/// Microsoft Authenticator. Aceita-se o código do intervalo anterior e do seguinte, para relógios desacertados.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int StepSeconds = 30;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    /// <summary>Um segredo novo (160 bits, como recomenda a RFC 4226).</summary>
    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public static long StepAt(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counter[i] = (byte)(step & 0xFF);
            step >>= 8;
        }

        Span<byte> hash = stackalloc byte[20];
        HMACSHA1.HashData(secret, counter, hash);
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// O intervalo a que o código pertence (o atual, o anterior ou o seguinte), ou null se não servir. Um código já
    /// usado (de um intervalo até <paramref name="lastUsedStep"/>) não volta a servir.
    /// </summary>
    public static long? Match(byte[] secret, string? code, DateTimeOffset now, long? lastUsedStep)
    {
        var digits = new string((code ?? "").Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != Digits)
        {
            return null;
        }

        var current = StepAt(now);
        for (var step = current - 1; step <= current + 1; step++)
        {
            if (step > (lastUsedStep ?? long.MinValue)
                && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(digits)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>O endereço que as apps de autenticação leem no código QR.</summary>
    public static string SetupUri(string base32Secret, string account) =>
        $"otpauth://totp/Kubico:{Uri.EscapeDataString(account)}?secret={base32Secret}&issuer=Kubico&digits={Digits}&period={StepSeconds}";

    public static string ToBase32(byte[] data)
    {
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            text.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return text.ToString();
    }

    public static byte[] FromBase32(string text)
    {
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text.ToUpperInvariant().Where(c => c != '=' && !char.IsWhiteSpace(c)))
        {
            var value = Base32Alphabet.IndexOf(c);
            if (value < 0)
            {
                throw new FormatException("Não é base32.");
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
