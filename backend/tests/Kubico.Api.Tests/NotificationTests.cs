using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Notifications;
using Kubico.Api.Realtime;
using Kubico.Api.Rewards;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Kubico.Api.Tests;

public class NotificationTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Members_are_told_when_a_chore_or_an_event_becomes_theirs()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (partner, partnerAuth) = await factory.CreateUserAsync("Pai");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await Join(admin, partner, household.Id, HouseholdRole.Adult);
        await Join(admin, child, household.Id, HouseholdRole.Child);
        var url = $"/api/households/{household.Id}";

        // Tarefa em rotação: o pai é o primeiro; quando a conclui, passa para a mãe.
        var bins = await Post<ChoreDto>(admin, $"{url}/chores", new SaveChoreRequest(
            "Lixo", Recurrence: ChoreRecurrence.Weekly, RotationUserIds: [partnerAuth.User.Id, adminAuth.User.Id]));
        (await partner.PostAsync($"{url}/chores/{bins.Id}/complete", null)).EnsureSuccessStatusCode();

        // Evento: o pai participa e a mãe leva; depois junta-se a Rita.
        var start = new DateTimeOffset(2026, 11, 10, 18, 0, 0, TimeSpan.Zero);
        var swimming = await Post<CalendarEventDto>(admin, $"{url}/events", new SaveEventRequest(
            "Natação", StartsAt: start, EndsAt: start.AddHours(1), ParticipantUserIds: [partnerAuth.User.Id],
            DropOffUserId: adminAuth.User.Id));
        (await admin.PutAsJsonAsync($"{url}/events/{swimming.Id}", new SaveEventRequest(
            "Natação", StartsAt: start, EndsAt: start.AddHours(1), ParticipantUserIds: [partnerAuth.User.Id, childAuth.User.Id],
            PickUpUserId: partnerAuth.User.Id), TestJson.Options)).EnsureSuccessStatusCode();

        var forPartner = await Inbox(partner);
        var forAdmin = await Inbox(admin);
        var forChild = await Inbox(child);

        Assert.Equal(["Novo evento: Natação", "Tarefa para ti: Lixo"], forPartner.Items.Select(n => n.Title));
        Assert.Equal("10/11 às 18:00", forPartner.Items[0].Body);
        Assert.Equal(["É a tua vez: Lixo"], forAdmin.Items.Select(n => n.Title));
        Assert.Equal(["Novo evento: Natação"], forChild.Items.Select(n => n.Title));
        Assert.Equal(2, forPartner.UnreadCount);
    }

    [Fact]
    public async Task Reward_requests_reach_the_adults_and_the_answer_reaches_the_child()
    {
        var (parent, _) = await factory.CreateUserAsync("Mãe");
        var (child, _) = await factory.CreateUserAsync("Rita");
        var household = await HouseholdTests.CreateHousehold(parent, "Casa");
        await Join(parent, child, household.Id, HouseholdRole.Child);
        var url = $"/api/households/{household.Id}";
        var chore = await Post<ChoreDto>(parent, $"{url}/chores", new SaveChoreRequest("Arrumar o quarto", Points: 10));
        (await child.PostAsync($"{url}/chores/{chore.Id}/complete", null)).EnsureSuccessStatusCode();
        var movie = await Post<RewardDto>(parent, $"{url}/rewards", new SaveRewardRequest("Escolher o filme", Cost: 10));

        var request = await Post<RedemptionDto>(child, $"{url}/rewards/{movie.Id}/redeem", null);
        (await parent.PostAsync($"{url}/redemptions/{request.Id}/reject", null)).EnsureSuccessStatusCode();

        var asked = Assert.Single((await Inbox(parent)).Items);
        var answered = Assert.Single((await Inbox(child)).Items);
        Assert.Equal(("Rita pediu: Escolher o filme", NotificationKind.RewardRequested), (asked.Title, asked.Kind));
        Assert.Equal(("Recusado: Escolher o filme", "Os pontos voltaram para ti."), (answered.Title, answered.Body));
    }

    [Fact]
    public async Task Notifications_can_be_marked_as_read_one_by_one_or_all_at_once()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (partner, partnerAuth) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await Join(admin, partner, household.Id, HouseholdRole.Adult);
        foreach (var title in new[] { "Loiça", "Roupa", "Aspirar" })
        {
            await Post<ChoreDto>(admin, $"/api/households/{household.Id}/chores",
                new SaveChoreRequest(title, AssigneeUserId: partnerAuth.User.Id));
        }

        var first = (await Inbox(partner)).Items[0];
        (await partner.PostAsJsonAsync("/api/notifications/read", new MarkReadRequest([first.Id]), TestJson.Options))
            .EnsureSuccessStatusCode();
        var afterOne = await Inbox(partner);
        (await partner.PostAsJsonAsync("/api/notifications/read", new MarkReadRequest(), TestJson.Options))
            .EnsureSuccessStatusCode();
        var afterAll = await Inbox(partner);

        Assert.Equal(2, afterOne.UnreadCount);
        Assert.True(afterOne.Items.Single(n => n.Id == first.Id).IsRead);
        Assert.Equal(0, afterAll.UnreadCount);
    }

    [Fact]
    public async Task Notifications_arrive_in_real_time_on_the_personal_channel()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (partner, partnerAuth) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await Join(admin, partner, household.Id, HouseholdRole.Adult);

        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HouseholdHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(partnerAuth.AccessToken);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = TestJson.Options)
            .Build();
        var received = new TaskCompletionSource<NotificationDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<NotificationDto>(HouseholdEvents.NotificationCreated, n => received.TrySetResult(n));
        await connection.StartAsync();

        await Post<ChoreDto>(admin, $"/api/households/{household.Id}/chores",
            new SaveChoreRequest("Regar as plantas", AssigneeUserId: partnerAuth.User.Id));

        var notification = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Tarefa para ti: Regar as plantas", notification.Title);
    }

    [Fact]
    public async Task Preferences_are_saved_and_quiet_hours_need_a_start_and_an_end()
    {
        var (client, _) = await factory.CreateUserAsync();

        var defaults = await client.GetFromJsonAsync<NotificationPreferencesDto>("/api/notifications/preferences", TestJson.Options);
        var saved = await client.PutAsJsonAsync("/api/notifications/preferences", new NotificationPreferencesDto(
            NotificationDelivery.DailyDigest, new TimeOnly(20, 30), new TimeOnly(22, 0), new TimeOnly(7, 30), "Europe/Lisbon"),
            TestJson.Options);
        var halfQuiet = await client.PutAsJsonAsync("/api/notifications/preferences", new NotificationPreferencesDto(
            NotificationDelivery.Immediate, new TimeOnly(19, 0), new TimeOnly(22, 0), null, "Europe/Lisbon"), TestJson.Options);
        var reread = await client.GetFromJsonAsync<NotificationPreferencesDto>("/api/notifications/preferences", TestJson.Options);

        Assert.Equal(NotificationDelivery.Immediate, defaults!.Delivery);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, halfQuiet.StatusCode);
        Assert.Equal((NotificationDelivery.DailyDigest, new TimeOnly(7, 30)), (reread!.Delivery, reread.QuietUntil!.Value));
    }

    internal static async Task Join(HttpClient admin, HttpClient member, Guid householdId, HouseholdRole role)
    {
        var invite = await HouseholdTests.CreateInvite(admin, householdId, role);
        (await member.PostAsync($"/api/invites/{invite.Code}/accept", null)).EnsureSuccessStatusCode();
    }

    internal static async Task<T> Post<T>(HttpClient client, string url, object? body)
    {
        var response = body is null ? await client.PostAsync(url, null) : await client.PostAsJsonAsync(url, body, TestJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Options))!;
    }

    internal static async Task<NotificationsDto> Inbox(HttpClient client) =>
        (await client.GetFromJsonAsync<NotificationsDto>("/api/notifications", TestJson.Options))!;
}

