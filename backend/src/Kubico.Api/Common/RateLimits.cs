using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Kubico.Api.Common;

/// <summary>
/// Limite de pedidos por endereço IP nos pedidos sem sessão (entrar, registar, renovar a sessão, entrada das
/// crianças): acima do limite, 429 com <c>Retry-After</c>. Os valores (pedidos por minuto) vêm de
/// <c>RateLimiting:*PerMinute</c>; <c>RateLimiting:Enabled=false</c> desliga-o (testes). Fica em memória: com várias
/// instâncias da API, cada uma conta os seus (limitação conhecida, ver CLAUDE.md). Atrás de um proxy, o IP vem do
/// cabeçalho X-Forwarded-For só se o proxy estiver em <c>ForwardedHeaders:KnownProxies</c>.
/// </summary>
public static class RateLimits
{
    public const string Login = "login";
    public const string Register = "register";
    public const string Refresh = "refresh";
    public const string ChildLogin = "child-login";
    public const string ChildProfiles = "child-profiles";

    /// <summary>Os valores por omissão (produção), em pedidos por minuto e por IP.</summary>
    private static readonly Dictionary<string, (string Setting, int PerMinute)> Policies = new()
    {
        [Login] = ("LoginPerMinute", 10),
        [Register] = ("RegisterPerMinute", 5),
        [Refresh] = ("RefreshPerMinute", 30),
        [ChildLogin] = ("ChildLoginPerMinute", 10),
        [ChildProfiles] = ("ChildProfilesPerMinute", 30),
    };

    public static IServiceCollection AddKubicoRateLimits(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("RateLimiting");
        var enabled = section.GetValue("Enabled", true);
        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            foreach (var (name, (setting, perMinute)) in Policies)
            {
                var limit = section.GetValue(setting, perMinute);
                options.AddPolicy(name, http => enabled
                    ? RateLimitPartition.GetFixedWindowLimiter(
                        $"{name}:{http.Connection.RemoteIpAddress}",
                        _ => new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = limit,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        })
                    : RateLimitPartition.GetNoLimiter(name));
            }

            options.OnRejected = async (context, ct) =>
            {
                var seconds = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                    ? Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds))
                    : 60;
                var http = context.HttpContext;
                http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = StatusCodes.Status429TooManyRequests,
                        Title = $"Demasiados pedidos. Tenta outra vez daqui a {seconds} segundos.",
                    },
                });
            };
        });
    }
}
