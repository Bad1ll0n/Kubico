using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Kubico.Api.Calendar;
using Kubico.Api.Calendar.Sync;
using Kubico.Api.Chores;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Api.Shopping;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;
using Kubico.Domain.Notifications;
using Kubico.Domain.Shopping;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Kubico.Api.Tests;

/// <summary>
/// Fase 2 (desempenho): compressão e cache HTTP (P3), mensagens de tempo real agrupadas e com as contagens (P2, P4),
/// "preflight" guardado (P9), índices da exportação (P15) e o JSON das ocorrências sem campos vazios (P1).
/// </summary>
public class PerformanceFixesTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Responses_are_compressed_except_the_ones_with_tokens()
    {
        var (admin, _, householdId) = await Fase1.HouseholdAsync(factory);
        var email = $"{Guid.NewGuid():N}@kubico.test";
        await factory.CreateUserAsync("Ana", email);

        var brotli = await SendAsync(admin, HttpMethod.Get, $"/api/households/{householdId}/events?from=2026-10-01&to=2026-11-11", "br, gzip");
        var gzip = await SendAsync(admin, HttpMethod.Get, $"/api/households/{householdId}/chores", "gzip");
        var plain = await SendAsync(admin, HttpMethod.Get, $"/api/households/{householdId}/chores", null);
        var login = await SendAsync(factory.CreateClient(), HttpMethod.Post, "/api/auth/login", "br, gzip",
            JsonContent.Create(new LoginRequest(email, "palavra-passe-segura")));

        Assert.Equal(["br"], brotli.Content.Headers.ContentEncoding);
        Assert.Contains("Accept-Encoding", brotli.Headers.Vary);
        Assert.Equal(["gzip"], gzip.Content.Headers.ContentEncoding);
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Empty(login.Content.Headers.ContentEncoding);
        Assert.True(brotli.Headers.CacheControl?.NoStore, "os dados continuam sem cache");
    }

    [Fact]
    public async Task Photos_are_kept_only_on_the_device_and_confirmed_with_the_server_each_time()
    {
        var (admin, _, householdId) = await Fase1.HouseholdAsync(factory);
        var (stranger, _, _) = await Fase1.HouseholdAsync(factory);
        var photo = await PhotoProofTests.Upload(admin, householdId, PhotoProofTests.Jpeg);
        var url = $"/api/households/{householdId}/chores/photos/{photo.Id}";

        var first = await admin.GetAsync(url);
        var etag = first.Headers.ETag!;
        var again = await SendAsync(admin, HttpMethod.Get, url, null, ifNoneMatch: etag);
        var strangerAgain = await SendAsync(stranger, HttpMethod.Get, url, null, ifNoneMatch: etag);
        var me = await admin.GetAsync("/api/me");

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal($"\"{photo.Id:N}\"", etag.Tag);
        Assert.True(first.Headers.CacheControl!.Private);
        Assert.True(first.Headers.CacheControl.NoCache);
        Assert.False(first.Headers.CacheControl.NoStore);
        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Empty(await again.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, strangerAgain.StatusCode);
        Assert.True(me.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task The_calendar_feed_answers_not_modified_while_nothing_changes()
    {
        var (admin, _, householdId) = await Fase1.HouseholdAsync(factory);
        var feed = await NotificationTests.Post<CalendarFeedDto>(admin, $"/api/households/{householdId}/calendar-feed", null);
        var path = new Uri(feed.Url).AbsolutePath;
        var anonymous = factory.CreateClient();

        var first = await SendAsync(anonymous, HttpMethod.Get, path, "gzip");
        var same = await SendAsync(anonymous, HttpMethod.Get, path, "gzip", ifNoneMatch: first.Headers.ETag);
        await NotificationTests.Post<CalendarEventDto>(admin, $"/api/households/{householdId}/events", new SaveEventRequest(
            "Jantar", StartsAt: factory.Clock.GetUtcNow().AddDays(2), EndsAt: factory.Clock.GetUtcNow().AddDays(2).AddHours(2)));
        var changed = await SendAsync(anonymous, HttpMethod.Get, path, "gzip", ifNoneMatch: first.Headers.ETag);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(["gzip"], first.Content.Headers.ContentEncoding);
        Assert.True(first.Headers.CacheControl!.Private && first.Headers.CacheControl.NoCache);
        Assert.Equal(HttpStatusCode.NotModified, same.StatusCode);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(first.Headers.ETag, changed.Headers.ETag);
    }

    [Fact]
    public async Task Occurrences_leave_out_the_empty_fields()
    {
        var (admin, _, householdId) = await Fase1.HouseholdAsync(factory);
        await NotificationTests.Post<CalendarEventDto>(admin, $"/api/households/{householdId}/events", new SaveEventRequest(
            "Natação", StartsAt: new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero), EndsAt: new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero)));
        await NotificationTests.Post<CalendarEventDto>(admin, $"/api/households/{householdId}/events", new SaveEventRequest(
            "Férias", IsAllDay: true, StartDate: new DateOnly(2026, 10, 21)));

        var json = await admin.GetStringAsync($"/api/households/{householdId}/events?from=2026-10-19&to=2026-10-25");
        var calendar = System.Text.Json.JsonSerializer.Deserialize<CalendarDto>(json, TestJson.Options)!;

        using var document = System.Text.Json.JsonDocument.Parse(json);
        var raw = document.RootElement.GetProperty("occurrences").EnumerateArray().ToList();
        Assert.All(raw, o => Assert.True(o.EnumerateObject().All(p => p.Value.ValueKind != System.Text.Json.JsonValueKind.Null)));
        Assert.Contains(raw, o => !o.TryGetProperty("startDate", out _));
        Assert.Contains(raw, o => !o.TryGetProperty("startsAt", out _));
        Assert.Equal(2, calendar.Occurrences.Count);
        Assert.Contains(calendar.Occurrences, o => o.StartsAt is not null && o.StartDate is null);
        Assert.Contains(calendar.Occurrences, o => o.StartDate is not null && o.StartsAt is null);
    }

    [Fact]
    public async Task Clearing_the_cart_sends_one_message_and_every_item_change_brings_the_counts()
    {
        var (admin, _, householdId) = await Fase1.HouseholdAsync(factory);
        var (partner, partnerId) = await Fase1.MemberAsync(factory, admin, householdId, "Pai", HouseholdRole.Adult);
        var list = await NotificationTests.Post<ShoppingListDto>(admin, $"/api/households/{householdId}/lists", new CreateShoppingListRequest("Super"));
        var items = $"/api/households/{householdId}/lists/{list.Id}/items";
        var added = new List<ShoppingItemDto>();
        foreach (var name in new[] { "Pão", "Leite", "Ovos", "Café" })
        {
            added.Add(await NotificationTests.Post<ShoppingItemDto>(admin, items, new AddShoppingItemRequest(name)));
        }

        await using var hub = await ConnectAsync(partnerId, partner, householdId);
        foreach (var item in added.Take(3))
        {
            (await admin.PatchAsJsonAsync($"{items}/{item.Id}", new UpdateShoppingItemRequest(IsChecked: true), TestJson.Options)).EnsureSuccessStatusCode();
        }

        await hub.WaitForAsync(HouseholdEvents.ShoppingListUpserted, 3);
        hub.Clear();
        (await admin.PostAsync($"{items}/clear-checked", null)).EnsureSuccessStatusCode();
        await hub.WaitForAsync(HouseholdEvents.ShoppingListUpserted, 1);
        await Task.Delay(200);

        Assert.Equal(0, hub.Count(HouseholdEvents.ShoppingItemDeleted));
        var (listId, ids) = ((Guid, Guid[]))Assert.Single(hub.Received[HouseholdEvents.ShoppingItemsDeleted]);
        Assert.Equal(list.Id, listId);
        Assert.Equal(added.Take(3).Select(i => i.Id).Order(), ids.Order());
        var counts = (ShoppingListDto)Assert.Single(hub.Received[HouseholdEvents.ShoppingListUpserted]);
        Assert.Equal((1, 0), (counts.ItemCount, counts.CheckedCount));
        Assert.Equal(2, hub.Messages.Count);
    }

    [Fact]
    public async Task Deleting_a_series_with_changed_days_sends_one_message()
    {
        var (admin, _, householdId) = await Fase1.HouseholdAsync(factory);
        var (partner, partnerId) = await Fase1.MemberAsync(factory, admin, householdId, "Pai", HouseholdRole.Adult);
        var url = $"/api/households/{householdId}/events";
        var series = await NotificationTests.Post<CalendarEventDto>(admin, url, new SaveEventRequest(
            "Natação", StartsAt: new DateTimeOffset(2026, 10, 20, 17, 0, 0, TimeSpan.Zero),
            EndsAt: new DateTimeOffset(2026, 10, 20, 18, 0, 0, TimeSpan.Zero), Recurrence: "FREQ=WEEKLY;BYDAY=TU"));
        var changed = new List<Guid>();
        foreach (var day in new[] { "2026-10-27", "2026-11-03" })
        {
            var response = await admin.PutAsJsonAsync($"{url}/{series.Id}/occurrences/{day}?scope=this", new SaveEventRequest(
                "Natação (outra piscina)", StartsAt: DateTimeOffset.Parse($"{day}T18:00:00Z"), EndsAt: DateTimeOffset.Parse($"{day}T19:00:00Z")),
                TestJson.Options);
            changed.Add((await response.Content.ReadFromJsonAsync<CalendarEventDto>(TestJson.Options))!.Id);
        }

        await using var hub = await ConnectAsync(partnerId, partner, householdId);
        (await admin.DeleteAsync($"{url}/{series.Id}")).EnsureSuccessStatusCode();
        await hub.WaitForAsync(HouseholdEvents.CalendarEventsDeleted, 1);
        await Task.Delay(200);

        var ids = (Guid[])Assert.Single(hub.Received[HouseholdEvents.CalendarEventsDeleted]);
        Assert.Equal([.. changed.Append(series.Id).Order()], ids.Order());
        Assert.Equal(0, hub.Count(HouseholdEvents.CalendarEventDeleted));
    }

    [Fact]
    public async Task Points_changes_are_announced_when_a_chore_is_done_and_not_when_it_is_renamed()
    {
        var (admin, _, householdId) = await Fase1.HouseholdAsync(factory);
        var (partner, partnerId) = await Fase1.MemberAsync(factory, admin, householdId, "Pai", HouseholdRole.Adult);
        var chore = await Fase1.ChoreAsync(admin, householdId, new SaveChoreRequest("Lixo", Points: 5));

        await using var hub = await ConnectAsync(partnerId, partner, householdId);
        (await admin.PutAsJsonAsync($"/api/households/{householdId}/chores/{chore.Id}", new SaveChoreRequest("Lixo (rua)", Points: 5), TestJson.Options))
            .EnsureSuccessStatusCode();
        await hub.WaitForAsync(HouseholdEvents.ChoreUpserted, 1);
        await Task.Delay(200);
        var afterRename = hub.Count(HouseholdEvents.PointsChanged);
        (await Fase1.CompleteAsync(admin, householdId, chore.Id)).EnsureSuccessStatusCode();
        await hub.WaitForAsync(HouseholdEvents.PointsChanged, 1);

        Assert.Equal(0, afterRename);
        Assert.Equal(householdId, (Guid)Assert.Single(hub.Received[HouseholdEvents.PointsChanged]));
    }

    /// <summary>
    /// P14: as mesadas de todos vêm de duas consultas ao todo (antes, duas por pessoa) e dão o mesmo que descrever
    /// cada uma sozinha (a resposta do PUT), com tarefas feitas, um mês e um período pago à mistura.
    /// </summary>
    [Fact]
    public async Task All_allowances_at_once_are_the_same_as_each_one_on_its_own()
    {
        var (admin, _, householdId) = await Fase1.HouseholdAsync(factory);
        var url = $"/api/households/{householdId}/allowances";
        var children = new List<Guid>();
        foreach (var (name, period) in new[] { ("Rita", Kubico.Domain.Rewards.AllowancePeriod.Weekly), ("Rui", Kubico.Domain.Rewards.AllowancePeriod.Monthly), ("Ana", Kubico.Domain.Rewards.AllowancePeriod.Weekly) })
        {
            var child = await ChildProfileTests.CreateChild(admin, householdId, name, "1234");
            children.Add(child.UserId);
            (await admin.PutAsJsonAsync($"{url}/{child.UserId}", new Kubico.Api.Rewards.SaveAllowanceRequest(500, 20, period), TestJson.Options))
                .EnsureSuccessStatusCode();
        }

        for (var i = 0; i < 5; i++)
        {
            var chore = await Fase1.ChoreAsync(admin, householdId, new SaveChoreRequest($"Tarefa {i}", Points: 2));
            (await Fase1.CompleteAsync(admin, householdId, chore.Id, new CompleteChoreRequest(DoneByUserId: children[i % 2])))
                .EnsureSuccessStatusCode();
        }

        var rita = await admin.GetFromJsonAsync<List<Kubico.Api.Rewards.AllowanceDto>>(url, TestJson.Options);
        var current = rita!.Single(a => a.UserId == children[0]).Periods[0].Start;
        (await admin.PostAsJsonAsync($"{url}/{children[0]}/payments", new Kubico.Api.Rewards.PayAllowanceRequest(current), TestJson.Options))
            .EnsureSuccessStatusCode();

        var all = (await admin.GetFromJsonAsync<List<Kubico.Api.Rewards.AllowanceDto>>(url, TestJson.Options))!;
        foreach (var (child, index) in children.Select((c, i) => (c, i)))
        {
            var period = index == 1 ? Kubico.Domain.Rewards.AllowancePeriod.Monthly : Kubico.Domain.Rewards.AllowancePeriod.Weekly;
            var alone = await (await admin.PutAsJsonAsync($"{url}/{child}", new Kubico.Api.Rewards.SaveAllowanceRequest(500, 20, period), TestJson.Options))
                .Content.ReadFromJsonAsync<Kubico.Api.Rewards.AllowanceDto>(TestJson.Options);
            var together = all.Single(a => a.UserId == child);
            Assert.Equal(System.Text.Json.JsonSerializer.Serialize(alone, TestJson.Options), System.Text.Json.JsonSerializer.Serialize(together, TestJson.Options));
        }

        Assert.Equal(3, all.Count);
        Assert.Equal(3, all.Single(a => a.UserId == children[0]).Periods[0].ChoresDone);
        Assert.NotNull(all.Single(a => a.UserId == children[0]).Periods[0].PaymentId);
    }

    [Fact]
    public async Task Browsers_keep_the_answer_to_the_cors_preflight_for_two_hours()
    {
        using var app = factory.WithWebHostBuilder(b => b.UseSetting("Cors:AllowedOrigins:0", "http://localhost:*"));
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/me");
        request.Headers.Add("Origin", "http://localhost:8081");
        request.Headers.Add("Access-Control-Request-Method", "PATCH");
        request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var response = await app.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("7200", response.Headers.GetValues("Access-Control-Max-Age").Single());
    }

    [Fact]
    public async Task The_account_export_finds_what_each_person_added_through_indexes()
    {
        using var scope = factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<KubicoDbContext>().Model;

        bool Indexed<T>(string property) => model.FindEntityType(typeof(T))!.GetIndexes()
            .Any(i => i.Properties[0].Name == property);

        Assert.True(Indexed<ShoppingItem>(nameof(ShoppingItem.AddedByUserId)));
        Assert.True(Indexed<CalendarEvent>(nameof(CalendarEvent.CreatedByUserId)));
        Assert.True(Indexed<Kubico.Domain.Rewards.RewardRedemption>("UserId"));
        Assert.True(Indexed<Kubico.Domain.Rewards.AllowancePayment>("UserId"));
        Assert.True(Indexed<Kubico.Domain.Rewards.Allowance>("UserId"));
        await Task.CompletedTask;
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client, HttpMethod method, string url, string? acceptEncoding, HttpContent? content = null,
        EntityTagHeaderValue? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (acceptEncoding is not null)
        {
            request.Headers.Add("Accept-Encoding", acceptEncoding);
        }

        if (ifNoneMatch is not null)
        {
            request.Headers.IfNoneMatch.Add(ifNoneMatch);
        }

        return await client.SendAsync(request);
    }

    private async Task<HubRecorder> ConnectAsync(Guid userId, HttpClient client, Guid householdId)
    {
        var token = client.DefaultRequestHeaders.Authorization!.Parameter!;
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, HouseholdHub.Path), options =>
            {
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions = TestJson.Options)
            .Build();
        var recorder = new HubRecorder(connection);
        await connection.StartAsync();
        await connection.InvokeAsync("JoinHousehold", householdId);
        _ = userId;
        return recorder;
    }

    /// <summary>Guarda as mensagens de tempo real que chegam a um aparelho.</summary>
    private sealed class HubRecorder : IAsyncDisposable
    {
        private readonly HubConnection _connection;

        public HubRecorder(HubConnection connection)
        {
            _connection = connection;
            connection.On<Guid, Guid>(HouseholdEvents.ShoppingItemDeleted, (list, item) => Add(HouseholdEvents.ShoppingItemDeleted, (list, item)));
            connection.On<Guid, Guid[]>(HouseholdEvents.ShoppingItemsDeleted, (list, items) => Add(HouseholdEvents.ShoppingItemsDeleted, (list, items)));
            connection.On<ShoppingListDto>(HouseholdEvents.ShoppingListUpserted, list => Add(HouseholdEvents.ShoppingListUpserted, list));
            connection.On<ShoppingItemDto>(HouseholdEvents.ShoppingItemUpserted, item => Add(HouseholdEvents.ShoppingItemUpserted, item));
            connection.On<Guid>(HouseholdEvents.CalendarEventDeleted, id => Add(HouseholdEvents.CalendarEventDeleted, id));
            connection.On<Guid[]>(HouseholdEvents.CalendarEventsDeleted, ids => Add(HouseholdEvents.CalendarEventsDeleted, ids));
            connection.On<CalendarEventDto>(HouseholdEvents.CalendarEventUpserted, e => Add(HouseholdEvents.CalendarEventUpserted, e));
            connection.On<ChoreDto>(HouseholdEvents.ChoreUpserted, c => Add(HouseholdEvents.ChoreUpserted, c));
            connection.On<Guid>(HouseholdEvents.PointsChanged, id => Add(HouseholdEvents.PointsChanged, id));
        }

        public ConcurrentQueue<(string Name, object Payload)> Messages { get; private set; } = new();

        public Dictionary<string, List<object>> Received =>
            Messages.GroupBy(m => m.Name).ToDictionary(g => g.Key, g => g.Select(m => m.Payload).ToList());

        public int Count(string name) => Messages.Count(m => m.Name == name);

        public void Clear() => Messages = new();

        public async Task WaitForAsync(string name, int count)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Count(name) < count)
            {
                Assert.True(DateTime.UtcNow < deadline, $"não chegaram {count} × {name}");
                await Task.Delay(20);
            }
        }

        public ValueTask DisposeAsync() => _connection.DisposeAsync();

        private void Add(string name, object payload) => Messages.Enqueue((name, payload));
    }
}