/// <summary>
/// Envio para os telemóveis. Cada teste tem o seu servidor, porque avança o relógio falso (horas de silêncio,
/// hora do resumo) e isso estragaria os tokens de acesso dos outros testes.
/// </summary>
public class NotificationDispatchTests
{
    [Fact]
    public async Task Immediate_notifications_wait_for_the_end_of_quiet_hours_and_are_sent_once()
    {
        using var setup = await Setup.CreateAsync();
        await setup.SavePreferences(NotificationDelivery.Immediate, quietFrom: new TimeOnly(22, 0), quietUntil: new TimeOnly(8, 0));
        await setup.AssignChore("Lixo");

        var atMidnight = await setup.Dispatch();
        setup.Clock.Advance(TimeSpan.FromHours(8));
        var atEight = await setup.Dispatch();
        var again = await setup.Dispatch();

        Assert.Equal(0, atMidnight);
        Assert.Equal(1, atEight);
        Assert.Equal(0, again);
        var (token, message) = Assert.Single(setup.Push.Sent);
        Assert.Equal(("telemovel-do-pai", "Tarefa para ti: Lixo"), (token, message.Title));
        Assert.Equal("ChoreAssigned", message.Data["kind"]);
    }

    [Fact]
    public async Task Daily_digest_sends_one_message_at_the_chosen_time()
    {
        using var setup = await Setup.CreateAsync();
        await setup.SavePreferences(NotificationDelivery.DailyDigest, digestAt: new TimeOnly(19, 0));
        await setup.AssignChore("Loiça");
        await setup.AssignChore("Roupa");

        var before = await setup.Dispatch();
        setup.Clock.Advance(TimeSpan.FromHours(19));
        var atSeven = await setup.Dispatch();
        setup.Clock.Advance(TimeSpan.FromHours(1));
        var laterSameDay = await setup.Dispatch();

        Assert.Equal((0, 1, 0), (before, atSeven, laterSameDay));
        var (_, digest) = Assert.Single(setup.Push.Sent);
        Assert.Equal("Kubico: 2 novidades", digest.Title);
        Assert.Contains("Tarefa para ti: Loiça", digest.Body);
    }

