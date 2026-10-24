using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Rewards;
using Kubico.Api.Shopping;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;
using Kubico.Domain.Users;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>
/// Pedidos repetidos e em simultâneo (T1–T4, T11, T12, A7): concluir, trocar pontos, pagar, PINs e códigos, renovar a
/// sessão, aceitar convites. Os pedidos em paralelo vão mesmo ao mesmo tempo ao servidor (e à base SQLite); em
/// PostgreSQL a mesma solução serve (versões no WHERE, UPDATE condicionais e índices únicos).
/// </summary>
public class ConcurrencyTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task A_repeating_chore_is_done_once_per_occurrence_and_later_ones_cannot_be_done_ahead()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, rita) = await Fase1.MemberAsync(factory, admin, household, "Rita", HouseholdRole.Child);
        var today = factory.Today;
        var daily = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest(
            "Fazer a cama", Points: 10, DueDate: today, Recurrence: ChoreRecurrence.Daily, AssigneeUserId: rita));
        var weekly = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest(
            "Regar as plantas", Points: 5, DueDate: today.AddDays(3), Recurrence: ChoreRecurrence.Weekly, AssigneeUserId: rita));

        var first = await Fase1.CompleteAsync(child, household, daily.Id);
        var again = await Fase1.CompleteAsync(child, household, daily.Id);
        var problem = await Fase1.ProblemAsync(again);
        var early = await Fase1.CompleteAsync(child, household, weekly.Id);
        var earlyAgain = await Fase1.CompleteAsync(child, household, weekly.Id);
        var points = await Fase1.PointsAsync(admin, household, rita);

        var done = (await first.Content.ReadFromJsonAsync<ChoreCompletedDto>(TestJson.Options))!;
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal((today.AddDays(1), today.AddDays(1)), (done.Chore.DueDate, done.Chore.AvailableFrom));
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal($"Esta tarefa já está feita. Volta a estar disponível a {today.AddDays(1):dd/MM}.", problem.Title);

        // Uma semanal faz-se em qualquer dia da sua semana (antes do prazo), mas só uma vez.
        Assert.Equal(today.AddDays(3 - 6), weekly.AvailableFrom);
        Assert.Equal(HttpStatusCode.OK, early.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, earlyAgain.StatusCode);
        Assert.Equal(15, points.Earned);
    }

    [Fact]
    public async Task The_same_chore_completed_at_the_same_time_counts_once()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, rita) = await Fase1.MemberAsync(factory, admin, household, "Bruno", HouseholdRole.Child);
        var once = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Arrumar a mochila", Points: 7, AssigneeUserId: rita));
        var daily = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest(
            "Pôr a mesa", Points: 3, DueDate: factory.Today, Recurrence: ChoreRecurrence.Daily, AssigneeUserId: rita));

        var onceResults = await Fase1.ParallelAsync(6, _ => Fase1.CompleteAsync(child, household, once.Id));
        var dailyResults = await Fase1.ParallelAsync(6, _ => Fase1.CompleteAsync(child, household, daily.Id));
        var history = await admin.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"/api/households/{household}/chores/history", TestJson.Options);
        var points = await Fase1.PointsAsync(admin, household, rita);

        Assert.Equal(1, onceResults.Count(s => s == HttpStatusCode.OK));
        Assert.All(onceResults.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.Conflict, s));
        Assert.Equal(1, dailyResults.Count(s => s == HttpStatusCode.OK));
        Assert.All(dailyResults.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.Conflict, s));
        Assert.Equal(2, history!.Count);
        Assert.Equal(10, points.Earned);
    }

    [Fact]
    public async Task Reward_requests_at_the_same_time_never_spend_more_than_the_balance()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, rita) = await Fase1.MemberAsync(factory, admin, household, "Carla", HouseholdRole.Child);
        var chore = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Lavar o carro", Points: 10, AssigneeUserId: rita));
        (await Fase1.CompleteAsync(child, household, chore.Id)).EnsureSuccessStatusCode();
        var reward = await NotificationTests.Post<RewardDto>(admin, $"/api/households/{household}/rewards", new SaveRewardRequest("Cinema", Cost: 10));

        var results = await Fase1.ParallelAsync(6, _ => child.PostAsync($"/api/households/{household}/rewards/{reward.Id}/redeem", null));
        var balance = await Fase1.PointsAsync(admin, household, rita);

        Assert.Equal(1, results.Count(s => s == HttpStatusCode.Created));
        Assert.All(results.Where(s => s != HttpStatusCode.Created), s => Assert.Equal(HttpStatusCode.Conflict, s));
        Assert.Equal((10, 0, 10, 0), (balance.Earned, balance.Spent, balance.Reserved, balance.Available));
    }

    [Fact]
    public async Task Approving_does_not_leave_the_balance_negative()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (child, rita) = await Fase1.MemberAsync(factory, admin, household, "Rita", HouseholdRole.Child);
        var chore = await Fase1.ChoreAsync(admin, household, new SaveChoreRequest("Aspirar", Points: 20, AssigneeUserId: rita));
        (await Fase1.CompleteAsync(child, household, chore.Id)).EnsureSuccessStatusCode();
        var reward = await NotificationTests.Post<RewardDto>(admin, $"/api/households/{household}/rewards", new SaveRewardRequest("Gelado", Cost: 20));
        var request = await NotificationTests.Post<RedemptionDto>(child, $"/api/households/{household}/rewards/{reward.Id}/redeem", null);

        // A tarefa afinal não foi feita: a mãe não a aceita, mesmo com os pontos reservados (confirma com force).
        var history = await admin.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"/api/households/{household}/chores/history", TestJson.Options);
        var reject = await admin.PostAsync($"/api/households/{household}/chores/completions/{history![0].Id}/reject?force=true", null);
        var approve = await admin.PostAsync($"/api/households/{household}/redemptions/{request.Id}/approve", null);

        Assert.Equal(HttpStatusCode.OK, reject.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Equal("Os pontos de Rita já não chegam para este pedido.", (await Fase1.ProblemAsync(approve)).Title);
    }

    [Fact]
    public async Task Wrong_pins_sent_together_check_at_most_five_and_lock_the_profile()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var dani = await ChildProfileTests.CreateChild(admin, household, "Dani", "8642");
        var code = await ChildProfileTests.FamilyCode(admin, household);
        var anonymous = factory.CreateClient();

        var results = await Fase1.ParallelAsync(20, i =>
            anonymous.PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, dani.UserId, $"{1000 + i}")));
        var right = await anonymous.PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, dani.UserId, "8642"));

        Assert.True(results.Count(s => s == HttpStatusCode.Unauthorized) <= User.MaxPinAttempts, string.Join(",", results));
        Assert.All(results, s => Assert.Contains(s, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.TooManyRequests }));
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
    }

    [Fact]
    public async Task Wrong_two_step_codes_sent_together_check_at_most_five_and_turning_it_off_also_locks()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (client, _) = await factory.CreateUserAsync("Pai", email);
        var setup = await NotificationTests.Post<TwoFactorSetupDto>(client, "/api/account/2fa/setup", new TwoFactorSetupRequest("palavra-passe-segura"));
        var secret = Totp.FromBase32(setup.Secret);
        var step = Totp.StepAt(factory.Clock.GetUtcNow());
        await NotificationTests.Post<TwoFactorEnabledDto>(client, "/api/account/2fa/enable", new TwoFactorCodeRequest(Totp.Code(secret, step)));
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "palavra-passe-segura"));
        var challenge = (await login.Content.ReadFromJsonAsync<TwoFactorChallengeDto>(TestJson.Options))!.ChallengeToken;

        var results = await Fase1.ParallelAsync(20, i => factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login/2fa", new TwoFactorLoginRequest(challenge, $"{100000 + i}"), TestJson.Options));
        var right = await factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login/2fa", new TwoFactorLoginRequest(challenge, Totp.Code(secret, step + 1)), TestJson.Options);
        var disable = await client.PostAsJsonAsync("/api/account/2fa/disable",
            new DisableTwoFactorRequest("palavra-passe-segura", Totp.Code(secret, step + 1)), TestJson.Options);

        Assert.True(results.Count(s => s == HttpStatusCode.Unauthorized) <= User.MaxPinAttempts, string.Join(",", results));
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, disable.StatusCode);
    }

    [Fact]
    public async Task The_same_code_used_twice_at_the_same_time_opens_one_session()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (client, _) = await factory.CreateUserAsync("Avó", email);
        var setup = await NotificationTests.Post<TwoFactorSetupDto>(client, "/api/account/2fa/setup", new TwoFactorSetupRequest("palavra-passe-segura"));
        var secret = Totp.FromBase32(setup.Secret);
        var step = Totp.StepAt(factory.Clock.GetUtcNow());
        var enabled = await NotificationTests.Post<TwoFactorEnabledDto>(client, "/api/account/2fa/enable", new TwoFactorCodeRequest(Totp.Code(secret, step)));
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, "palavra-passe-segura"));
        var challenge = (await login.Content.ReadFromJsonAsync<TwoFactorChallengeDto>(TestJson.Options))!.ChallengeToken;

        var results = await Fase1.ParallelAsync(4, _ => factory.CreateClient()
            .PostAsJsonAsync("/api/auth/login/2fa", new TwoFactorLoginRequest(challenge, enabled.RecoveryCodes[0]), TestJson.Options));

        Assert.Equal(1, results.Count(s => s == HttpStatusCode.OK));
    }

    [Fact]
    public async Task A_refresh_already_on_its_way_when_the_token_was_replaced_gets_a_new_pair_but_a_later_reuse_ends_the_session()
    {
        var (_, auth) = await factory.CreateUserAsync();
        using var scope = factory.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<SessionService>();
        var now = factory.Clock.GetUtcNow();

        // Duas abas renovam com o mesmo token: a segunda chegou antes de a primeira o trocar.
        var first = await sessions.RefreshAsync(auth.RefreshToken, now, default);
        var concurrent = await sessions.RefreshAsync(auth.RefreshToken, now - TimeSpan.FromMilliseconds(1), default);
        var firstStillWorks = await sessions.RefreshAsync(first!.RefreshToken, now, default);

        // Muito depois, o token antigo volta a aparecer: é roubo, a sessão do dispositivo termina.
        var reuse = await sessions.RefreshAsync(auth.RefreshToken, now, default);
        var afterTheft = await sessions.RefreshAsync(concurrent!.RefreshToken, now, default);

        Assert.NotNull(firstStillWorks);
        Assert.NotEqual(first.RefreshToken, concurrent.RefreshToken);
        Assert.Null(reuse);
        Assert.Null(afterTheft);
    }

    [Fact]
    public async Task Paying_the_same_period_or_sending_the_same_item_at_the_same_time_answers_without_errors()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (_, rita) = await Fase1.MemberAsync(factory, admin, household, "Rita", HouseholdRole.Child);
        var url = $"/api/households/{household}/allowances/{rita}";
        await admin.PutAsJsonAsync(url, new SaveAllowanceRequest(500, 10), TestJson.Options);
        var week = Allowance.PeriodStart(factory.Today, AllowancePeriod.Weekly);
        var list = await NotificationTests.Post<ShoppingListDto>(admin, $"/api/households/{household}/lists", new CreateShoppingListRequest("Supermercado"));
        var itemId = Guid.NewGuid();

        var payments = await Fase1.ParallelAsync(4, _ => admin.PostAsJsonAsync($"{url}/payments", new PayAllowanceRequest(week), TestJson.Options));
        var items = await Fase1.ParallelAsync(4, _ => admin.PostAsJsonAsync(
            $"/api/households/{household}/lists/{list.Id}/items", new AddShoppingItemRequest("Iogurtes", Id: itemId), TestJson.Options));
        var allowance = (await admin.GetFromJsonAsync<List<AllowanceDto>>($"/api/households/{household}/allowances", TestJson.Options))!.Single();
        var stored = await admin.GetFromJsonAsync<List<ShoppingItemDto>>($"/api/households/{household}/lists/{list.Id}/items", TestJson.Options);

        Assert.Equal(1, payments.Count(s => s == HttpStatusCode.OK));
        Assert.All(payments.Where(s => s != HttpStatusCode.OK), s => Assert.Equal(HttpStatusCode.Conflict, s));
        Assert.NotNull(allowance.Periods[0].PaymentId);
        Assert.All(items, s => Assert.Contains(s, new[] { HttpStatusCode.Created, HttpStatusCode.OK }));
        Assert.Equal(1, items.Count(s => s == HttpStatusCode.Created));
        Assert.Single(stored!);
    }

    [Fact]
    public async Task A_single_use_invite_accepted_by_several_people_at_once_lets_one_in()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var invite = await HouseholdTests.CreateInvite(admin, household, HouseholdRole.Adult);
        var people = new List<HttpClient>();
        for (var i = 0; i < 5; i++)
        {
            people.Add((await factory.CreateUserAsync($"Pessoa {i}")).Client);
        }

        var results = await Fase1.ParallelAsync(5, i => people[i].PostAsync($"/api/invites/{invite.Code}/accept", null));
        var members = await MemberTests.Members(admin, household);

        Assert.Equal(1, results.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(2, members.Count);
        Assert.Equal(1, invite.MaxUses);
    }
}
