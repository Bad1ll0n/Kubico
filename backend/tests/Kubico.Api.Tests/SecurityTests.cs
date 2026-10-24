using System.Net;
using System.Net.Http.Json;
using System.Text;
using Kubico.Api.Auth;
using Kubico.Api.Calendar;
using Kubico.Api.Common;
using Kubico.Api.Notifications;
using Kubico.Api.Realtime;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Kubico.Domain.Users;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Tests;

/// <summary>Segurança (S1, S3, S4, S5, S7, S9, S11, S12, S15, S18).</summary>
public class SecurityTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Signing_out_everywhere_closes_the_live_connections_and_they_cannot_come_back_with_the_old_token()
    {
        var (client, auth) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        await using var connection = Connect(auth.AccessToken);
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Closed += _ => { closed.TrySetResult(); return Task.CompletedTask; };
        await connection.StartAsync();
        await connection.InvokeAsync("JoinHousehold", household.Id);

        (await client.PostAsync("/api/auth/logout-all", null)).EnsureSuccessStatusCode();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // O token de acesso ainda não expirou, mas a sessão terminou: não volta a ligar.
        await using var again = Connect(auth.AccessToken);
        var reconnect = await Record.ExceptionAsync(async () =>
        {
            await again.StartAsync();
            await again.InvokeAsync("JoinHousehold", household.Id);
        });
        Assert.NotNull(reconnect);
    }

    [Fact]
    public async Task Turning_on_two_step_verification_needs_the_password_and_ends_the_other_sessions()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (client, _) = await factory.CreateUserAsync("Mãe", email);
        var other = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "palavra-passe-segura"));
        var otherSession = (await other.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;

        var noBody = await client.PostAsync("/api/account/2fa/setup", null);
        var wrong = await client.PostAsJsonAsync("/api/account/2fa/setup", new TwoFactorSetupRequest("adivinha"), TestJson.Options);
        var setup = await NotificationTests.Post<TwoFactorSetupDto>(client, "/api/account/2fa/setup", new TwoFactorSetupRequest("palavra-passe-segura"));
        await NotificationTests.Post<TwoFactorEnabledDto>(client, "/api/account/2fa/enable",
            new TwoFactorCodeRequest(Totp.Code(Totp.FromBase32(setup.Secret), Totp.StepAt(factory.Clock.GetUtcNow()))));
        var otherRefresh = await factory.CreateClient().PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(otherSession.RefreshToken));
        var stillHere = await client.GetAsync("/api/account/2fa");

        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, otherRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, stillHere.StatusCode);
    }

    [Fact]
    public async Task A_failed_login_checks_a_password_even_when_the_email_has_no_account()
    {
        var counting = new CountingHasher();
        await using var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IPasswordHasher<User>>(counting)));

        var response = await app.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest("ninguem-mesmo@kubico.test", "qualquer-coisa"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, counting.Verifications);
    }

    [Fact]
    public async Task Ending_a_session_stops_the_push_notices_of_that_phone()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (client, auth) = await factory.CreateUserAsync("Pai", email);
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "palavra-passe-segura"));
        var tablet = Fase1.Authorized(factory, (await login.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!.AccessToken);
        (await client.PostAsJsonAsync("/api/notifications/devices", new RegisterDeviceRequest("fid-telemovel", "android"), TestJson.Options))
            .EnsureSuccessStatusCode();
        (await tablet.PostAsJsonAsync("/api/notifications/devices", new RegisterDeviceRequest("fid-tablet", "android"), TestJson.Options))
            .EnsureSuccessStatusCode();

        (await factory.CreateClient().PostAsJsonAsync("/api/auth/logout", new RefreshRequest(auth.RefreshToken))).EnsureSuccessStatusCode();
        var afterOne = await Devices(email);
        (await tablet.PostAsync("/api/auth/logout-all", null)).EnsureSuccessStatusCode();
        var afterAll = await Devices(email);

        Assert.Equal(["fid-tablet"], afterOne);
        Assert.Empty(afterAll);
    }

    [Fact]
    public async Task Responses_carry_security_headers_and_personal_data_is_not_cached()
    {
        var (client, _) = await factory.CreateUserAsync();

        var me = await client.GetAsync("/api/me");
        var health = await client.GetAsync("/health");

        Assert.Equal("nosniff", me.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", me.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("no-referrer", me.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("default-src 'none'", me.Headers.GetValues("Content-Security-Policy").Single());
        Assert.True(me.Headers.CacheControl?.NoStore);
        Assert.Equal("nosniff", health.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task Web_sessions_last_fourteen_days_and_tokens_have_little_clock_tolerance()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        await factory.CreateUserAsync("Ana", email);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(email, "palavra-passe-segura")),
        };
        request.Headers.Add("X-Kubico-Device", "web");

        var web = (await (await factory.CreateClient().SendAsync(request)).Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
        var jwt = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(factory.Clock.GetUtcNow().AddDays(14), web.RefreshTokenExpiresAt);
        Assert.Equal(TimeSpan.FromSeconds(30), jwt.TokenValidationParameters.ClockSkew);
    }

    [Fact]
    public async Task After_signing_out_everywhere_sensitive_actions_stop_and_calendar_addresses_can_be_renewed()
    {
        var (client, _) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var feed = await NotificationTests.Post<CalendarFeedDto>(client, $"/api/households/{household.Id}/calendar-feed", null);

        (await client.PostAsJsonAsync("/api/auth/logout-all", new LogoutAllRequest(ResetCalendarFeeds: true), TestJson.Options))
            .EnsureSuccessStatusCode();
        var export = await client.GetAsync("/api/account/export");
        var oldFeed = await factory.CreateClient().GetAsync(new Uri(feed.Url).AbsolutePath);

        Assert.Equal(HttpStatusCode.Unauthorized, export.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, oldFeed.StatusCode);
    }

    [Fact]
    public async Task The_data_protection_keys_are_stored_encrypted_with_the_master_key()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (client, _) = await factory.CreateUserAsync("Mãe", email);
        await NotificationTests.Post<TwoFactorSetupDto>(client, "/api/account/2fa/setup", new TwoFactorSetupRequest("palavra-passe-segura"));

        await using var scope = factory.Services.CreateAsyncScope();
        var keys = await scope.ServiceProvider.GetRequiredService<KubicoDbContext>().DataProtectionKeys.AsNoTracking().ToListAsync();

        Assert.NotEmpty(keys);
        Assert.All(keys, k => Assert.Contains("<encryptedKey", k.Xml));
        Assert.All(keys, k => Assert.DoesNotContain("<masterKey", k.Xml));
    }

    [Fact]
    public void A_key_encrypted_with_one_master_key_is_not_read_with_another()
    {
        var master = new DataProtectionMasterKey(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        var other = new DataProtectionMasterKey(Enumerable.Range(2, 32).Select(i => (byte)i).ToArray());
        var secret = new System.Xml.Linq.XElement("masterKey", "segredo");

        var encrypted = new MasterKeyXmlEncryptor(master).Encrypt(secret).EncryptedElement;
        var back = new MasterKeyXmlDecryptor(new ServiceCollection().AddSingleton(master).BuildServiceProvider()).Decrypt(encrypted);

        Assert.DoesNotContain("segredo", encrypted.ToString());
        Assert.Equal("segredo", back.Value);
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            new MasterKeyXmlDecryptor(new ServiceCollection().AddSingleton(other).BuildServiceProvider()).Decrypt(encrypted));
    }

    [Theory]
    [InlineData("Production", "dev-only-signing-key-change-me-in-production-0123456789", "*", true, true)]
    [InlineData("Staging", "uma-chave-propria-com-mais-de-32-caracteres-123", "*", false, true)]
    [InlineData("Production", "uma-chave-propria-com-mais-de-32-caracteres-123", "https://app.kubico.pt", false, true)]
    [InlineData("Production", "uma-chave-propria-com-mais-de-32-caracteres-123", "https://app.kubico.pt", true, false)]
    [InlineData("Development", "dev-only-signing-key-change-me-in-production-0123456789", "*", false, false)]
    public void Outside_development_the_api_does_not_start_with_development_settings(
        string environment, string signingKey, string origin, bool complete, bool fails)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = signingKey,
            ["Cors:AllowedOrigins:0"] = origin,
        };
        if (complete)
        {
            settings["DataProtection:MasterKey"] = Convert.ToBase64String(new byte[32]);
            settings["PublicBaseUrl"] = "https://api.kubico.pt";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var error = Record.Exception(() => ProductionChecks.Validate(configuration, new Environment(environment)));

        Assert.Equal(fails, error is not null);
    }

    [Fact]
    public async Task Photos_are_stored_without_their_metadata()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var exif = Encoding.ASCII.GetBytes("Exif\0\0GPSLatitude=38.7223;GPSLongitude=-9.1393");
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE1, 0x00, (byte)(exif.Length + 2), .. exif, 0xFF, 0xDB, 0x00, 0x03, 0x01, 0xFF, 0xD9];
        byte[] html = [0xFF, 0xD8, 0xFF, .. "<html><script>alert(1)</script></html>"u8];

        var stored = await PhotoProofTests.Upload(admin, household, jpeg);
        var served = await admin.GetByteArrayAsync($"/api/households/{household}/chores/photos/{stored.Id}");
        var disguised = await PhotoProofTests.UploadRaw(admin, household, html);

        Assert.DoesNotContain("GPS", Encoding.ASCII.GetString(served));
        Assert.Equal([0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x03, 0x01, 0xFF, 0xD9], served);
        Assert.Equal(HttpStatusCode.BadRequest, disguised.StatusCode);
    }

    [Fact]
    public void Png_and_webp_text_and_exif_blocks_are_removed()
    {
        static byte[] Chunk(string type, byte[] data) =>
            [.. BitConverter.GetBytes(data.Length).Reverse(), .. Encoding.ASCII.GetBytes(type), .. data, 0, 0, 0, 0];
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. Chunk("IHDR", new byte[13]),
            .. Chunk("tEXt", "GPS\0Lisboa"u8.ToArray()), .. Chunk("IDAT", [1, 2, 3]), .. Chunk("IEND", []), .. "lixo"u8];
        static byte[] Riff(string type, byte[] data) =>
            [.. Encoding.ASCII.GetBytes(type), .. BitConverter.GetBytes(data.Length), .. data, .. data.Length % 2 == 1 ? new byte[] { 0 } : []];
        byte[] chunks = [.. Riff("VP8X", [0x0C, 0, 0, 0, 0, 0, 0, 0, 0, 0]), .. Riff("VP8 ", [9, 9]), .. Riff("EXIF", "GPS"u8.ToArray())];
        byte[] webp = [.. "RIFF"u8, .. BitConverter.GetBytes(chunks.Length + 4), .. "WEBP"u8, .. chunks];

        var cleanPng = PhotoMetadata.Strip(png, "image/png")!;
        var cleanWebp = PhotoMetadata.Strip(webp, "image/webp")!;

        Assert.DoesNotContain("GPS", Encoding.ASCII.GetString(cleanPng));
        Assert.DoesNotContain("lixo", Encoding.ASCII.GetString(cleanPng));
        Assert.Contains("IDAT", Encoding.ASCII.GetString(cleanPng));
        Assert.DoesNotContain("GPS", Encoding.ASCII.GetString(cleanWebp));
        Assert.Equal(0, cleanWebp[20] & 0x0C);
        Assert.Equal(cleanWebp.Length - 8, BitConverter.ToInt32(cleanWebp, 4));
        Assert.Null(PhotoMetadata.Strip("RIFF0000WEBPHTML<html>"u8.ToArray(), "image/webp"));
    }

    private HubConnection Connect(string accessToken) => new HubConnectionBuilder()
        .WithUrl(new Uri(factory.Server.BaseAddress, HouseholdHub.Path), o =>
        {
            o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            o.Transports = HttpTransportType.LongPolling;
            o.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
        })
        .AddJsonProtocol(o => o.PayloadSerializerOptions = TestJson.Options)
        .Build();

    private async Task<List<string>> Devices(string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<KubicoDbContext>();
        var userId = await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync();
        return await db.PushDevices.Where(d => d.UserId == userId).Select(d => d.Token).ToListAsync();
    }

    private sealed class CountingHasher : IPasswordHasher<User>
    {
        private readonly PasswordHasher<User> _inner = new();
        private int _verifications;

        public int Verifications => _verifications;

        public string HashPassword(User user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(User user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verifications);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Kubico";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

/// <summary>Limite de pedidos e de palavras-passe erradas (S2). Servidor próprio, com o limitador ligado.</summary>
public sealed class RateLimitTests : IDisposable
{
    private readonly KubicoApiFactory _factory = new();
    private readonly WebApplicationFactory<Program> _app;

    public RateLimitTests() =>
        _app = _factory.WithWebHostBuilder(b => b.UseSetting("RateLimiting:Enabled", "true").UseSetting("RateLimiting:LoginPerMinute", "10"));

    [Fact]
    public async Task The_eleventh_login_in_a_minute_from_the_same_address_waits()
    {
        var client = _app.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/login", new LoginRequest($"ninguem{i}@kubico.test", "x"))).StatusCode);
        }

        var last = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("outra@kubico.test", "x"));

        Assert.All(statuses.Take(10), s => Assert.Equal(HttpStatusCode.Unauthorized, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
        Assert.Equal(HttpStatusCode.TooManyRequests, last.StatusCode);
        Assert.True(last.Headers.RetryAfter is not null);
    }

    [Fact]
    public async Task Guessing_family_codes_is_limited_by_address()
    {
        var client = _app.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 31; i++)
        {
            statuses.Add((await client.PostAsJsonAsync("/api/auth/child/profiles", new FamilyCodeRequest($"CODIGO{i:00}"))).StatusCode);
        }

        Assert.All(statuses.Take(30), s => Assert.Equal(HttpStatusCode.NotFound, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[30]);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_email_even_with_the_right_one_and_even_without_an_account()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        await _factory.CreateUserAsync("Vítima", email);
        var client = _factory.CreateClient();
        var wrong = new List<HttpStatusCode>();
        var unknown = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            wrong.Add((await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, $"errada-{i}"))).StatusCode);
            unknown.Add((await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("nao-existe@kubico.test", $"errada-{i}"))).StatusCode);
        }

        var right = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "palavra-passe-segura"));
        var problem = await Fase1.ProblemAsync(right);
        _factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var later = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "palavra-passe-segura"));

        Assert.Equal(wrong.Select(s => s), unknown.Select(s => s));
        Assert.Equal(HttpStatusCode.TooManyRequests, wrong[4]);
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.Equal("Demasiadas tentativas erradas. Tenta outra vez daqui a um minuto.", problem.Title);
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    public void Dispose()
    {
        _app.Dispose();
        _factory.Dispose();
    }
}