    [Fact]
    public async Task Read_notifications_and_in_app_only_preferences_send_nothing_and_dead_phones_are_forgotten()
    {
        using var setup = await Setup.CreateAsync();
        await setup.AssignChore("Já vista");
        (await setup.Partner.PostAsJsonAsync("/api/notifications/read", new MarkReadRequest(), TestJson.Options))
            .EnsureSuccessStatusCode();
        var nothingUnread = await setup.Dispatch();

        setup.Push.InvalidTokens.Add("telemovel-do-pai");
        await setup.AssignChore("Para um telemóvel que já não existe");
        await setup.Dispatch();
        var devicesLeft = await setup.Count(db => db.PushDevices.Count());

        await setup.SavePreferences(NotificationDelivery.InAppOnly);
        await setup.AssignChore("Só na app");
        var inAppOnly = await setup.Dispatch();
        var stillPending = await setup.Count(db => db.Notifications.Count(n => n.PushedAt == null));

        Assert.Equal(0, nothingUnread);
        Assert.Equal(0, devicesLeft);
        Assert.Equal(0, inAppOnly);
        Assert.Equal(0, stillPending);
        Assert.Empty(setup.Push.Sent);
    }

    /// <summary>A mãe atribui tarefas ao pai, que tem um telemóvel registado e preferências no fuso UTC.</summary>
    private sealed class Setup : IDisposable
    {
        private readonly KubicoApiFactory _factory = new();
        private readonly WebApplicationFactory<Program> _app;
        private Guid _householdId;
        private Guid _partnerId;

        private Setup()
        {
            _app = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IPushSender>(Push)));
        }

        public RecordingPushSender Push { get; } = new();
        public FakeTimeProvider Clock => _factory.Clock;
        public HttpClient Admin { get; private set; } = null!;
        public HttpClient Partner { get; private set; } = null!;

        public static async Task<Setup> CreateAsync()
        {
            var setup = new Setup();
            setup.Admin = await setup.Register("Mãe");
            setup.Partner = await setup.Register("Pai");
            var household = await HouseholdTests.CreateHousehold(setup.Admin, "Casa");
            setup._householdId = household.Id;
            await NotificationTests.Join(setup.Admin, setup.Partner, household.Id, HouseholdRole.Adult);
            var me = await setup.Partner.GetFromJsonAsync<Kubico.Api.Households.MeDto>("/api/me", TestJson.Options);
            setup._partnerId = me!.User.Id;
            (await setup.Partner.PostAsJsonAsync("/api/notifications/devices",
                new RegisterDeviceRequest("telemovel-do-pai", "android"), TestJson.Options)).EnsureSuccessStatusCode();
            await setup.SavePreferences(NotificationDelivery.Immediate);
            return setup;
        }

        public async Task SavePreferences(
            NotificationDelivery delivery, TimeOnly? digestAt = null, TimeOnly? quietFrom = null, TimeOnly? quietUntil = null) =>
            (await Partner.PutAsJsonAsync("/api/notifications/preferences", new NotificationPreferencesDto(
                delivery, digestAt ?? new TimeOnly(19, 0), quietFrom, quietUntil, "UTC"), TestJson.Options)).EnsureSuccessStatusCode();

        /// <summary>Pelo serviço (e não pela API), porque o relógio pode já estar à frente dos tokens.</summary>
        public async Task AssignChore(string title)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<NotificationService>().NotifyAsync(
                _householdId, [_partnerId], Guid.Empty, NotificationKind.ChoreAssigned, $"Tarefa para ti: {title}", null, null, default);
        }

        public async Task<int> Dispatch()
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<NotificationDispatcher>().DispatchAsync(default);
        }

        public async Task<int> Count(Func<Kubico.Infrastructure.Persistence.KubicoDbContext, int> query)
        {
            await using var scope = _app.Services.CreateAsyncScope();
            return query(scope.ServiceProvider.GetRequiredService<Kubico.Infrastructure.Persistence.KubicoDbContext>());
        }

        private async Task<HttpClient> Register(string name)
        {
            var client = _app.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/register",
                new Kubico.Api.Auth.RegisterRequest($"{Guid.NewGuid():N}@kubico.test", "palavra-passe-segura", name));
            var auth = (await response.Content.ReadFromJsonAsync<Kubico.Api.Auth.AuthResponse>(TestJson.Options))!;
            client.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);
            return client;
        }

        public void Dispose()
        {
            _app.Dispose();
            _factory.Dispose();
        }
    }
}