/// <summary>
/// P13: as limpezas deixaram de depender de alguém abrir um ecrã. Servidor próprio: o teste avança o relógio.
/// </summary>
public sealed class MaintenanceTests : IDisposable
{
    private readonly KubicoApiFactory _factory = new();

    [Fact]
    public async Task The_daily_cleanup_removes_old_history_notices_sessions_and_photos_and_reading_deletes_nothing()
    {
        var (admin, _, householdId) = await Fase1.HouseholdAsync(_factory);
        var (partner, partnerId) = await Fase1.MemberAsync(_factory, admin, householdId, "Pai", HouseholdRole.Adult);
        await Fase1.ChoreAsync(admin, householdId, new SaveChoreRequest("Lixo", AssigneeUserId: partnerId));
        await PhotoProofTests.Upload(admin, householdId, PhotoProofTests.Jpeg);
        var historyBefore = await Count(db => db.ActivityEntries.Count(a => a.HouseholdId == householdId));
        var noticesBefore = await Count(db => db.Notifications.Count(n => n.UserId == partnerId));

        _factory.Clock.Advance(TimeSpan.FromDays(31));
        var shown = await admin.GetFromJsonAsync<List<Kubico.Api.Activity.ActivityDto>>($"/api/households/{householdId}/activity", TestJson.Options);
        var historyAfterReading = await Count(db => db.ActivityEntries.Count(a => a.HouseholdId == householdId));
        var first = await RunAsync();
        var historyAfterCleanup = await Count(db => db.ActivityEntries.Count(a => a.HouseholdId == householdId));

        _factory.Clock.Advance(TimeSpan.FromDays(60));
        var second = await RunAsync();

        Assert.True(historyBefore > 0 && noticesBefore > 0);
        Assert.Empty(shown!);
        Assert.Equal(historyBefore, historyAfterReading);
        Assert.Equal(0, historyAfterCleanup);
        Assert.True(first.Activity >= historyBefore);
        Assert.Equal(1, first.Photos);
        Assert.Equal(0, first.Notifications);
        Assert.True(second.Notifications >= noticesBefore);
        Assert.True(second.RefreshTokens >= 2, "as sessões da mãe e do pai expiraram (60 dias)");
        Assert.Equal(0, await Count(db => db.Notifications.Count(n => n.UserId == partnerId)));
        _ = partner;
    }

