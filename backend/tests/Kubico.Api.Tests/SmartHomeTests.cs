using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Chores;
using Kubico.Api.Home;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>A casa inteligente (F4-16c): o Home Assistant da casa e as regras que criam tarefas.</summary>
public class SmartHomeTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    private const string Url = "http://192.168.1.50:8123";

    [Theory]
    [InlineData("http://127.0.0.1:8123", false)]
    [InlineData("http://169.254.169.254/latest", false)]
    [InlineData("http://[::1]:8123", false)]
    [InlineData("ftp://192.168.1.50", false)]
    [InlineData("http://user:pass@192.168.1.50", false)]
    [InlineData("http://192.168.1.50:8123", true)]
    [InlineData("https://10.0.0.7", true)]
    public async Task Only_http_addresses_outside_the_server_itself_are_accepted(string url, bool accepted)
    {
        var (normalized, error) = await HomeAssistantUrls.ValidateAsync(url, default);

        Assert.Equal(accepted, normalized is not null);
        Assert.Equal(accepted, error is null);
        if (accepted)
        {
            Assert.EndsWith("/", normalized!.AbsolutePath);
        }
    }

    [Fact]
    public async Task When_the_washing_machine_finishes_a_chore_is_created_once_for_the_chosen_person()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        var (dad, dadId) = await Fase1.MemberAsync(factory, mum, household, "Pai", HouseholdRole.Adult);
        var url = $"/api/households/{household}/smart-home";
        var entity = $"sensor.maquina_{Guid.NewGuid():N}";
        factory.HomeAssistant.Set(entity, "a lavar", "Máquina de lavar");

        var wrongToken = await mum.PutAsJsonAsync($"{url}/connection", new SmartHomeConnectionRequest(Url, "token-errado-123"), TestJson.Options);
        var connected = await Put<SmartHomeDto>(mum, $"{url}/connection", new SmartHomeConnectionRequest(Url, FakeHomeAssistant.Token));
        var entities = await mum.GetFromJsonAsync<List<HomeAssistantEntity>>($"{url}/entities", TestJson.Options);
        var rule = await NotificationTests.Post<SmartHomeRuleDto>(mum, $"{url}/rules",
            new SaveSmartHomeRuleRequest(entity, "terminado", "Estender a roupa", "Máquina de lavar", dadId, Points: 3));

        // A primeira leitura só regista o estado; a mudança para "terminado" cria a tarefa uma vez.
        Assert.Equal(0, await Check(household));
        factory.HomeAssistant.Set(entity, "Terminado");
        var results = await Task.WhenAll(Check(household), Check(household));
        Assert.Equal(0, await Check(household));
        factory.HomeAssistant.Set(entity, "a lavar");
        await Check(household);
        factory.HomeAssistant.Set(entity, "terminado");
        var tooSoon = await Check(household);

        var chores = await mum.GetFromJsonAsync<List<ChoreDto>>($"/api/households/{household}/chores", TestJson.Options);
        var chore = Assert.Single(chores!, c => c.Title == "Estender a roupa");
        var inbox = await NotificationTests.Inbox(dad);
        var state = await mum.GetFromJsonAsync<SmartHomeDto>(url, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, wrongToken.StatusCode);
        Assert.True(connected.Connected);
        Assert.Equal(Url + "/", connected.BaseUrl);
        Assert.Contains(entities!, e => e.EntityId == entity && e.Name == "Máquina de lavar");
        Assert.Equal(1, results.Sum());
        Assert.Equal(0, tooSoon);
        Assert.Equal((dadId, 3, factory.Today), (chore.AssigneeUserId, chore.Points, chore.DueDate));
        Assert.Contains(inbox.Items, n => n.Title == "Tarefa para ti: Estender a roupa");
        Assert.Equal("terminado", Assert.Single(state!.Rules).LastState);
        Assert.NotNull(state.Rules[0].LastTriggeredAt);
        Assert.Null(state.LastError);
        _ = rule;
    }

    [Fact]
    public async Task Only_who_manages_the_home_area_sees_or_changes_it_and_the_token_is_never_shown()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        var (guest, _) = await Fase1.MemberAsync(factory, mum, household, "Rui", HouseholdRole.Guest);
        var (stranger, _, _) = await Fase1.HouseholdAsync(factory);
        var url = $"/api/households/{household}/smart-home";
        await Put<SmartHomeDto>(mum, $"{url}/connection", new SmartHomeConnectionRequest(Url, FakeHomeAssistant.Token));

        var asGuest = await guest.GetAsync(url);
        var guestRule = await guest.PostAsJsonAsync($"{url}/rules",
            new SaveSmartHomeRuleRequest("sensor.x", "on", "Ver"), TestJson.Options);
        var asStranger = await stranger.GetAsync(url);
        var badEntity = await mum.PostAsJsonAsync($"{url}/rules", new SaveSmartHomeRuleRequest("sem ponto", "on", "Ver"), TestJson.Options);
        var body = await (await mum.GetAsync(url)).Content.ReadAsStringAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<KubicoDbContext>();
            var stored = await db.SmartHomeConnections.SingleAsync(c => c.HouseholdId == household);
            Assert.DoesNotContain(FakeHomeAssistant.Token, stored.ProtectedToken);
        }

        Assert.Equal(HttpStatusCode.Forbidden, asGuest.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, guestRule.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, asStranger.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, badEntity.StatusCode);
        Assert.DoesNotContain(FakeHomeAssistant.Token, body);

        (await mum.DeleteAsync($"{url}/connection")).EnsureSuccessStatusCode();
        var after = await mum.GetFromJsonAsync<SmartHomeDto>(url, TestJson.Options);
        var checkWithout = await mum.PostAsync($"{url}/check", null);
        Assert.False(after!.Connected);
        Assert.Equal(HttpStatusCode.Conflict, checkWithout.StatusCode);
    }

    private async Task<int> Check(Guid household)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SmartHomeService>().CheckAsync(household, default);
    }

    private static async Task<T> Put<T>(HttpClient client, string url, object body)
    {
        var response = await client.PutAsJsonAsync(url, body, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Options))!;
    }
}
