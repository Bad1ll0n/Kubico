using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Kubico.Api.Households;

namespace Kubico.Api.Tests;

public class AuthTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Register_returns_token_and_me_starts_without_households()
    {
        var (client, auth) = await factory.CreateUserAsync("Filipe");

        Assert.False(string.IsNullOrWhiteSpace(auth.AccessToken));
        Assert.Equal("Filipe", auth.User.DisplayName);

        var me = await client.GetFromJsonAsync<MeDto>("/api/me", TestJson.Options);
        Assert.Equal(auth.User.Id, me!.User.Id);
        Assert.Empty(me.Households);
    }

    [Fact]
    public async Task Register_with_existing_email_returns_conflict()
    {
        var client = factory.CreateClient();
        var request = new RegisterRequest($"{Guid.NewGuid():N}@kubico.test", "palavra-passe-segura", "Ana");

        (await client.PostAsJsonAsync("/api/auth/register", request)).EnsureSuccessStatusCode();
        var again = await client.PostAsJsonAsync("/api/auth/register", request with { Email = request.Email.ToUpperInvariant() });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task Register_with_short_password_returns_validation_error()
    {
        var client = factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest($"{Guid.NewGuid():N}@kubico.test", "curta", "Ana"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Login_with_correct_password_works_and_wrong_password_is_rejected()
    {
        var client = factory.CreateClient();
        var email = $"{Guid.NewGuid():N}@kubico.test";
        (await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email, "palavra-passe-segura", "Rui"))).EnsureSuccessStatusCode();

        var ok = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "palavra-passe-segura"));
        var wrong = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "errada-errada"));

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
    }

    [Fact]
    public async Task Protected_endpoints_require_a_token()
    {
        var response = await factory.CreateClient().GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
