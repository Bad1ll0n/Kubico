using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Net.Http.Headers;

namespace Kubico.Api.Common;

/// <summary>
/// Cache HTTP onde faz sentido: as fotos de prova (nunca mudam) e o calendário subscrito (os calendários do telemóvel
/// perguntam de hora a hora). Vão com <c>Cache-Control: private, no-cache</c> e um <c>ETag</c>: só o próprio aparelho
/// guarda uma cópia (nunca as caches partilhadas, S11), e antes de a usar confirma com o servidor, que volta a verificar
/// o acesso e responde 304 sem corpo quando nada mudou. Tudo o resto em /api continua <c>no-store</c>
/// (<see cref="SecurityHeaders"/>).
/// </summary>
public static class HttpCaching
{
    public const string PrivateRevalidate = "private, no-cache";

    private const string OwnPolicyKey = "kubico:own-cache-policy";

    /// <summary>ETag forte a partir do conteúdo (SHA-256, os primeiros 16 bytes).</summary>
    public static string ETagOf(string content) =>
        $"\"{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)), 0, 16).ToLowerInvariant()}\"";

    /// <summary>ETag de algo que nunca muda (uma foto): o próprio id.</summary>
    public static string ETagOf(Guid id) => $"\"{id:N}\"";

    /// <summary>Marca a resposta como guardável só neste aparelho, com revalidação, e com este ETag.</summary>
    public static void PrivateWithETag(HttpContext http, string etag)
    {
        http.Items[OwnPolicyKey] = true;
        http.Response.Headers.CacheControl = PrivateRevalidate;
        http.Response.Headers.ETag = etag;
    }

    /// <summary>O pedido já tem esta versão (If-None-Match): responde-se 304, sem corpo.</summary>
    public static bool ClientHas(HttpContext http, string etag)
    {
        var header = http.Request.Headers.IfNoneMatch;
        if (header.Count == 0)
        {
            return false;
        }

        return EntityTagHeaderValue.TryParseList(header, out var tags)
            && tags.Any(t => t.Equals(EntityTagHeaderValue.Any) || t.Compare(new EntityTagHeaderValue(etag), useStrongComparison: false));
    }

    internal static bool HasOwnPolicy(HttpContext http) => http.Items.ContainsKey(OwnPolicyKey);
}

/// <summary>
/// Compressão das respostas (P3): Brotli e gzip, no nível "Optimal" (Brotli 4, gzip 6), também em HTTPS. Medido no mês
/// de uma casa cheia (980 KB): "Fastest" deixava 211 KB (Brotli 1) e 120 KB (gzip 1); "Optimal" deixa ~60–70 KB com
/// poucos milissegundos a mais. Fica de fora o que leva
/// segredos na resposta (entrar, renovar a sessão, códigos da verificação em dois passos), por precaução contra ataques
/// do tipo BREACH, e o SignalR (as mensagens são pequenas).
/// </summary>
public static class ResponseCompressionSetup
{
    private static readonly PathString[] Excluded = ["/api/auth", "/api/account/2fa", "/hubs"];

    public static IServiceCollection AddKubicoResponseCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(o =>
        {
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
            o.MimeTypes = [.. ResponseCompressionDefaults.MimeTypes, "application/problem+json", "text/calendar"];
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Optimal);
        return services;
    }

    public static IApplicationBuilder UseKubicoResponseCompression(this IApplicationBuilder app) =>
        app.UseWhen(
            http => !Excluded.Any(path => http.Request.Path.StartsWithSegments(path)),
            branch => branch.UseResponseCompression());
}
