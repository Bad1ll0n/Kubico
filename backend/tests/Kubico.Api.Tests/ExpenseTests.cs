using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Expenses;
using Kubico.Domain.Expenses;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>Despesas partilhadas (F4-10): divisão, saldos, acertos, permissões e isolamento entre casas.</summary>
public class ExpenseTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Shared_expenses_give_balances_that_add_to_zero_and_a_short_way_to_settle()
    {
        var (mum, auth, household) = await Fase1.HouseholdAsync(factory);
        var (dad, dadId) = await Fase1.MemberAsync(factory, mum, household, "Pai", HouseholdRole.Adult);
        var (guest, guestId) = await Fase1.MemberAsync(factory, mum, household, "Rui", HouseholdRole.Guest);
        var mumId = auth.User.Id;
        var url = $"/api/households/{household}/expenses";
        var today = factory.Today;

        // 90 € de supermercado pagos pela Mãe, entre os três; 30 € de gás pagos pelo Pai, entre ele e a Mãe.
        await NotificationTests.Post<ExpenseDto>(mum, url, new SaveExpenseRequest("Supermercado", 9000, mumId, today, SplitWith: [mumId, dadId, guestId]));
        await NotificationTests.Post<ExpenseDto>(dad, url, new SaveExpenseRequest("Gás", 3000, dadId, today, SplitWith: [mumId, dadId]));
        var odd = await NotificationTests.Post<ExpenseDto>(guest, url, new SaveExpenseRequest("Pão", 100, guestId, today, SplitWith: [mumId, dadId, guestId]));

        var overview = await mum.GetFromJsonAsync<ExpensesOverviewDto>(url, TestJson.Options);
        var net = overview!.Balances.ToDictionary(b => b.UserId, b => b.NetCents);

        Assert.Equal([34, 33, 33], odd.Shares.Select(s => s.AmountCents));
        Assert.Equal(0, net.Values.Sum());
        Assert.Equal(9000 - 3000 - 1500 - 34, net[mumId]);
        Assert.Equal(3000 - 3000 - 1500 - 33, net[dadId]);
        Assert.True(overview.Transfers.Count <= 2);

        // Depois de pagarem o que as transferências dizem, ninguém deve nada.
        foreach (var t in overview.Transfers)
        {
            (await mum.PostAsJsonAsync($"/api/households/{household}/settlements",
                new SaveSettlementRequest(t.FromUserId, t.ToUserId, t.AmountCents, today), TestJson.Options)).EnsureSuccessStatusCode();
        }

        var settled = await mum.GetFromJsonAsync<ExpensesOverviewDto>(url, TestJson.Options);
        Assert.Empty(settled!.Balances);
        Assert.Empty(settled.Transfers);
    }

    [Fact]
    public async Task Shares_must_add_up_and_be_household_members()
    {
        var (mum, auth, household) = await Fase1.HouseholdAsync(factory);
        var (_, strangerAuth, _) = await Fase1.HouseholdAsync(factory);
        var strangerId = strangerAuth.User.Id;
        var url = $"/api/households/{household}/expenses";
        var me = auth.User.Id;

        var wrongSum = await mum.PostAsJsonAsync(url, new SaveExpenseRequest("X", 1000, me, factory.Today, Shares: [new ShareDto(me, 900)]), TestJson.Options);
        var stranger = await mum.PostAsJsonAsync(url, new SaveExpenseRequest("X", 1000, me, factory.Today, SplitWith: [me, strangerId]), TestJson.Options);
        var exact = await mum.PostAsJsonAsync(url, new SaveExpenseRequest("X", 1000, me, factory.Today, Shares: [new ShareDto(me, 1000)]), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, wrongSum.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, stranger.StatusCode);
        Assert.Equal(HttpStatusCode.Created, exact.StatusCode);
    }

    [Fact]
    public async Task Children_do_not_see_expenses_guests_change_only_their_own_and_strangers_get_404()
    {
        var (mum, auth, household) = await Fase1.HouseholdAsync(factory);
        var (child, _, _) = await Fase1.ChildProfileAsync(factory, mum, household, "Rita");
        var (guest, guestId) = await Fase1.MemberAsync(factory, mum, household, "Rui", HouseholdRole.Guest);
        var (stranger, _, _) = await Fase1.HouseholdAsync(factory);
        var url = $"/api/households/{household}/expenses";
        var mine = await NotificationTests.Post<ExpenseDto>(mum, url, new SaveExpenseRequest("Luz", 5000, auth.User.Id, factory.Today));

        var childSees = await child.GetAsync(url);
        var guestDeletes = await guest.DeleteAsync($"{url}/{mine.Id}");
        var guestAdds = await guest.PostAsJsonAsync(url, new SaveExpenseRequest("Água", 2000, guestId, factory.Today), TestJson.Options);
        var strangerSees = await stranger.GetAsync(url);

        Assert.Equal(HttpStatusCode.Forbidden, childSees.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, guestDeletes.StatusCode);
        Assert.Equal(HttpStatusCode.Created, guestAdds.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerSees.StatusCode);
    }

    [Fact]
    public void Settling_needs_at_most_one_transfer_less_than_people_owing_or_owed()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var d = Guid.NewGuid();

        var transfers = ExpenseMath.Settle(new Dictionary<Guid, int> { [a] = 600, [b] = -200, [c] = -300, [d] = -100 });

        Assert.Equal(3, transfers.Count);
        Assert.All(transfers, t => Assert.Equal(a, t.ToUserId));
        Assert.Equal(600, transfers.Sum(t => t.AmountCents));
    }
}
