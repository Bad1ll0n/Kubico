using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Mvc;

namespace Kubico.Api.Tests;

/// <summary>Ajudas dos testes da fase 1 (correções da revisão): casas, crianças, pedidos em paralelo e saldos.</summary>
internal static class Fase1
{
    /// <summary>Uma casa com a administradora (Mãe).</summary>
    public static async Task<(HttpClient Admin, AuthResponse Auth, Guid HouseholdId)> HouseholdAsync(KubicoApiFactory factory, string? name = null)
    {
        var (admin, auth) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(admin, name ?? $"Casa {Guid.NewGuid():N}"[..13]);
        return (admin, auth, household.Id);
    }

    /// <summary>Uma pessoa com conta que entra na casa com este papel.</summary>
    public static async Task<(HttpClient Client, Guid UserId)> MemberAsync(
        KubicoApiFactory factory, HttpClient admin, Guid householdId, string name, HouseholdRole role)
    {
        var (client, auth) = await factory.CreateUserAsync(name);
        await NotificationTests.Join(admin, client, householdId, role);
        return (client, auth.User.Id);
    }

    /// <summary>Um perfil de criança (PIN 1234) e a sua sessão.</summary>
    public static async Task<(HttpClient Client, Guid UserId, AuthResponse Auth)> ChildProfileAsync(
        KubicoApiFactory factory, HttpClient admin, Guid householdId, string name, string pin = "1234")
    {
        var child = await ChildProfileTests.CreateChild(admin, householdId, name, pin);
        var code = await ChildProfileTests.FamilyCode(admin, householdId);
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, child.UserId, pin));
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
        return (Authorized(factory, auth.AccessToken), child.UserId, auth);
    }

    public static HttpClient Authorized(KubicoApiFactory factory, string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>Dispara <paramref name="count"/> pedidos ao mesmo tempo e devolve os estados.</summary>
    public static async Task<List<HttpStatusCode>> ParallelAsync(int count, Func<int, Task<HttpResponseMessage>> send)
    {
        using var start = new SemaphoreSlim(0);
        var tasks = Enumerable.Range(0, count).Select(async i =>
        {
            await start.WaitAsync();
            using var response = await send(i);
            return response.StatusCode;
        }).ToList();
        start.Release(count);
        return [.. await Task.WhenAll(tasks)];
    }

    public static async Task<ChoreDto> ChoreAsync(HttpClient admin, Guid householdId, SaveChoreRequest request) =>
        await NotificationTests.Post<ChoreDto>(admin, $"/api/households/{householdId}/chores", request);

    public static Task<HttpResponseMessage> CompleteAsync(HttpClient client, Guid householdId, Guid choreId, CompleteChoreRequest? request = null) =>
        request is null
            ? client.PostAsync($"/api/households/{householdId}/chores/{choreId}/complete", null)
            : client.PostAsJsonAsync($"/api/households/{householdId}/chores/{choreId}/complete", request, TestJson.Options);

    /// <summary>O saldo de pontos de uma pessoa.</summary>
    public static async Task<MemberPointsDto> PointsAsync(HttpClient client, Guid householdId, Guid userId) =>
        (await client.GetFromJsonAsync<RewardsDto>($"/api/households/{householdId}/rewards", TestJson.Options))!
            .Points.Single(p => p.UserId == userId);

    public static async Task<ProblemDetails> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options))!;

    public static async Task<HouseholdDto> HouseholdDetailsAsync(HttpClient client, Guid householdId) =>
        (await client.GetFromJsonAsync<HouseholdDto>($"/api/households/{householdId}", TestJson.Options))!;
}
