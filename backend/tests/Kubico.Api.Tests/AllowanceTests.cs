using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Chores;
using Kubico.Api.Rewards;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;

namespace Kubico.Api.Tests;

/// <summary>Mesada: um valor por semana ou por mês, mais um extra por tarefa feita; quem gere marca como paga.</summary>
public class AllowanceTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task The_allowance_grows_with_the_chores_done_and_is_paid_once_per_period()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        var rita = childAuth.User.Id;
        var url = $"/api/households/{household.Id}/allowances";
        var chores = $"/api/households/{household.Id}/chores";
        var week = Allowance.PeriodStart(factory.Today, AllowancePeriod.Weekly);

        var saved = await admin.PutAsJsonAsync($"{url}/{rita}", new SaveAllowanceRequest(500, 50), TestJson.Options);
        foreach (var title in new[] { "Fazer a cama", "Pôr a mesa" })
        {
            var chore = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest(title, AssigneeUserId: rita));
            await NotificationTests.Post<ChoreCompletedDto>(child, $"{chores}/{chore.Id}/complete", null);
        }

        var mine = await child.GetFromJsonAsync<List<AllowanceDto>>(url, TestJson.Options);
        var paid = await admin.PostAsJsonAsync($"{url}/{rita}/payments", new PayAllowanceRequest(week), TestJson.Options);
        var paidAgain = await admin.PostAsJsonAsync($"{url}/{rita}/payments", new PayAllowanceRequest(week), TestJson.Options);
        var after = (await paid.Content.ReadFromJsonAsync<AllowanceDto>(TestJson.Options))!;
        var inbox = await NotificationTests.Inbox(child);
        var undone = await admin.DeleteAsync($"{url}/{rita}/payments/{after.Periods[0].PaymentId}");
        var reopened = (await admin.GetFromJsonAsync<List<AllowanceDto>>(url, TestJson.Options))!.Single();

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var current = Assert.Single(Assert.Single(mine!).Periods);
        Assert.Equal((week, week.AddDays(6), 2, 600, true), (current.Start, current.End, current.ChoresDone, current.AmountCents, current.IsCurrent));
        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, paidAgain.StatusCode);
        Assert.NotNull(after.Periods[0].PaidAt);
        Assert.Contains(inbox.Items, n => n.Title == "Recebeste a mesada: 6,00 €" && n.Body == "Com 2 tarefas feitas.");
        Assert.Equal(HttpStatusCode.NoContent, undone.StatusCode);
        Assert.Null(reopened.Periods[0].PaidAt);
    }

    [Fact]
    public async Task Only_who_manages_rewards_sets_and_pays_allowances_and_each_member_sees_only_their_own()
    {
        var (admin, adminAuth) = await factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await factory.CreateUserAsync("Rita");
        var (brother, brotherAuth) = await factory.CreateUserAsync("Tomás");
        var (stranger, _) = await factory.CreateUserAsync("Vizinho");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        await NotificationTests.Join(admin, brother, household.Id, HouseholdRole.Child);
        var url = $"/api/households/{household.Id}/allowances";
        await admin.PutAsJsonAsync($"{url}/{childAuth.User.Id}", new SaveAllowanceRequest(500), TestJson.Options);
        await admin.PutAsJsonAsync($"{url}/{brotherAuth.User.Id}", new SaveAllowanceRequest(2000, Period: AllowancePeriod.Monthly), TestJson.Options);
        var week = Allowance.PeriodStart(factory.Today, AllowancePeriod.Weekly);

        var byChild = await child.PutAsJsonAsync($"{url}/{childAuth.User.Id}", new SaveAllowanceRequest(10_000), TestJson.Options);
        var childPays = await child.PostAsJsonAsync($"{url}/{childAuth.User.Id}/payments", new PayAllowanceRequest(week), TestJson.Options);
        var notMember = await admin.PutAsJsonAsync($"{url}/{Guid.NewGuid()}", new SaveAllowanceRequest(500), TestJson.Options);
        var strangerReads = await stranger.GetAsync(url);
        var notAPeriod = await admin.PostAsJsonAsync($"{url}/{childAuth.User.Id}/payments", new PayAllowanceRequest(week.AddDays(2)), TestJson.Options);
        var future = await admin.PostAsJsonAsync($"{url}/{childAuth.User.Id}/payments", new PayAllowanceRequest(week.AddDays(7)), TestJson.Options);
        var all = await admin.GetFromJsonAsync<List<AllowanceDto>>(url, TestJson.Options);
        var childSees = await child.GetFromJsonAsync<List<AllowanceDto>>(url, TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, byChild.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, childPays.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, notMember.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerReads.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAPeriod.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Equal(2, all!.Count);
        Assert.Equal(childAuth.User.Id, Assert.Single(childSees!).UserId);
        Assert.Equal(AllowancePeriod.Monthly, all.Single(a => a.UserId == brotherAuth.User.Id).Period);
        Assert.NotEqual(adminAuth.User.Id, childAuth.User.Id);
    }

    [Theory]
    [InlineData("2026-10-21", AllowancePeriod.Weekly, "2026-10-19", "2026-10-25")]
    [InlineData("2026-10-19", AllowancePeriod.Weekly, "2026-10-19", "2026-10-25")]
    [InlineData("2026-10-25", AllowancePeriod.Weekly, "2026-10-19", "2026-10-25")]
    [InlineData("2026-02-14", AllowancePeriod.Monthly, "2026-02-01", "2026-02-28")]
    public void Weeks_run_from_monday_to_sunday_and_months_from_the_first_to_the_last_day(
        string day, AllowancePeriod period, string start, string end)
    {
        var first = Allowance.PeriodStart(DateOnly.Parse(day), period);
        Assert.Equal((DateOnly.Parse(start), DateOnly.Parse(end)), (first, Allowance.PeriodEnd(first, period)));
    }

    [Fact]
    public void Amounts_are_written_as_euros() =>
        Assert.Equal(("6,20 €", "0,05 €", "12,00 €"), (Allowance.Format(620), Allowance.Format(5), Allowance.Format(1200)));
}

