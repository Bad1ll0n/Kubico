using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Activity;
using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;

namespace Kubico.Api.Tests;

/// <summary>Histórico e traduções (A13, A17, S13).</summary>
public class HistoryFixesTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task The_family_template_creates_its_rewards_in_the_language_of_the_app()
    {
        var (client, _) = await factory.CreateUserAsync("Mum");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var created = await client.PostAsJsonAsync("/api/households",
            new CreateHouseholdRequest("Home", Template: HouseholdTemplate.FamilyWithKids), TestJson.Options);
        var household = (await created.Content.ReadFromJsonAsync<HouseholdSummaryDto>(TestJson.Options))!;

        var rewards = await client.GetFromJsonAsync<RewardsDto>($"/api/households/{household.Id}/rewards", TestJson.Options);

        Assert.Equal(
            ["A special outing", "Half an hour of extra screen time", "Pick Friday's film"],
            rewards!.Rewards.Select(r => r.Title).Order());
    }

    [Fact]
    public async Task Absences_requests_allowances_permissions_and_pins_appear_in_the_history()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, rita) = await Fase1.MemberAsync(factory, admin, household, "Rita", HouseholdRole.Child);
        var (brother, _) = await Fase1.MemberAsync(factory, admin, household, "Tomás", HouseholdRole.Child);
        var bruno = await ChildProfileTests.CreateChild(admin, household, "Bruno", "1234");
        var url = $"/api/households/{household}";
        var today = factory.Today;
        var chore = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Aspirar", Points: 10, AssigneeUserId: rita));
        (await Fase1.CompleteAsync(child, household, chore.Id)).EnsureSuccessStatusCode();
        var reward = await NotificationTests.Post<RewardDto>(admin, $"{url}/rewards", new SaveRewardRequest("Filme de sexta", Cost: 5));

        (await admin.PostAsJsonAsync($"{url}/absences", new SaveAbsenceRequest(rita, today.AddDays(1), today.AddDays(2)), TestJson.Options))
            .EnsureSuccessStatusCode();
        var request = await NotificationTests.Post<RedemptionDto>(child, $"{url}/rewards/{reward.Id}/redeem", null);
        (await admin.PostAsync($"{url}/redemptions/{request.Id}/approve", null)).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"{url}/allowances/{rita}", new SaveAllowanceRequest(500), TestJson.Options)).EnsureSuccessStatusCode();
        (await admin.PostAsJsonAsync($"{url}/allowances/{rita}/payments",
            new PayAllowanceRequest(Allowance.PeriodStart(today, AllowancePeriod.Weekly)), TestJson.Options)).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"{url}/permissions", new SavePermissionsRequest(
            [PermissionTests.Role(HouseholdRole.Guest, (HouseholdModule.Rewards, ModuleAccess.None))]), TestJson.Options)).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync($"{url}/children/{bruno.UserId}/pin", new ChildPinRequest("4321"), TestJson.Options)).EnsureSuccessStatusCode();

        var forAdmin = (await admin.GetFromJsonAsync<List<ActivityDto>>($"{url}/activity", TestJson.Options))!.Select(a => a.Summary).ToList();
        var forRita = (await child.GetFromJsonAsync<List<ActivityDto>>($"{url}/activity", TestJson.Options))!.Select(a => a.Summary).ToList();
        var forBrother = (await brother.GetFromJsonAsync<List<ActivityDto>>($"{url}/activity", TestJson.Options))!.Select(a => a.Summary).ToList();
        admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var english = (await admin.GetFromJsonAsync<List<ActivityDto>>($"{url}/activity", TestJson.Options))!.Select(a => a.Summary).ToList();

        Assert.Contains($"marcou a ausência de Rita de {today.AddDays(1):dd/MM} a {today.AddDays(2):dd/MM}", forAdmin);
        Assert.Contains("pediu «Filme de sexta»", forAdmin);
        Assert.Contains("aprovou «Filme de sexta» a Rita", forAdmin);
        Assert.Contains("definiu a mesada de Rita", forAdmin);
        Assert.Contains("pagou a mesada de Rita: 5,00 €", forAdmin);
        Assert.Contains("mudou as permissões dos Convidados", forAdmin);
        Assert.Contains("mudou o PIN de Bruno", forAdmin);

        // A mesada é de cada um: a Rita vê a sua, o irmão não.
        Assert.Contains("pagou a mesada de Rita: 5,00 €", forRita);
        Assert.DoesNotContain(forBrother, s => s.Contains("mesada"));
        Assert.Contains("paid Rita's allowance: €5.00", english);
        Assert.Contains("approved “Filme de sexta” for Rita", english);
    }

    [Fact]
    public async Task Whoever_stops_managing_an_area_can_no_longer_undo_what_they_did_there()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (dad, _) = await Fase1.MemberAsync(factory, admin, household, "Pai", HouseholdRole.Adult);
        var url = $"/api/households/{household}";
        await NotificationTests.Post<CalendarEventDto>(dad, $"{url}/events", new SaveEventRequest(
            "Evento do Pai", StartsAt: DateTimeOffset.Parse("2026-11-04T17:00:00Z"), EndsAt: DateTimeOffset.Parse("2026-11-04T18:00:00Z")));
        var entry = (await dad.GetFromJsonAsync<List<ActivityDto>>($"{url}/activity", TestJson.Options))!
            .Single(a => a.Summary == "criou o evento «Evento do Pai»");

        (await admin.PutAsJsonAsync($"{url}/permissions", new SavePermissionsRequest(
            [PermissionTests.Role(HouseholdRole.Adult, (HouseholdModule.Calendar, ModuleAccess.View))]), TestJson.Options)).EnsureSuccessStatusCode();
        var listed = (await dad.GetFromJsonAsync<List<ActivityDto>>($"{url}/activity", TestJson.Options))!.Single(a => a.Id == entry.Id);
        var undo = await dad.PostAsync($"{url}/activity/{entry.Id}/undo", null);
        var calendar = await admin.GetFromJsonAsync<CalendarDto>($"{url}/events?from=2026-11-04&to=2026-11-04", TestJson.Options);

        Assert.True(entry.CanUndo);
        Assert.False(listed.CanUndo);
        Assert.Equal(HttpStatusCode.Forbidden, undo.StatusCode);
        Assert.Single(calendar!.Events);
    }
}