public sealed class RecordingPushSender : IPushSender
{
    public List<(string Token, PushMessage Message)> Sent { get; } = [];
    public HashSet<string> InvalidTokens { get; } = [];

    public Task<PushResult> SendAsync(string deviceToken, PushMessage message, CancellationToken ct)
    {
        if (InvalidTokens.Contains(deviceToken))
        {
            return Task.FromResult(PushResult.InvalidToken);
        }

        Sent.Add((deviceToken, message));
        return Task.FromResult(PushResult.Sent);
    }
}

public class FcmPushSenderTests
{
    [Fact]
    public async Task Signs_in_with_the_service_account_once_and_sends_one_message_per_phone()
    {
        using var rsa = RSA.Create(2048);
        var serviceAccount = JsonSerializer.Serialize(new
        {
            project_id = "kubico-teste",
            client_email = "push@kubico-teste.iam.gserviceaccount.com",
            private_key = rsa.ExportPkcs8PrivateKeyPem(),
            token_uri = "https://oauth2.googleapis.com/token",
        });
        var tokenRequests = 0;
        var messages = new List<JsonElement>();
        var handler = new FakeHandler(async request =>
        {
            if (request.RequestUri!.AbsoluteUri == "https://oauth2.googleapis.com/token")
            {
                tokenRequests++;
                var form = await request.Content!.ReadAsStringAsync();
                var assertion = Uri.UnescapeDataString(form.Split("assertion=")[1]);
                var parts = assertion.Split('.');
                Assert.True(rsa.VerifyData(
                    Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                    System.Buffers.Text.Base64Url.DecodeFromChars(parts[2]),
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1));
                Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", form);
                return Json("""{"access_token":"ya29.teste","expires_in":3600}""");
            }

            Assert.Equal("https://fcm.googleapis.com/v1/projects/kubico-teste/messages:send", request.RequestUri.AbsoluteUri);
            Assert.Equal("Bearer ya29.teste", request.Headers.Authorization!.ToString());
            var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()).RootElement.GetProperty("message");
            messages.Add(body.Clone());
            return body.GetProperty("token").GetString() == "desinstalado"
                ? new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("""{"error":{"status":"NOT_FOUND"}}""") }
                : Json("""{"name":"projects/kubico-teste/messages/1"}""");
        });
        var sender = new FcmPushSender(
            new HttpClient(handler),
            Options.Create(new FcmOptions { ServiceAccountJson = serviceAccount }),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            NullLogger<FcmPushSender>.Instance);
        var message = new PushMessage("Tarefa para ti: Lixo", "Prazo: 15/10", new Dictionary<string, string> { ["kind"] = "ChoreAssigned" });

        var first = await sender.SendAsync("telemovel-1", message, default);
        var second = await sender.SendAsync("telemovel-2", message, default);
        var gone = await sender.SendAsync("desinstalado", message, default);

        Assert.Equal((PushResult.Sent, PushResult.Sent, PushResult.InvalidToken), (first, second, gone));
        Assert.Equal(1, tokenRequests);
        Assert.Equal("Tarefa para ti: Lixo", messages[0].GetProperty("notification").GetProperty("title").GetString());
        Assert.Equal("ChoreAssigned", messages[1].GetProperty("data").GetProperty("kind").GetString());
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }
}
