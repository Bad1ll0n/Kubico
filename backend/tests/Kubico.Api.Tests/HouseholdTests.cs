using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Households;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

public class HouseholdTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Creator_becomes_admin_of_the_new_household()
    {
        var (client, _) = await factory.CreateUserAsync();

        var created = await CreateHousehold(client, "Casa da Amora");
        var me = await client.GetFromJsonAsync<MeDto>("/api/me", TestJson.Options);

        var summary = Assert.Single(me!.Households);
        Assert.Equal(created.Id, summary.Id);
        Assert.Equal(HouseholdRole.Admin, summary.Role);
    }

    [Fact]
    public async Task Invited_user_joins_with_the_invite_role()
    {
        var (admin, _) = await factory.CreateUserAsync("Filipe");
        var (guest, guestAuth) = await factory.CreateUserAsync("Ana");
        var household = await CreateHousehold(admin, "Casa");

        var invite = await CreateInvite(admin, household.Id, HouseholdRole.Child);
        var accept = await guest.PostAsync($"/api/invites/{invite.Code.ToLowerInvariant()}/accept", null);
        accept.EnsureSuccessStatusCode();

        var details = await admin.GetFromJsonAsync<HouseholdDto>($"/api/households/{household.Id}", TestJson.Options);
        Assert.Equal(2, details!.Members.Count);
        var member = Assert.Single(details.Members, m => m.UserId == guestAuth.User.Id);
        Assert.Equal(HouseholdRole.Child, member.Role);
        Assert.NotEqual(details.Members[0].Color, details.Members[1].Color);
    }

    [Fact]
    public async Task Accepting_the_same_invite_twice_does_not_duplicate_membership()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (other, _) = await factory.CreateUserAsync();
        var household = await CreateHousehold(admin, "Casa");
        var invite = await CreateInvite(admin, household.Id, HouseholdRole.Adult);

        (await other.PostAsync($"/api/invites/{invite.Code}/accept", null)).EnsureSuccessStatusCode();
        (await other.PostAsync($"/api/invites/{invite.Code}/accept", null)).EnsureSuccessStatusCode();

        var details = await admin.GetFromJsonAsync<HouseholdDto>($"/api/households/{household.Id}", TestJson.Options);
        Assert.Equal(2, details!.Members.Count);
    }

    [Fact]
    public async Task Unknown_invite_code_returns_not_found()
    {
        var (client, _) = await factory.CreateUserAsync();

        var response = await client.PostAsync("/api/invites/NOPE2345/accept", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Non_members_cannot_see_a_household()
    {
        var (owner, _) = await factory.CreateUserAsync();
        var (stranger, _) = await factory.CreateUserAsync();
        var household = await CreateHousehold(owner, "Privada");

        var response = await stranger.GetAsync($"/api/households/{household.Id}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Only_admins_can_create_invites()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (adult, _) = await factory.CreateUserAsync();
        var household = await CreateHousehold(admin, "Casa");
        var invite = await CreateInvite(admin, household.Id, HouseholdRole.Adult);
        (await adult.PostAsync($"/api/invites/{invite.Code}/accept", null)).EnsureSuccessStatusCode();

        var response = await adult.PostAsJsonAsync(
            $"/api/households/{household.Id}/invites", new CreateInviteRequest(HouseholdRole.Adult), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    internal static async Task<HouseholdSummaryDto> CreateHousehold(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/households", new CreateHouseholdRequest(name));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<HouseholdSummaryDto>(TestJson.Options))!;
    }

    internal static async Task<InviteDto> CreateInvite(HttpClient admin, Guid householdId, HouseholdRole role)
    {
        var response = await admin.PostAsJsonAsync(
            $"/api/households/{householdId}/invites", new CreateInviteRequest(role), TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<InviteDto>(TestJson.Options))!;
    }
}
