using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Kubico.Api.Households;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>Membros, ausências e convites (A3, A4, A7, A8, A20, S6, S19).</summary>
public class MemberFixesTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Colours_are_not_repeated_while_there_are_free_ones()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        await Fase1.MemberAsync(factory, admin, household, "Pai", HouseholdRole.Adult);
        var (_, ana) = await Fase1.MemberAsync(factory, admin, household, "Ana", HouseholdRole.Child);
        await Fase1.MemberAsync(factory, admin, household, "Bruno", HouseholdRole.Child);
        (await admin.DeleteAsync($"/api/households/{household}/members/{ana}")).EnsureSuccessStatusCode();

        await ChildProfileTests.CreateChild(admin, household, "Carla", "1234");
        var members = await MemberTests.Members(admin, household);

        Assert.Equal(4, members.Count);
        Assert.Equal(members.Count, members.Select(m => m.Color).Distinct().Count());
    }

    [Fact]
    public async Task Absences_follow_the_chores_permission_and_children_do_not_set_their_own()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (dad, dadId) = await Fase1.MemberAsync(factory, admin, household, "Pai", HouseholdRole.Adult);
        var (child, rita) = await Fase1.MemberAsync(factory, admin, household, "Rita", HouseholdRole.Child);
        var url = $"/api/households/{household}/absences";
        var today = factory.Today;

        var childOwn = await child.PostAsJsonAsync(url, new SaveAbsenceRequest(rita, today, today.AddDays(60), "não me apetece"), TestJson.Options);
        var dadForChild = await dad.PostAsJsonAsync(url, new SaveAbsenceRequest(rita, today, today.AddDays(1)), TestJson.Options);
        var created = (await dadForChild.Content.ReadFromJsonAsync<AbsenceDto>(TestJson.Options))!;
        var childCancels = await child.DeleteAsync($"{url}/{created.Id}");
        (await admin.PutAsJsonAsync($"/api/households/{household}/permissions", new SavePermissionsRequest(
            [PermissionTests.Role(HouseholdRole.Adult, (HouseholdModule.Chores, ModuleAccess.View))]), TestJson.Options)).EnsureSuccessStatusCode();
        var viewerForChild = await dad.PostAsJsonAsync(url, new SaveAbsenceRequest(rita, today.AddDays(5), today.AddDays(6)), TestJson.Options);
        var viewerOwn = await dad.PostAsJsonAsync(url, new SaveAbsenceRequest(dadId, today.AddDays(5), today.AddDays(6)), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, childOwn.StatusCode);
        Assert.Equal(HttpStatusCode.Created, dadForChild.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, childCancels.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, viewerForChild.StatusCode);
        Assert.Equal(HttpStatusCode.Created, viewerOwn.StatusCode);
    }

    [Fact]
    public async Task Invites_are_listed_revoked_used_once_by_default_and_never_for_admin()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var url = $"/api/households/{household}/invites";
        var adultInvite = await HouseholdTests.CreateInvite(admin, household, HouseholdRole.Adult);
        var guestInvite = await HouseholdTests.CreateInvite(admin, household, HouseholdRole.Guest);
        var asAdmin = await admin.PostAsJsonAsync(url, new CreateInviteRequest(HouseholdRole.Admin), TestJson.Options);
        var tooMany = await admin.PostAsJsonAsync(url, new CreateInviteRequest(HouseholdRole.Guest, MaxUses: 51), TestJson.Options);
        var (first, _) = await factory.CreateUserAsync("Primeiro");
        var (second, _) = await factory.CreateUserAsync("Segundo");
        var (third, _) = await factory.CreateUserAsync("Terceiro");

        var firstJoins = await first.PostAsync($"/api/invites/{adultInvite.Code}/accept", null);
        var firstAgain = await first.PostAsync($"/api/invites/{adultInvite.Code}/accept", null);
        var secondRefused = await second.PostAsync($"/api/invites/{adultInvite.Code}/accept", null);
        var preview = await second.GetAsync($"/api/invites/{adultInvite.Code}");
        var active = await admin.GetFromJsonAsync<List<InviteDto>>(url, TestJson.Options);
        var revoked = await admin.DeleteAsync($"{url}/{guestInvite.Code}");
        var thirdRefused = await third.PostAsync($"/api/invites/{guestInvite.Code}/accept", null);
        var activeAfter = await admin.GetFromJsonAsync<List<InviteDto>>(url, TestJson.Options);

        Assert.Equal((1, (int?)null), (adultInvite.MaxUses, guestInvite.MaxUses));
        Assert.Equal(HttpStatusCode.BadRequest, asAdmin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstJoins.StatusCode);
        Assert.Equal(HttpStatusCode.OK, firstAgain.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, secondRefused.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
        Assert.Equal(guestInvite.Code, Assert.Single(active!).Code);
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, thirdRefused.StatusCode);
        Assert.Empty(activeAfter!);
    }

    [Fact]
    public async Task Deleting_the_account_hands_the_house_to_an_adult_or_asks_to_do_it_first()
    {
        var (admin, adminAuth, household) = await Fase1.HouseholdAsync(factory, "Casa da Mãe");
        var (_, guest) = await Fase1.MemberAsync(factory, admin, household, "Babysitter", HouseholdRole.Guest);
        var other = await HouseholdTests.CreateHousehold(admin, "Casa de férias");
        await Fase1.MemberAsync(factory, admin, other.Id, "Pai", HouseholdRole.Adult);
        await Fase1.MemberAsync(factory, admin, other.Id, "Avó", HouseholdRole.Guest);

        var preview = await admin.GetFromJsonAsync<List<AccountDeletionHouseholdDto>>("/api/account/deletion", TestJson.Options);
        var blocked = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest("palavra-passe-segura")),
        });
        var stillThere = await Fase1.HouseholdDetailsAsync(admin, other.Id);

        (await admin.PutAsJsonAsync($"/api/households/{household}/members/{guest}/role",
            new UpdateMemberRoleRequest(HouseholdRole.Adult), TestJson.Options)).EnsureSuccessStatusCode();
        var deleted = await admin.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest("palavra-passe-segura")),
        });

        Assert.Equal("Blocked", preview!.Single(h => h.HouseholdId == household).Outcome);
        Assert.Equal(("AdminPassesTo", "Pai"), (preview!.Single(h => h.HouseholdId == other.Id).Outcome,
            preview!.Single(h => h.HouseholdId == other.Id).SuccessorName));
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("Antes de apagares a conta, passa a administração de «Casa da Mãe» a um adulto.", (await Fase1.ProblemAsync(blocked)).Title);
        Assert.Contains(stillThere.Members, m => m.UserId == adminAuth.User.Id && m.Role == HouseholdRole.Admin);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task When_the_house_is_deleted_the_other_devices_are_told()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory, "Casa a apagar");
        var (_, _, childAuth) = await Fase1.ChildProfileAsync(factory, admin, household, "Bruno");
        await using var connection = await ConnectAsync(childAuth.AccessToken);
        var deleted = new TaskCompletionSource<(Guid, string)>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<Guid, string>(HouseholdEvents.HouseholdDeleted, (id, name) => deleted.TrySetResult((id, name)));
        await connection.InvokeAsync("JoinHousehold", household);

        (await admin.PostAsync($"/api/households/{household}/leave", null)).EnsureSuccessStatusCode();

        Assert.Equal((household, "Casa a apagar"), await deleted.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task Changing_the_pin_or_renewing_the_family_code_signs_the_child_out_everywhere()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, bruno, auth) = await Fase1.ChildProfileAsync(factory, admin, household, "Bruno");
        var (_, carla, carlaAuth) = await Fase1.ChildProfileAsync(factory, admin, household, "Carla");
        await using var connection = await ConnectAsync(auth.AccessToken);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };

        (await admin.PutAsJsonAsync($"/api/households/{household}/children/{bruno}/pin", new ChildPinRequest("5678"), TestJson.Options))
            .EnsureSuccessStatusCode();
        var refresh = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken));
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var carlaBefore = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(carlaAuth.RefreshToken));
        var carlaRenewed = (await carlaBefore.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
        (await admin.DeleteAsync($"/api/households/{household}/family-code")).EnsureSuccessStatusCode();
        var carlaAfter = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(carlaRenewed.RefreshToken));
        var byChild = await child.PostAsync($"/api/households/{household}/children/{carla}/sign-out", null);

        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, carlaBefore.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, carlaAfter.StatusCode);

        // O token de acesso do Bruno ainda é válido, mas a sessão terminou: as ações sensíveis já não passam (S18).
        Assert.Equal(HttpStatusCode.Unauthorized, byChild.StatusCode);
    }

    [Fact]
    public async Task An_adult_can_sign_a_child_profile_out_of_every_device()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (_, bruno, auth) = await Fase1.ChildProfileAsync(factory, admin, household, "Bruno");
        var (other, _) = await Fase1.MemberAsync(factory, admin, household, "Rita", HouseholdRole.Child);

        var byOtherChild = await other.PostAsync($"/api/households/{household}/children/{bruno}/sign-out", null);
        var byAdult = await admin.PostAsync($"/api/households/{household}/children/{bruno}/sign-out", null);
        var refresh = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken));

        Assert.Equal(HttpStatusCode.Forbidden, byOtherChild.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, byAdult.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task Only_who_manages_profiles_sees_the_childrens_birth_dates()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (guest, _) = await Fase1.MemberAsync(factory, admin, household, "Babysitter", HouseholdRole.Guest);
        var rita = await admin.PostAsJsonAsync($"/api/households/{household}/children",
            new CreateChildProfileRequest("Rita", "1234", factory.Today.AddYears(-7)), TestJson.Options);
        var ritaId = (await rita.Content.ReadFromJsonAsync<MemberDto>(TestJson.Options))!.UserId;

        var forAdmin = await Fase1.HouseholdDetailsAsync(admin, household);
        var forGuest = await Fase1.HouseholdDetailsAsync(guest, household);

        Assert.Equal(factory.Today.AddYears(-7), forAdmin.Members.Single(m => m.UserId == ritaId).BirthDate);
        Assert.Null(forGuest.Members.Single(m => m.UserId == ritaId).BirthDate);
    }

    private async Task<HubConnection> ConnectAsync(string accessToken)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HouseholdHub.Path), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = TestJson.Options)
            .Build();
        await connection.StartAsync();
        return connection;
    }
}
