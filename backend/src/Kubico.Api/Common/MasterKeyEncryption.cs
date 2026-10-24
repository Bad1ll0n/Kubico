using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Common;

/// <summary>
/// A chave-mestra que cifra as chaves do Data Protection guardadas na base de dados (que por sua vez cifram os
/// refresh tokens do Google e do Microsoft, as palavras-passe do iCloud e os segredos da 2FA). Assim, uma cópia da
/// base de dados sozinha não chega para os decifrar. Vem de <c>DataProtection:MasterKey</c> (base64 de 32 bytes),
/// fora da base de dados: em Production é obrigatória; nos outros ambientes, sem ela, usa-se uma fixa de
/// desenvolvimento.
/// </summary>
public sealed class DataProtectionMasterKey
{
    public const string Setting = "DataProtection:MasterKey";

    /// <summary>Só para desenvolvimento e testes (está no código, não protege nada a sério).</summary>
    private const string DevelopmentKey = "S3VCaUNvLURFVi1vbmx5LW1hc3Rlci1rZXktMzJieXQ=";

    public DataProtectionMasterKey(byte[] key)
    {
        if (key.Length != 32)
        {
            throw new InvalidOperationException($"{Setting} tem de ter 32 bytes (em base64).");
        }

        Key = key;

        // Identifica a chave sem a revelar: ao decifrar com outra chave, o erro diz qual se esperava.
        Fingerprint = Convert.ToHexString(SHA256.HashData(key))[..16];
    }

    public byte[] Key { get; }

    public string Fingerprint { get; }

    /// <summary>A chave da configuração; sem ela, a de desenvolvimento (só fora de Production).</summary>
    public static DataProtectionMasterKey FromConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[Setting];
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException(
                    $"Falta {Setting} (base64 de 32 bytes, por exemplo com `openssl rand -base64 32`): em produção as chaves " +
                    "do Data Protection têm de ficar cifradas com uma chave fora da base de dados.");
            }

            configured = DevelopmentKey;
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(configured.Trim());
        }
        catch (FormatException)
        {
            throw new InvalidOperationException($"{Setting} tem de estar em base64 (32 bytes).");
        }

        return new DataProtectionMasterKey(key);
    }
}

/// <summary>Cifra cada chave do Data Protection com AES-256-GCM e a chave-mestra, antes de a guardar.</summary>
public sealed class MasterKeyXmlEncryptor(DataProtectionMasterKey master) : IXmlEncryptor
{
    internal static readonly byte[] Context = "Kubico.DataProtection.Keys.v1"u8.ToArray();

    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plaintext = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(master.Key, tag.Length))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Context);
        }

        CryptographicOperations.ZeroMemory(plaintext);
        var element = new XElement("encryptedKey",
            new XComment(" Cifrada com DataProtection:MasterKey (AES-256-GCM). "),
            new XAttribute("masterKey", master.Fingerprint),
            new XElement("nonce", Convert.ToBase64String(nonce)),
            new XElement("tag", Convert.ToBase64String(tag)),
            new XElement("value", Convert.ToBase64String(ciphertext)));
        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }
}

/// <summary>Decifra as chaves guardadas (o Data Protection cria-o pelo tipo, com acesso aos serviços).</summary>
public sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    public XElement Decrypt(XElement encryptedElement)
    {
        var master = services.GetRequiredService<DataProtectionMasterKey>();
        var expected = (string?)encryptedElement.Attribute("masterKey");
        if (expected is not null && expected != master.Fingerprint)
        {
            throw new CryptographicException(
                $"Esta chave do Data Protection foi cifrada com outra chave-mestra ({expected}); a configurada é {master.Fingerprint}.");
        }

        var nonce = Convert.FromBase64String((string)encryptedElement.Element("nonce")!);
        var tag = Convert.FromBase64String((string)encryptedElement.Element("tag")!);
        var ciphertext = Convert.FromBase64String((string)encryptedElement.Element("value")!);
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(master.Key, tag.Length))
        {
            aes.Decrypt(nonce, ciphertext, tag, plaintext, MasterKeyXmlEncryptor.Context);
        }

        try
        {
            return XElement.Parse(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
}

public static class MasterKeyEncryptionExtensions
{
    /// <summary>
    /// Data Protection com as chaves na base de dados (sobrevivem a reinícios e a novas versões), cifradas com a
    /// chave-mestra da configuração.
    /// </summary>
    public static IServiceCollection AddKubicoDataProtection(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var master = DataProtectionMasterKey.FromConfiguration(configuration, environment);
        services.AddSingleton(master);
        services.AddDataProtection().SetApplicationName("Kubico").PersistKeysToDbContext<KubicoDbContext>();
        services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(
            new ConfigureOptions<KeyManagementOptions>(o => o.XmlEncryptor = new MasterKeyXmlEncryptor(master)));
        return services;
    }
}
