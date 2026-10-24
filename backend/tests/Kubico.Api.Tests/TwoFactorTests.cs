using System.Net;
using System.Net.Http.Json;
using System.Text;
using Kubico.Api.Auth;
using Kubico.Domain.Users;
using Microsoft.AspNetCore.Mvc;

namespace Kubico.Api.Tests;

/// <summary>Verificação em dois passos: ligar com a app de autenticação, entrar com o código, desligar.</summary>
public class TwoFactorTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    private const string Password = "palavra-passe-segura";

    [Fact]
    public void Codes_follow_the_rfc_6238_example_and_base32_round_trips()
    {
        // RFC 6238, apêndice B: segredo "12345678901234567890", aos 59 segundos o código de 8 algarismos é 94287082.
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");

        Assert.Equal("287082", Totp.Code(secret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(59))));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", Totp.ToBase32(secret));
        Assert.Equal(secret, Totp.FromBase32("gezdgnbvgy3tqojq gezdgnbvgy3tqojq"));
    }

    [Fact]
    public async Task With_two_step_verification_on_the_password_alone_is_not_enough_to_sign_in()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (client, _) = await factory.CreateUserAsync("Mãe", email);
        var setup = await NotificationTests.Post<TwoFactorSetupDto>(client, "/api/account/2fa/setup", new TwoFactorSetupRequest(Password));
        var secret = Totp.FromBase32(setup.Secret);
        var step = Totp.StepAt(factory.Clock.GetUtcNow());

        var wrong = await client.PostAsJsonAsync("/api/account/2fa/enable", new TwoFactorCodeRequest("000000"), TestJson.Options);
        var enabled = await NotificationTests.Post<TwoFactorEnabledDto>(client, "/api/account/2fa/enable", new TwoFactorCodeRequest(Totp.Code(secret, step)));
        var status = await client.GetFromJsonAsync<TwoFactorStatusDto>("/api/account/2fa", TestJson.Options);

        var challenge = await Login(email);
        var replay = await SecondStep(challenge.ChallengeToken, Totp.Code(secret, step));
        var signedIn = await SecondStep(challenge.ChallengeToken, Totp.Code(secret, step + 1));
        var auth = await signedIn.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        var withRecovery = await SecondStep((await Login(email)).ChallengeToken, enabled.RecoveryCodes[0].ToLowerInvariant().Replace("-", ""));
        var recoveryAgain = await SecondStep((await Login(email)).ChallengeToken, enabled.RecoveryCodes[0]);
        var forged = await SecondStep("isto-nao-e-um-bilhete", Totp.Code(secret, step + 1));
        var statusAfter = await client.GetFromJsonAsync<TwoFactorStatusDto>("/api/account/2fa", TestJson.Options);

        Assert.StartsWith("otpauth://totp/Kubico:", setup.Uri);
        Assert.Contains("issuer=Kubico", setup.Uri);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(8, enabled.RecoveryCodes.Count);
        Assert.Equal(new TwoFactorStatusDto(true, 8), status);
        Assert.True(challenge.TwoFactorRequired);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Equal(email, auth!.User.Email);
        Assert.Equal(HttpStatusCode.OK, withRecovery.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, recoveryAgain.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        Assert.Equal(7, statusAfter!.RecoveryCodesLeft);
    }

    [Fact]
    public async Task Five_wrong_codes_lock_the_second_step_for_a_while()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (client, _) = await factory.CreateUserAsync("Pai", email);
        var secret = await TurnOn(client);
        var step = Totp.StepAt(factory.Clock.GetUtcNow());
        var challenge = (await Login(email)).ChallengeToken;

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SecondStep(challenge, "123456")).StatusCode);
        }

        var locked = await SecondStep(challenge, Totp.Code(secret, step + 1));
        var problem = await locked.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options);

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.Equal("Demasiados códigos errados. Tenta outra vez daqui a 15 minutos.", problem!.Title);
    }

    [Fact]
    public async Task Turning_it_off_needs_the_password_and_a_code()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (client, _) = await factory.CreateUserAsync("Avó", email);
        var secret = await TurnOn(client);
        var step = Totp.StepAt(factory.Clock.GetUtcNow());

        var wrongPassword = await client.PostAsJsonAsync("/api/account/2fa/disable", new DisableTwoFactorRequest("outra", Totp.Code(secret, step + 1)), TestJson.Options);
        var wrongCode = await client.PostAsJsonAsync("/api/account/2fa/disable", new DisableTwoFactorRequest(Password, "000000"), TestJson.Options);
        var off = await client.PostAsJsonAsync("/api/account/2fa/disable", new DisableTwoFactorRequest(Password, Totp.Code(secret, step + 1)), TestJson.Options);
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        var auth = await login.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options);
        var again = await client.PostAsJsonAsync("/api/account/2fa/setup", new TwoFactorSetupRequest(Password), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrongCode.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, off.StatusCode);
        Assert.False(string.IsNullOrEmpty(auth!.AccessToken));
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Child_profiles_have_no_two_step_verification()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var rita = await ChildProfileTests.CreateChild(admin, household.Id, "Rita", "1234");
        var code = await ChildProfileTests.FamilyCode(admin, household.Id);
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, rita.UserId, "1234"));
        var child = factory.CreateClient();
        child.DefaultRequestHeaders.Authorization = new("Bearer", (await login.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!.AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden,
            (await child.PostAsJsonAsync("/api/account/2fa/setup", new TwoFactorSetupRequest("1234"), TestJson.Options)).StatusCode);
    }

    /// <summary>Liga a verificação e devolve o segredo.</summary>
    private async Task<byte[]> TurnOn(HttpClient client)
    {
        var setup = await NotificationTests.Post<TwoFactorSetupDto>(client, "/api/account/2fa/setup", new TwoFactorSetupRequest(Password));
        var secret = Totp.FromBase32(setup.Secret);
        await NotificationTests.Post<TwoFactorEnabledDto>(
            client, "/api/account/2fa/enable", new TwoFactorCodeRequest(Totp.Code(secret, Totp.StepAt(factory.Clock.GetUtcNow()))));
        return secret;
    }

    private async Task<TwoFactorChallengeDto> Login(string email)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TwoFactorChallengeDto>(TestJson.Options))!;
    }

    private Task<HttpResponseMessage> SecondStep(string challenge, string code) =>
        factory.CreateClient().PostAsJsonAsync("/api/auth/login/2fa", new TwoFactorLoginRequest(challenge, code), TestJson.Options);
}