/// <summary>Os períodos que acabam ficam por pagar. Servidor próprio: o teste avança o relógio.</summary>
public sealed class AllowanceOwedTests : IDisposable
{
    private readonly KubicoApiFactory _factory = new();

    [Fact]
    public async Task A_finished_week_is_owed_and_rejected_chores_do_not_count()
    {
        var (admin, _) = await _factory.CreateUserAsync("Mãe");
        var (child, childAuth) = await _factory.CreateUserAsync("Rita");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, child, household.Id, HouseholdRole.Child);
        var rita = childAuth.User.Id;
        var url = $"/api/households/{household.Id}/allowances";
        var chores = $"/api/households/{household.Id}/chores";
        await admin.PutAsJsonAsync($"{url}/{rita}", new SaveAllowanceRequest(500, 100), TestJson.Options);
        var lastWeek = Allowance.PeriodStart(_factory.Today, AllowancePeriod.Weekly);
        for (var i = 0; i < 3; i++)
        {
            var chore = await NotificationTests.Post<ChoreDto>(admin, chores, new SaveChoreRequest($"Tarefa {i}", AssigneeUserId: rita));
            await NotificationTests.Post<ChoreCompletedDto>(child, $"{chores}/{chore.Id}/complete", null);
        }

        var history = await admin.GetFromJsonAsync<List<ChoreHistoryItemDto>>($"{chores}/history", TestJson.Options);
        await admin.PostAsync($"{chores}/completions/{history![0].Id}/reject", null);

        _factory.Clock.Advance(TimeSpan.FromDays(7));
        var allowance = (await admin.GetFromJsonAsync<List<AllowanceDto>>(url, TestJson.Options))!.Single();

        Assert.Equal(2, allowance.Periods.Count);
        var (current, finished) = (allowance.Periods[0], allowance.Periods[1]);
        Assert.Equal((true, 0, 500), (current.IsCurrent, current.ChoresDone, current.AmountCents));
        Assert.Equal((lastWeek, false, 2, 700), (finished.Start, finished.IsCurrent, finished.ChoresDone, finished.AmountCents));
        Assert.Equal(700, allowance.OwedCents);
    }

    public void Dispose() => _factory.Dispose();
}
