using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Api.Shopping;
using Kubico.Domain.Chores;

namespace Kubico.Api.Tests;

/// <summary>Modelos ao criar uma casa: a casa já nasce com listas, tarefas e recompensas.</summary>
public class HouseholdTemplateTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task The_family_template_brings_routines_chores_lists_and_rewards()
    {
        var (client, _) = await factory.CreateUserAsync("Mãe");
        var response = await client.PostAsJsonAsync("/api/households",
            new CreateHouseholdRequest("Casa da Amora", Template: HouseholdTemplate.FamilyWithKids), TestJson.Options);
        var household = (await response.Content.ReadFromJsonAsync<HouseholdSummaryDto>(TestJson.Options))!;

        var lists = await client.GetFromJsonAsync<List<ShoppingListDto>>($"/api/households/{household.Id}/lists", TestJson.Options);
        var chores = await client.GetFromJsonAsync<List<ChoreDto>>($"/api/households/{household.Id}/chores", TestJson.Options);
        var rewards = await client.GetFromJsonAsync<RewardsDto>($"/api/households/{household.Id}/rewards", TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(["Farmácia", "Supermercado"], lists!.Select(l => l.Name));
        var morning = chores!.Single(c => c.Title == "Rotina da manhã");
        Assert.Equal((ChoreTimeOfDay.Morning, ChoreRecurrence.Daily, factory.Today), (morning.TimeOfDay, morning.Recurrence, morning.DueDate!.Value));
        Assert.Equal(["Lavar os dentes", "Vestir", "Fazer a cama"], morning.Steps);
        Assert.Equal(5, chores!.Count);
        Assert.Equal(3, rewards!.Rewards.Count);
    }

    [Fact]
    public async Task Without_a_template_the_house_starts_empty_and_unknown_templates_are_refused()
    {
        var (client, _) = await factory.CreateUserAsync("Pai");
        var blank = await HouseholdTests.CreateHousehold(client, "Casa nova");
        var unknown = await client.PostAsJsonAsync("/api/households", new { name = "Casa", template = 99 });
        var templates = await client.GetFromJsonAsync<List<HouseholdTemplateDto>>("/api/household-templates", TestJson.Options);

        Assert.Empty((await client.GetFromJsonAsync<List<ChoreDto>>($"/api/households/{blank.Id}/chores", TestJson.Options))!);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HouseholdTemplate.Blank, templates!.Last().Key);
        Assert.Contains(templates!, t => t.Key == HouseholdTemplate.SharedHouse && t.Name == "Casa partilhada" && t.Chores.Count > 0);
    }
}
