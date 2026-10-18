using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Realtime;
using Kubico.Api.Rewards;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>Mudar papéis, tirar membros, sair da casa e passar a administração.</summary>
public class MemberTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Only_admins_change_roles_and_the_house_keeps_at_least_one_admin()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (partner, partnerAuth) = await factory.CreateUserAsync("Pai");
        var (stranger, _) = await factory.CreateUserAsync("Vizinho");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Guest);

        var promoted = await admin.PutAsJsonAsync(RoleUrl(household.Id, partnerAuth.User.Id), new UpdateMemberRoleRequest(HouseholdRole.Adult), TestJson.Options);
        var byNonAdmin = await partner.PutAsJsonAsync(RoleUrl(household.Id, adminAuth.User.Id), new UpdateMemberRoleRequest(HouseholdRole.Guest), TestJson.Options);
        var byStranger = await stranger.PutAsJsonAsync(RoleUrl(household.Id, partnerAuth.User.Id), new UpdateMemberRoleRequest(HouseholdRole.Admin), TestJson.Options);
        var lastAdmin = await admin.PutAsJsonAsync(RoleUrl(household.Id, adminAuth.User.Id), new UpdateMemberRoleRequest(HouseholdRole.Adult), TestJson.Options);

        Assert.Equal(HouseholdRole.Adult, (await promoted.Content.ReadFromJsonAsync<MemberDto>(TestJson.Options))!.Role);
        Assert.Equal(HttpStatusCode.Forbidden, byNonAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, lastAdmin.StatusCode);
        Assert.Equal(HouseholdRole.Adult, (await Members(admin, household.Id)).Single(m => m.UserId == partnerAuth.User.Id).Role);
    }

    [Fact]
    public async Task Removing_someone_takes_them_off_chores_events_and_pending_rewards_but_keeps_the_history()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var (partner, partnerAuth) = await factory.CreateUserAsync("Pai");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Adult);
        var (me, rita, dad) = (adminAuth.User.Id, childAuth.User.Id, partnerAuth.User.Id);

        // A Rita ganha 10 pontos e pede uma recompensa; tem o lixo esta semana e vai à natação com o pai a levar.
        var dishes = await NotificationTests.Post<ChoreDto>(admin, $"/api/households/{household.Id}/chores",
            new SaveChoreRequest("Pôr a mesa", Points: 10, AssigneeUserId: rita));
        await NotificationTests.Post<ChoreCompletedDto>(child, $"/api/households/{household.Id}/chores/{dishes.Id}/complete", null);
        var trash = await NotificationTests.Post<ChoreDto>(admin, $"/api/households/{household.Id}/chores", new SaveChoreRequest(
            "Lixo", DueDate: factory.Today, Recurrence: ChoreRecurrence.Weekly, AssigneeUserId: rita, RotationUserIds: [me, rita, dad]));
        var swim = await NotificationTests.Post<CalendarEventDto>(admin, $"/api/households/{household.Id}/events", new SaveEventRequest(
            "Natação", StartsAt: DateTimeOffset.Parse("2026-11-10T18:00:00Z"), EndsAt: DateTimeOffset.Parse("2026-11-10T19:00:00Z"),
            ParticipantUserIds: [rita, me], DropOffUserId: dad, PickUpUserId: rita));
        var reward = await NotificationTests.Post<RewardDto>(admin, $"/api/households/{household.Id}/rewards", new SaveRewardRequest("Cinema", Cost: 10));
        await NotificationTests.Post<RedemptionDto>(child, $"/api/households/{household.Id}/rewards/{reward.Id}/redeem", null);

        var removed = await admin.DeleteAsync($"/api/households/{household.Id}/members/{rita}");
        var removeSelf = await admin.DeleteAsync($"/api/households/{household.Id}/members/{me}");
        var byAdult = await partner.DeleteAsync($"/api/households/{household.Id}/members/{me}");

        var chores = await admin.GetFromJsonAsync<List<ChoreDto>>($"/api/households/{household.Id}/chores", TestJson.Options);
        var calendar = await admin.GetFromJsonAsync<CalendarDto>($"/api/households/{household.Id}/events?from=2026-11-10&to=2026-11-10", TestJson.Options);
        var rewards = await admin.GetFromJsonAsync<RewardsDto>($"/api/households/{household.Id}/rewards", TestJson.Options);
        var childView = await child.GetAsync($"/api/households/{household.Id}");
        var childMe = await child.GetFromJsonAsync<MeDto>("/api/me", TestJson.Options);

        var trashNow = chores!.Single(c => c.Id == trash.Id);
        var swimNow = calendar!.Events.Single(e => e.Id == swim.Id);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, removeSelf.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, byAdult.StatusCode);
        Assert.Equal(new[] { me, dad }, trashNow.RotationUserIds);
        Assert.Equal(dad, trashNow.AssigneeUserId);
        Assert.Equal(new[] { me }, swimNow.ParticipantUserIds);
        Assert.Equal(dad, swimNow.DropOffUserId);
        Assert.Null(swimNow.PickUpUserId);
        Assert.Empty(rewards!.Redemptions);
        Assert.DoesNotContain(await Members(admin, household.Id), m => m.UserId == rita);
        Assert.Equal(HttpStatusCode.NotFound, childView.StatusCode);
        Assert.Empty(childMe!.Households);
    }

    [Fact]
    public async Task The_last_admin_hands_over_before_leaving_and_the_last_member_takes_the_house_along()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (partner, partnerAuth) = await factory.CreateUserAsync("Pai");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Adult);

        var tooEarly = await admin.PostAsync($"/api/households/{household.Id}/leave", null);
        var toStranger = await admin.PostAsJsonAsync($"/api/households/{household.Id}/transfer-admin", new TransferAdminRequest(Guid.NewGuid()), TestJson.Options);
        var handOver = await admin.PostAsJsonAsync($"/api/households/{household.Id}/transfer-admin", new TransferAdminRequest(partnerAuth.User.Id), TestJson.Options);
        var roles = (await Members(partner, household.Id)).ToDictionary(m => m.UserId, m => m.Role);
        var left = await admin.PostAsync($"/api/households/{household.Id}/leave", null);
        var membersAfter = await Members(partner, household.Id);
        var lastOneLeaves = await partner.PostAsync($"/api/households/{household.Id}/leave", null);
        var gone = await partner.GetAsync($"/api/households/{household.Id}");

        Assert.Equal(HttpStatusCode.Conflict, tooEarly.StatusCode);
        Assert.Equal("Antes de saíres, passa a administração da casa a outra pessoa.", (await tooEarly.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title);
        Assert.Equal(HttpStatusCode.NotFound, toStranger.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, handOver.StatusCode);
        Assert.Equal((HouseholdRole.Adult, HouseholdRole.Admin), (roles[adminAuth.User.Id], roles[partnerAuth.User.Id]));
        Assert.Equal(HttpStatusCode.NoContent, left.StatusCode);
        Assert.Equal(partnerAuth.User.Id, Assert.Single(membersAfter).UserId);
        Assert.Equal(HttpStatusCode.NoContent, lastOneLeaves.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task Members_see_in_real_time_who_joins_and_who_leaves()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (partner, partnerAuth) = await factory.CreateUserAsync("Pai");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HouseholdHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(adminAuth.AccessToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = TestJson.Options)
            .Build();
        var joined = new TaskCompletionSource<MemberDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var left = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<Guid, MemberDto>(HouseholdEvents.MemberUpserted, (_, member) => joined.TrySetResult(member));
        connection.On<Guid, Guid>(HouseholdEvents.MemberDeleted, (_, userId) => left.TrySetResult(userId));
        await connection.StartAsync();
        await connection.InvokeAsync("JoinHousehold", household.Id);

        await NotificationTests.Join(admin, partner, household.Id, HouseholdRole.Adult);
        var member = await joined.Task.WaitAsync(TimeSpan.FromSeconds(10));
        (await partner.PostAsync($"/api/households/{household.Id}/leave", null)).EnsureSuccessStatusCode();

        Assert.Equal(("Pai", HouseholdRole.Adult), (member.DisplayName, member.Role));
        Assert.Equal(partnerAuth.User.Id, await left.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private static string RoleUrl(Guid householdId, Guid userId) => $"/api/households/{householdId}/members/{userId}/role";

    internal static async Task<IReadOnlyList<MemberDto>> Members(HttpClient client, Guid householdId) =>
        (await client.GetFromJsonAsync<HouseholdDto>($"/api/households/{householdId}", TestJson.Options))!.Members;
}

/// <summary>Perfis de criança: criados por um adulto, entram com o código da família e um PIN.</summary>
public class ChildProfileTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task A_child_signs_in_with_the_family_code_and_pin_and_stays_in_their_house()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var rita = await CreateChild(admin, household.Id, "Rita", "1234");
        var code = await FamilyCode(admin, household.Id);
        var anonymous = factory.CreateClient();

        var profiles = await anonymous.PostAsJsonAsync("/api/auth/child/profiles", new FamilyCodeRequest(code.ToLowerInvariant()[..4] + "-" + code[4..]));
        var badCode = await anonymous.PostAsJsonAsync("/api/auth/child/profiles", new FamilyCodeRequest("NAOEXISTE"));
        var login = await anonymous.PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, rita.UserId, "1234"));
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
        var child = factory.CreateClient();
        child.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);

        var me = await child.GetFromJsonAsync<MeDto>("/api/me", TestJson.Options);
        var createHouse = await child.PostAsJsonAsync("/api/households", new CreateHouseholdRequest("Casa da Rita"));
        var invite = await HouseholdTests.CreateInvite(admin, household.Id, HouseholdRole.Adult);
        var joinElsewhere = await child.PostAsync($"/api/invites/{invite.Code}/accept", null);
        var leave = await child.PostAsync($"/api/households/{household.Id}/leave", null);
        var promote = await admin.PutAsJsonAsync($"/api/households/{household.Id}/members/{rita.UserId}/role", new UpdateMemberRoleRequest(HouseholdRole.Adult), TestJson.Options);

        Assert.Equal((HouseholdRole.Child, true), (rita.Role, rita.IsChildProfile));
        Assert.Equal("Rita", Assert.Single((await profiles.Content.ReadFromJsonAsync<List<ChildProfileOptionDto>>(TestJson.Options))!).DisplayName);
        Assert.Equal(HttpStatusCode.NotFound, badCode.StatusCode);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Null(auth.User.Email);
        Assert.True(auth.User.IsChildProfile);
        Assert.Equal((household.Id, HouseholdRole.Child), (Assert.Single(me!.Households).Id, me.Households[0].Role));
        Assert.Equal(HttpStatusCode.Forbidden, createHouse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, joinElsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, leave.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, promote.StatusCode);
    }

    [Fact]
    public async Task Five_wrong_pins_lock_the_profile_until_an_adult_resets_it_or_time_passes()
    {
        var (admin, _) = await factory.CreateUserAsync("Pai");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var tiago = await CreateChild(admin, household.Id, "Tiago", "4321");
        var code = await FamilyCode(admin, household.Id);
        var anonymous = factory.CreateClient();
        Task<HttpResponseMessage> Try(string pin) =>
            anonymous.PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, tiago.UserId, pin));

        var wrong = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            wrong.Add((await Try("0000")).StatusCode);
        }

        var lockedEvenWithRightPin = await Try("4321");
        var lockedMessage = (await lockedEvenWithRightPin.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>())!.Title;
        var reset = await admin.PutAsJsonAsync($"/api/households/{household.Id}/children/{tiago.UserId}/pin", new ChildPinRequest("2468"), TestJson.Options);
        var afterReset = await Try("2468");
        for (var i = 0; i < 5; i++)
        {
            await Try("0000");
        }

        factory.Clock.Advance(TimeSpan.FromMinutes(16));
        var afterWaiting = await Try("2468");
        var shortPin = await admin.PutAsJsonAsync($"/api/households/{household.Id}/children/{tiago.UserId}/pin", new ChildPinRequest("12a"), TestJson.Options);

        Assert.Equal(
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests },
            wrong);
        Assert.Equal(HttpStatusCode.TooManyRequests, lockedEvenWithRightPin.StatusCode);
        Assert.Equal("Demasiados PINs errados. Tenta outra vez daqui a 15 minutos.", lockedMessage);
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterReset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterWaiting.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, shortPin.StatusCode);
    }

    [Fact]
    public async Task Only_adults_manage_profiles_and_codes_and_each_code_only_opens_its_own_house()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var (guest, _) = await factory.CreateUserAsync("Babysitter");
        var (otherAdmin, _) = await factory.CreateUserAsync("Vizinha");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var other = await HouseholdTests.CreateHousehold(otherAdmin, "Outra casa");
        await NotificationTests.Join(admin, guest, household.Id, HouseholdRole.Guest);
        var rita = await CreateChild(admin, household.Id, "Rita", "1234");
        var neighbourKid = await CreateChild(otherAdmin, other.Id, "Duarte", "1234");
        var code = await FamilyCode(admin, household.Id);
        var anonymous = factory.CreateClient();

        var guestCreates = await guest.PostAsJsonAsync($"/api/households/{household.Id}/children", new CreateChildProfileRequest("Zé", "1111"), TestJson.Options);
        var guestCode = await guest.PostAsync($"/api/households/{household.Id}/family-code", null);
        var otherHouseKid = await anonymous.PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, neighbourKid.UserId, "1234"));
        var renamed = await admin.PutAsJsonAsync($"/api/households/{household.Id}/children/{rita.UserId}",
            new UpdateChildProfileRequest("Rita Maria", new DateOnly(2016, 5, 3)), TestJson.Options);
        var renamedDto = (await renamed.Content.ReadFromJsonAsync<MemberDto>(TestJson.Options))!;
        (await admin.DeleteAsync($"/api/households/{household.Id}/family-code")).EnsureSuccessStatusCode();
        var oldCode = await anonymous.PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, rita.UserId, "1234"));
        var newCode = await FamilyCode(admin, household.Id);
        (await admin.DeleteAsync($"/api/households/{household.Id}/members/{rita.UserId}")).EnsureSuccessStatusCode();
        var removedProfile = await anonymous.PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(newCode, rita.UserId, "1234"));

        Assert.Equal(HttpStatusCode.Forbidden, guestCreates.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, guestCode.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherHouseKid.StatusCode);
        Assert.Equal(("Rita Maria", new DateOnly(2016, 5, 3)), (renamedDto.DisplayName, renamedDto.BirthDate!.Value));
        Assert.Equal(HttpStatusCode.NotFound, oldCode.StatusCode);
        Assert.NotEqual(code, newCode);
        Assert.Equal(HttpStatusCode.NotFound, removedProfile.StatusCode);
    }

    private static async Task<MemberDto> CreateChild(HttpClient adult, Guid householdId, string name, string pin)
    {
        var response = await adult.PostAsJsonAsync($"/api/households/{householdId}/children", new CreateChildProfileRequest(name, pin), TestJson.Options);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MemberDto>(TestJson.Options))!;
    }

    private static async Task<string> FamilyCode(HttpClient adult, Guid householdId) =>
        (await NotificationTests.Post<FamilyCodeDto>(adult, $"/api/households/{householdId}/family-code", null)).Code;
}