    [Fact]
    public async Task Series_saved_before_the_end_column_get_it_filled_in()
    {
        var (admin, _, householdId) = await Fase1.HouseholdAsync(_factory);
        var url = $"/api/households/{householdId}/events";
        var ended = await NotificationTests.Post<CalendarEventDto>(admin, url, new SaveEventRequest(
            "Ginásio", StartsAt: new DateTimeOffset(2025, 1, 6, 18, 0, 0, TimeSpan.Zero),
            EndsAt: new DateTimeOffset(2025, 1, 6, 19, 0, 0, TimeSpan.Zero), Recurrence: "FREQ=WEEKLY;UNTIL=20250301"));
        var endless = await NotificationTests.Post<CalendarEventDto>(admin, url, new SaveEventRequest(
            "Natação", StartsAt: new DateTimeOffset(2025, 1, 7, 18, 0, 0, TimeSpan.Zero),
            EndsAt: new DateTimeOffset(2025, 1, 7, 19, 0, 0, TimeSpan.Zero), Recurrence: "FREQ=WEEKLY"));
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<KubicoDbContext>().CalendarEvents
                .Where(e => e.HouseholdId == householdId)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.RecurrenceEndDate, (DateOnly?)null));
        }

        var report = await RunAsync();
        var ends = await Query(db => db.CalendarEvents.Where(e => e.HouseholdId == householdId).ToDictionary(e => e.Id, e => e.RecurrenceEndDate));
        var calendar = await admin.GetFromJsonAsync<CalendarDto>($"{url}?from=2025-02-24&to=2025-03-09", TestJson.Options);

        Assert.Equal(2, report.RecurrenceEnds);
        Assert.Equal(new DateOnly(2025, 3, 5), ends[ended.Id]);
        Assert.Equal(DateOnly.MaxValue, ends[endless.Id]);
        Assert.Contains(calendar!.Occurrences, o => o.EventId == ended.Id && o.OccurrenceDate == new DateOnly(2025, 2, 24));
    }

    private async Task<MaintenanceReport> RunAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MaintenanceService>().RunAsync(default);
    }

    private Task<int> Count(Func<KubicoDbContext, int> query) => Query(query);

    private async Task<T> Query<T>(Func<KubicoDbContext, T> query)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return query(scope.ServiceProvider.GetRequiredService<KubicoDbContext>());
    }

    public void Dispose() => _factory.Dispose();
}

/// <summary>P7: o token de acesso dos calendários ligados guarda-se em memória até perto de expirar.</summary>
public class CalendarAccessTokensTests
{
    [Fact]
    public void Tokens_are_reused_until_close_to_expiring_and_only_with_the_same_refresh_token()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 10, 0, 0, TimeSpan.Zero));
        var tokens = new CalendarAccessTokens(clock);
        var connection = Guid.NewGuid();

        tokens.Set(connection, "refresh-1", "access-1", TimeSpan.FromHours(1));
        var reused = tokens.Get(connection, "refresh-1");
        var otherRefresh = tokens.Get(connection, "refresh-2");
        clock.Advance(TimeSpan.FromMinutes(59));
        var tooLate = tokens.Get(connection, "refresh-1");
        tokens.Set(connection, "refresh-1", "access-2", null);
        var withoutLifetime = tokens.Get(connection, "refresh-1");

        Assert.Equal("access-1", reused);
        Assert.Null(otherRefresh);
        Assert.Null(tooLate);
        Assert.Null(withoutLifetime);
        Assert.False(tokens.Forget(connection));
    }
}
