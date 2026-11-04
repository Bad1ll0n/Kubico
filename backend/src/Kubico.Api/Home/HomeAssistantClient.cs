using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kubico.Api.Home;

/// <summary>Uma entidade do Home Assistant: o id ("sensor.maquina_de_lavar"), o estado atual e o nome que lá tem.</summary>
public record HomeAssistantEntity(string EntityId, string State, string? Name);

/// <summary>Um problema ao falar com o Home Assistant, com a mensagem pronta a mostrar.</summary>
public class HomeAssistantException(string message, bool unauthorized = false) : Exception(message)
{
    public bool Unauthorized { get; } = unauthorized;
}

/// <summary>A API REST do Home Assistant (<c>/api/</c> e <c>/api/states</c>, com o token de longa duração).</summary>
public interface IHomeAssistantClient
{
    /// <summary>Confirma que o endereço é um Home Assistant e que o token serve.</summary>
    Task CheckAsync(Uri baseUrl, string token, CancellationToken ct);

    Task<IReadOnlyList<HomeAssistantEntity>> StatesAsync(Uri baseUrl, string token, CancellationToken ct);
}

public sealed class HomeAssistantClient(HttpClient http) : IHomeAssistantClient
{
    public async Task CheckAsync(Uri baseUrl, string token, CancellationToken ct)
    {
        using var response = await SendAsync(baseUrl, "api/", token, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!body.Contains("API running", StringComparison.OrdinalIgnoreCase))
        {
            throw new HomeAssistantException("Este endereço não parece ser um Home Assistant.");
        }
    }

    public async Task<IReadOnlyList<HomeAssistantEntity>> StatesAsync(Uri baseUrl, string token, CancellationToken ct)
    {
        using var response = await SendAsync(baseUrl, "api/states", token, ct);
        try
        {
            var states = await response.Content.ReadFromJsonAsync<List<State>>(ct) ?? [];
            return [.. states.Where(s => s.EntityId is not null)
                .Select(s => new HomeAssistantEntity(s.EntityId!, s.Value ?? "", s.Attributes?.FriendlyName))];
        }
        catch (JsonException)
        {
            throw new HomeAssistantException("O Home Assistant respondeu algo que não se percebe.");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri baseUrl, string path, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new HomeAssistantException("Não foi possível chegar ao Home Assistant nesse endereço.");
        }

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            response.Dispose();
            throw new HomeAssistantException("O Home Assistant não aceitou o token.", unauthorized: true);
        }

        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HomeAssistantException("O Home Assistant respondeu com um erro.");
        }

        return response;
    }

    private sealed record State(
        [property: JsonPropertyName("entity_id")] string? EntityId,
        [property: JsonPropertyName("state")] string? Value,
        [property: JsonPropertyName("attributes")] Attributes? Attributes);

    private sealed record Attributes([property: JsonPropertyName("friendly_name")] string? FriendlyName);
}

/// <summary>
/// O endereço do Home Assistant vem de quem gere a casa e é o servidor que lhe faz pedidos: só http(s), sem utilizador
/// no endereço, e nunca o próprio servidor nem endereços de ligação local (onde ficam os metadados das clouds). A rede
/// da casa (192.168.x.x, 10.x.x.x) é permitida, porque é lá que o Home Assistant costuma estar.
/// </summary>
public static class HomeAssistantUrls
{
    public static async Task<(Uri? Url, string? Error)> ValidateAsync(string? text, CancellationToken ct)
    {
        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment))
        {
            return (null, "Escreve o endereço completo do Home Assistant (por exemplo http://192.168.1.50:8123).");
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(url.Host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(url.Host, ct);
            }
            catch (SocketException)
            {
                return (null, "Não encontrei esse endereço.");
            }
        }

        if (addresses.Length == 0 || addresses.Any(IsForbidden))
        {
            return (null, "Esse endereço não pode ser usado.");
        }

        // Sempre com a barra no fim, para os caminhos da API se juntarem bem.
        var normalized = new UriBuilder(url) { Path = url.AbsolutePath.TrimEnd('/') + "/" }.Uri;
        return (normalized, null);
    }

    public static bool IsForbidden(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal || address.IsIPv6Multicast)
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            && (bytes[0] == 0 || (bytes[0] == 169 && bytes[1] == 254) || bytes[0] >= 224);
    }
}
