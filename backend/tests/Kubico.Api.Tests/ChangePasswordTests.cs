using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Kubico.Api.Tests;

/// <summary>Mudar a palavra-passe sabendo a atual (SEC-04, em parte: a recuperação por email vem depois).</summary>
public class ChangePasswordTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    private const string Old = "palavra-passe-segura";

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_ends_the_other_sessions()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (me, _) = await factory.CreateUserAsync("Mãe", email);
        var other = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Old));
        var otherAuth = (await other.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;

        var wrong = await me.PutAsJsonAsync("/api/account/password", new ChangePasswordRequest("errada-errada", "nova-palavra-passe"), TestJson.Options);
        var tooShort = await me.PutAsJsonAsync("/api/account/password", new ChangePasswordRequest(Old, "curta"), TestJson.Options);
        var changed = await me.PutAsJsonAsync("/api/account/password", new ChangePasswordRequest(Old, "nova-palavra-passe"), TestJson.Options);

        var oldLogin = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Old));
        var newLogin = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "nova-palavra-passe"));
        var otherRefresh = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(otherAuth.RefreshToken!));
        var stillHere = await me.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal("A palavra-passe não está certa.", (await wrong.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options))!.Title);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillHere.StatusCode);
    }

    [Fact]
    public async Task A_child_profile_has_no_password_to_change()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, _, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");

        var response = await child.PutAsJsonAsync("/api/account/password", new ChangePasswordRequest("4829", "nova-palavra-passe"), TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}

/// <summary>Mudar o próprio nome (UX-15).</summary>
public class ChangeNameTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task An_adult_renames_themselves_and_the_household_sees_it_but_a_child_cannot()
    {
        var (admin, adminAuth, household) = await Fase1.HouseholdAsync(factory);
        var (child, _, _) = await Fase1.ChildProfileAsync(factory, admin, household, "Rita");

        var blank = await admin.PutAsJsonAsync("/api/account/name", new ChangeNameRequest("   "), TestJson.Options);
        var renamed = await admin.PutAsJsonAsync("/api/account/name", new ChangeNameRequest("  Ana Silva "), TestJson.Options);
        var members = (await admin.GetFromJsonAsync<Kubico.Api.Households.HouseholdDto>($"/api/households/{household}", TestJson.Options))!.Members;
        var childTries = await child.PutAsJsonAsync("/api/account/name", new ChangeNameRequest("Rainha"), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Ana Silva", members.Single(m => m.UserId == adminAuth.User.Id).DisplayName);
        Assert.Equal(HttpStatusCode.Forbidden, childTries.StatusCode);
    }
}
