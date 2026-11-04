using System.Collections.Concurrent;
using Kubico.Api.Home;

namespace Kubico.Api.Tests;

/// <summary>O Home Assistant dos testes: aceita um só token e devolve os estados que lhe põem.</summary>
public sealed class FakeHomeAssistant : IHomeAssistantClient
{
    public const string Token = "token-de-longa-duracao-do-home-assistant";

    private readonly ConcurrentDictionary<string, HomeAssistantEntity> _states = new();

    public void Set(string entityId, string state, string? name = null) =>
        _states[entityId] = new HomeAssistantEntity(entityId, state, name);

    public Task CheckAsync(Uri baseUrl, string token, CancellationToken ct) =>
        token == Token ? Task.CompletedTask : throw new HomeAssistantException("O Home Assistant não aceitou o token.", unauthorized: true);

    public Task<IReadOnlyList<HomeAssistantEntity>> StatesAsync(Uri baseUrl, string token, CancellationToken ct) =>
        token == Token
            ? Task.FromResult<IReadOnlyList<HomeAssistantEntity>>([.. _states.Values])
            : throw new HomeAssistantException("O Home Assistant não aceitou o token.", unauthorized: true);
}
