using Kubico.Api.Auth;

namespace Kubico.Api.Common;

/// <summary>
/// O que a API confirma ao arrancar, para nunca correr num servidor com a configuração de desenvolvimento:
/// fora de Development, a chave do JWT não pode ser a do repositório e o CORS não pode estar aberto a tudo ("*");
/// em Production são obrigatórios a chave do JWT, a chave-mestra do Data Protection (ver
/// <see cref="DataProtectionMasterKey"/>) e o endereço público (<c>PublicBaseUrl</c>, para os endereços de regresso
/// do OAuth e dos calendários não virem do cabeçalho Host).
/// </summary>
public static class ProductionChecks
{
    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        var problems = new List<string>();
        var signingKey = configuration["Jwt:SigningKey"];
        if (signingKey == JwtOptions.DevelopmentSigningKey)
        {
            problems.Add("Jwt:SigningKey é a chave de desenvolvimento do repositório: define uma própria (32+ caracteres).");
        }

        if ((configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []).Contains("*"))
        {
            problems.Add("Cors:AllowedOrigins com \"*\" só é aceite em Development: indica os endereços da app web.");
        }

        if (environment.IsProduction())
        {
            if (string.IsNullOrWhiteSpace(signingKey))
            {
                problems.Add("Falta Jwt:SigningKey (32+ caracteres aleatórios).");
            }

            if (string.IsNullOrWhiteSpace(configuration[DataProtectionMasterKey.Setting]))
            {
                problems.Add($"Falta {DataProtectionMasterKey.Setting} (base64 de 32 bytes).");
            }

            if (!Uri.TryCreate(configuration["PublicBaseUrl"], UriKind.Absolute, out var publicUrl)
                || publicUrl.Scheme is not ("https" or "http"))
            {
                problems.Add("Falta PublicBaseUrl (o endereço público da API, por exemplo https://api.kubico.pt).");
            }
        }

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Configuração insegura para o ambiente {environment.EnvironmentName}:\n - " + string.Join("\n - ", problems));
        }
    }
}

/// <summary>Que origens podem chamar a API a partir de um browser (CORS).</summary>
public static class CorsOrigins
{
    /// <summary>
    /// A origem está na lista: igual, ou com ":*" no fim da entrada, a mesma origem em qualquer porta
    /// ("http://localhost:*"). "*" aceita tudo (só em Development).
    /// </summary>
    public static bool IsAllowed(string origin, IReadOnlyCollection<string> allowed)
    {
        if (allowed.Contains("*"))
        {
            return true;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        foreach (var entry in allowed)
        {
            if (entry.EndsWith(":*", StringComparison.Ordinal))
            {
                if (Uri.TryCreate(entry[..^2], UriKind.Absolute, out var pattern)
                    && string.Equals(pattern.Scheme, uri.Scheme, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(pattern.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            else if (string.Equals(entry.TrimEnd('/'), origin.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Cabeçalhos de segurança em todas as respostas da API.</summary>
public static class SecurityHeaders
{
    /// <summary>
    /// nosniff (o browser não adivinha tipos: uma "foto" nunca corre como HTML), sem iframes, sem Referer, uma CSP
    /// que não deixa carregar nada (a API só devolve dados; as páginas próprias, como o regresso do OAuth, definem a
    /// sua) e, em /api, sem cache (exportação RGPD, fotos, calendário).
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        http.Response.OnStarting(() =>
        {
            var headers = http.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            if (!headers.ContainsKey("Content-Security-Policy"))
            {
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            }

            if (http.Request.Path.StartsWithSegments("/api"))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });
        await next(http);
    });
}
