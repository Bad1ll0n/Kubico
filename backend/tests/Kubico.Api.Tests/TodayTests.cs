using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kubico.Api.Households;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>O ecrã Hoje num só pedido (F4-19), com as permissões de cada área.</summary>
public class TodayTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task One_request_brings_every_area_the_person_can_see()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, _, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");
        (await admin.PutAsJsonAsync($"/api/households/{household}/permissions", new SavePermissionsRequest(
            [PermissionTests.Role(HouseholdRole.Child, (HouseholdModule.Rewards, ModuleAccess.None))]), TestJson.Options))
            .EnsureSuccessStatusCode();

        var adultToday = await admin.GetFromJsonAsync<JsonElement>($"/api/households/{household}/today", TestJson.Options);
        var childToday = await child.GetFromJsonAsync<JsonElement>($"/api/households/{household}/today", TestJson.Options);

        foreach (var part in new[] { "household", "absences", "chores", "calendar", "lists", "rewards" })
        {
            Assert.NotEqual(JsonValueKind.Null, adultToday.GetProperty(part).ValueKind);
        }
        Assert.Equal(household, adultToday.GetProperty("household").GetProperty("id").GetGuid());
        Assert.Equal(JsonValueKind.Array, adultToday.GetProperty("calendar").GetProperty("occurrences").ValueKind);
        Assert.Equal(JsonValueKind.Null, childToday.GetProperty("rewards").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, childToday.GetProperty("chores").ValueKind);
    }

    [Fact]
    public async Task Someone_from_another_household_gets_404()
    {
        var (_, _, household) = await Fase1.HouseholdAsync(factory);
        var (stranger, _, _) = await Fase1.HouseholdAsync(factory);

        var response = await stranger.GetAsync($"/api/households/{household}/today");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
