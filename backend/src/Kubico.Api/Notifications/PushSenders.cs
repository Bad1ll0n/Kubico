using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Notifications;

public record PushMessage(string Title, string? Body, IReadOnlyDictionary<string, string> Data);

public enum PushResult
{
    Sent,

    /// <summary>O telemóvel já não existe (app desinstalada, token renovado): esquece-se o token.</summary>
    InvalidToken,

    /// <summary>Falha passageira: volta-se a tentar mais tarde.</summary>
    Failed,
}

/// <summary>Quem entrega os avisos nos telemóveis.</summary>
public interface IPushSender
{
    Task<PushResult> SendAsync(string deviceToken, PushMessage message, CancellationToken ct);
}

/// <summary>Sem Firebase configurado: os avisos ficam só na app e o envio aparece no registo.</summary>
public class LogPushSender(ILogger<LogPushSender> logger) : IPushSender
{
    public Task<PushResult> SendAsync(string deviceToken, PushMessage message, CancellationToken ct)
    {
        logger.LogInformation("Push (sem Firebase configurado): {Title}", message.Title);
        return Task.FromResult(PushResult.Sent);
    }
}

public class FcmOptions
{
    public const string SectionName = "Push:Fcm";

    /// <summary>O JSON da conta de serviço do Firebase (Definições do projeto → Contas de serviço → Gerar chave).</summary>
    public string? ServiceAccountJson { get; set; }

    /// <summary>Em alternativa ao JSON: o caminho do ficheiro descarregado (mais fácil de pôr nos user-secrets).</summary>
    public string? ServiceAccountFile { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServiceAccountJson) || !string.IsNullOrWhiteSpace(ServiceAccountFile);

    public string ReadServiceAccount() =>
        !string.IsNullOrWhiteSpace(ServiceAccountJson) ? ServiceAccountJson : File.ReadAllText(ServiceAccountFile!);
}

/// <summary>
/// Firebase Cloud Messaging (API HTTP v1). Autentica-se com a conta de serviço: assina um JWT (RS256), troca-o
/// por um token de acesso OAuth (que guarda até perto de expirar) e envia uma mensagem por telemóvel.
/// </summary>
public class FcmPushSender(HttpClient http, IOptions<FcmOptions> options, TimeProvider clock, ILogger<FcmPushSender> logger)
    : IPushSender
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly ServiceAccount _account = JsonSerializer.Deserialize<ServiceAccount>(options.Value.ReadServiceAccount())
        ?? throw new InvalidOperationException("A conta de serviço do Firebase (Push:Fcm) é inválida.");

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public async Task<PushResult> SendAsync(string deviceToken, PushMessage message, CancellationToken ct)
    {
        var url = $"https://fcm.googleapis.com/v1/projects/{_account.ProjectId}/messages:send";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            // O destino é o Firebase Installation ID (FID) que a app registou ("token" está obsoleto).
            Content = JsonContent.Create(new
            {
                message = new
                {
                    fid = deviceToken,
                    notification = new { title = message.Title, body = message.Body },
                    data = message.Data,
                },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await AccessTokenAsync(ct));

        using var response = await http.SendAsync(request, ct);
        if (response.IsSuccessStatusCode)
        {
            return PushResult.Sent;
        }

        var error = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == HttpStatusCode.NotFound || error.Contains("UNREGISTERED", StringComparison.Ordinal))
        {
            return PushResult.InvalidToken;
        }

        logger.LogWarning("FCM recusou o envio ({Status}): {Error}", (int)response.StatusCode, error);
        return PushResult.Failed;
    }

    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow();
            if (_accessToken is not null && now < _accessTokenExpiresAt.AddMinutes(-1))
            {
                return _accessToken;
            }

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = SignedAssertion(now),
            });
            using var response = await http.PostAsync(_account.TokenUri, form, ct);
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<TokenResponse>(ct)
                ?? throw new InvalidOperationException("Resposta vazia do servidor de tokens da Google.");

            _accessToken = token.AccessToken;
            _accessTokenExpiresAt = now.AddSeconds(token.ExpiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private string SignedAssertion(DateTimeOffset now)
    {
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"alg":"RS256","typ":"JWT"}"""));
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = _account.ClientEmail,
            scope = Scope,
            aud = _account.TokenUri,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddHours(1).ToUnixTimeSeconds(),
        }));

        using var rsa = RSA.Create();
        rsa.ImportFromPem(_account.PrivateKey);
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{Base64Url.EncodeToString(signature)}";
    }

    private sealed record ServiceAccount(
        [property: JsonPropertyName("project_id")] string ProjectId,
        [property: JsonPropertyName("client_email")] string ClientEmail,
        [property: JsonPropertyName("private_key")] string PrivateKey,
        [property: JsonPropertyName("token_uri")] string TokenUri = "https://oauth2.googleapis.com/token");

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
