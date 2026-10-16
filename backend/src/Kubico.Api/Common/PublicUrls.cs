namespace Kubico.Api.Common;

public static class PublicUrls
{
    /// <summary>
    /// O endereço público da API. Atrás de um proxy, PublicBaseUrl diz qual é (ex.: https://api.kubico.pt);
    /// sem ele, usa-se o do pedido.
    /// </summary>
    public static string BaseUrl(HttpRequest request, IConfiguration configuration) =>
        configuration["PublicBaseUrl"]?.TrimEnd('/') ?? $"{request.Scheme}://{request.Host}{request.PathBase}";
}