/// <summary>Mesada paga antes de o período acabar (T16). Servidor próprio: o teste avança o relógio.</summary>
public sealed class AllowanceTopUpTests : IDisposable
{
    private readonly KubicoApiFactory _factory = new();

    [Fact]
    public async Task Chores_done_after_paying_the_current_week_are_owed_at_the_end_and_can_be_paid()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var (child, rita) = await Fase1.MemberAsync(_factory, admin, household, "Rita", HouseholdRole.Child);
        var url = $"/api/households/{household}/allowances";
        var week = Kubico.Domain.Rewards.Allowance.PeriodStart(_factory.Today, Kubico.Domain.Rewards.AllowancePeriod.Weekly);
        await admin.PutAsJsonAsync($"{url}/{rita}", new Kubico.Api.Rewards.SaveAllowanceRequest(500, 100), TestJson.Options);
        async Task Chore(string title)
        {
            var chore = await Fase1.ChoreAsync(admin, household, new Kubico.Api.Chores.SaveChoreRequest(title, AssigneeUserId: rita));
            (await Fase1.CompleteAsync(child, household, chore.Id)).EnsureSuccessStatusCode();
        }

        await Chore("Uma");
        (await admin.PostAsJsonAsync($"{url}/{rita}/payments", new Kubico.Api.Rewards.PayAllowanceRequest(week), TestJson.Options))
            .EnsureSuccessStatusCode();
        await Chore("Duas");
        await Chore("Três");
        var midWeek = (await admin.GetFromJsonAsync<List<Kubico.Api.Rewards.AllowanceDto>>(url, TestJson.Options))!.Single();
        _factory.Clock.Advance(TimeSpan.FromDays(7));
        var nextWeek = (await admin.GetFromJsonAsync<List<Kubico.Api.Rewards.AllowanceDto>>(url, TestJson.Options))!.Single();
        var rest = await admin.PostAsJsonAsync($"{url}/{rita}/payments", new Kubico.Api.Rewards.PayAllowanceRequest(week), TestJson.Options);
        var paidUp = (await rest.Content.ReadFromJsonAsync<Kubico.Api.Rewards.AllowanceDto>(TestJson.Options))!;
        var again = await admin.PostAsJsonAsync($"{url}/{rita}/payments", new Kubico.Api.Rewards.PayAllowanceRequest(week), TestJson.Options);
        var inbox = await NotificationTests.Inbox(child);

        var paid = midWeek.Periods.Single(p => p.Start == week);
        Assert.Equal((3, 600, 200), (paid.ChoresDone, paid.AmountCents, paid.OwedCents));
        Assert.Equal(0, midWeek.OwedCents);
        Assert.Equal(200, nextWeek.OwedCents);
        Assert.Equal(HttpStatusCode.OK, rest.StatusCode);
        Assert.Equal((800, 0), (paidUp.Periods.Single(p => p.Start == week).AmountCents, paidUp.OwedCents));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Contains(inbox.Items, n => n.Title == "Recebeste o resto da mesada: 2,00 €");
    }

    public void Dispose() => _factory.Dispose();
}
