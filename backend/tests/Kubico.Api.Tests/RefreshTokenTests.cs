using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kubico.Api.Auth;

namespace Kubico.Api.Tests;

public class RefreshTokenTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Refreshing_gives_a_new_pair_and_reusing_an_old_token_ends_that_session()
    {
        var (_, auth) = await factory.CreateUserAsync();
        var anonymous = factory.CreateClient();

        var refreshed = await Refresh(anonymous, auth.RefreshToken);
        var withNewToken = factory.CreateClient();
        withNewToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", refreshed!.AccessToken);
        var me = await withNewToken.GetAsync("/api/me");

        // O token antigo voltou a aparecer: a sessão deste dispositivo termina, incluindo o token novo.
        var reused = await anonymous.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(auth.RefreshToken));
        var afterTheft = await anonymous.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshed.RefreshToken));

        Assert.NotEqual(auth.RefreshToken, refreshed.RefreshToken);
        Assert.Equal(auth.User.Id, refreshed.User.Id);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterTheft.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_this_device_and_logout_all_ends_every_device()
    {
        var (phone, phoneAuth) = await factory.CreateUserAsync();
        var anonymous = factory.CreateClient();
        var login = await anonymous.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(phoneAuth.User.Email, "palavra-passe-segura"));
        var laptopAuth = (await login.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;

        var logout = await anonymous.PostAsJsonAsync("/api/auth/logout", new RefreshRequest(phoneAuth.RefreshToken));
        var phoneAfterLogout = await anonymous.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(phoneAuth.RefreshToken));
        var laptop = await Refresh(anonymous, laptopAuth.RefreshToken);

        var everywhere = await phone.PostAsync("/api/auth/logout-all", null);
        var laptopAfterAll = await anonymous.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(laptop!.RefreshToken));
        var anonymousLogoutAll = await anonymous.PostAsync("/api/auth/logout-all", null);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, phoneAfterLogout.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, everywhere.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, laptopAfterAll.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousLogoutAll.StatusCode);
    }

    [Fact]
    public async Task Unknown_refresh_tokens_are_rejected()
    {
        var response = await factory.CreateClient()
            .PostAsJsonAsync("/api/auth/refresh", new RefreshRequest("nao-existe"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Each_device_is_listed_and_can_be_signed_out_from_another()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var phone = await SignIn("/api/auth/register", new RegisterRequest(email, "palavra-passe-segura", "Ana"), "android");
        var laptop = await SignIn("/api/auth/login", new LoginRequest(email, "palavra-passe-segura"), "windows");
        var (stranger, _) = await factory.CreateUserAsync();
        var phoneClient = Authorized(phone.AccessToken);

        var sessions = (await phoneClient.GetFromJsonAsync<List<DeviceSessionDto>>("/api/auth/sessions", TestJson.Options))!;
        var laptopSession = sessions.Single(s => s.Device == "windows");
        var byStranger = await stranger.DeleteAsync($"/api/auth/sessions/{laptopSession.Id}");
        var ended = await phoneClient.DeleteAsync($"/api/auth/sessions/{laptopSession.Id}");
        var laptopRefresh = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(laptop.RefreshToken));
        var after = await phoneClient.GetFromJsonAsync<List<DeviceSessionDto>>("/api/auth/sessions", TestJson.Options);

        Assert.Equal(2, sessions.Count);
        Assert.True(sessions.Single(s => s.Device == "android").IsCurrent);
        Assert.False(laptopSession.IsCurrent);
        Assert.Equal(HttpStatusCode.NotFound, byStranger.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, laptopRefresh.StatusCode);
        Assert.Equal("android", Assert.Single(after!).Device);
    }

    private async Task<AuthResponse> SignIn(string url, object request, string device)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(request) };
        message.Headers.Add("X-Kubico-Device", device);
        var response = await factory.CreateClient().SendAsync(message);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
    }

    private HttpClient Authorized(string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    internal static async Task<AuthResponse?> Refresh(HttpClient client, string refreshToken)
    {
        var response = await client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(refreshToken));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
    }
}

/// <summary>Numa classe à parte: avança o relógio falso, o que estragaria os tokens de acesso dos outros testes.</summary>
public class RefreshTokenExpiryTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Refresh_tokens_expire_after_60_days_without_use()
    {
        var (_, auth) = await factory.CreateUserAsync();
        var anonymous = factory.CreateClient();

        factory.Clock.Advance(TimeSpan.FromDays(59));
        var stillValid = await RefreshTokenTests.Refresh(anonymous, auth.RefreshToken);
        factory.Clock.Advance(TimeSpan.FromDays(61));
        var expired = await anonymous.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(stillValid!.RefreshToken));

        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);
    }
}
