using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;
using Kubico.Domain.Shopping;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>Compras inteligentes (F4-22): sugestões, ordem do supermercado e artigos recorrentes.</summary>
public sealed class SmartShoppingTests : IDisposable
{
    private readonly KubicoApiFactory _factory = new();

    [Fact]
    public async Task What_is_bought_regularly_comes_back_as_a_suggestion_once_it_is_due()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var list = await NotificationTests.Post<ShoppingListDto>(admin, $"/api/households/{household}/lists", new CreateShoppingListRequest("Supermercado"));
        var items = $"/api/households/{household}/lists/{list.Id}/items";

        // Leite comprado de 7 em 7 dias, três vezes.
        for (var week = 0; week < 3; week++)
        {
            var milk = await NotificationTests.Post<ShoppingItemDto>(admin, items, new AddShoppingItemRequest(week == 1 ? "leite " : "Leite"));
            (await admin.PatchAsJsonAsync($"{items}/{milk.Id}", new UpdateShoppingItemRequest(IsChecked: true), TestJson.Options))
                .EnsureSuccessStatusCode();
            (await admin.PostAsync($"{items}/clear-checked", null)).EnsureSuccessStatusCode();
            _factory.Clock.Advance(TimeSpan.FromDays(7));
        }

        var due = await admin.GetFromJsonAsync<ShoppingInsightsDto>($"/api/households/{household}/lists/{list.Id}/insights", TestJson.Options);
        await NotificationTests.Post<ShoppingItemDto>(admin, items, new AddShoppingItemRequest("Leite"));
        var alreadyOnTheList = await admin.GetFromJsonAsync<ShoppingInsightsDto>($"/api/households/{household}/lists/{list.Id}/insights", TestJson.Options);

        var suggestion = Assert.Single(due!.Suggestions);
        Assert.Equal("Leite", suggestion.Name);
        Assert.Equal(7, suggestion.EveryDays);
        Assert.Empty(alreadyOnTheList!.Suggestions);
    }

    [Fact]
    public async Task Unchecking_an_item_forgets_the_purchase_and_other_households_see_nothing()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var (stranger, _, _) = await Fase1.HouseholdAsync(_factory);
        var list = await NotificationTests.Post<ShoppingListDto>(admin, $"/api/households/{household}/lists", new CreateShoppingListRequest("Supermercado"));
        var items = $"/api/households/{household}/lists/{list.Id}/items";
        var bread = await NotificationTests.Post<ShoppingItemDto>(admin, items, new AddShoppingItemRequest("Pão"));

        (await admin.PatchAsJsonAsync($"{items}/{bread.Id}", new UpdateShoppingItemRequest(IsChecked: true), TestJson.Options)).EnsureSuccessStatusCode();
        var bought = await CountPurchasesAsync();
        (await admin.PatchAsJsonAsync($"{items}/{bread.Id}", new UpdateShoppingItemRequest(IsChecked: false), TestJson.Options)).EnsureSuccessStatusCode();
        var forgotten = await CountPurchasesAsync();
        var strangerInsights = await stranger.GetAsync($"/api/households/{household}/lists/{list.Id}/insights");
        var strangerRecurring = await stranger.PostAsJsonAsync($"/api/households/{household}/lists/{list.Id}/recurring",
            new SaveRecurringItemRequest("Pão", 7), TestJson.Options);

        Assert.Equal(1, bought);
        Assert.Equal(0, forgotten);
        Assert.Equal(HttpStatusCode.NotFound, strangerInsights.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, strangerRecurring.StatusCode);
    }

    [Fact]
    public async Task A_recurring_item_goes_back_on_the_list_on_its_day_once_and_not_if_it_is_already_there()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(_factory);
        var list = await NotificationTests.Post<ShoppingListDto>(admin, $"/api/households/{household}/lists", new CreateShoppingListRequest("Supermercado"));
        var items = $"/api/households/{household}/lists/{list.Id}/items";
        var recurring = await NotificationTests.Post<RecurringItemDto>(admin, $"/api/households/{household}/lists/{list.Id}/recurring",
            new SaveRecurringItemRequest("Ovos", 7, Quantity: "12"));

        var before = await RunAsync();
        _factory.Clock.Advance(TimeSpan.FromDays(7).Add(TimeSpan.FromHours(10)));
        var runs = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => RunAsync()));
        var onTheList = await admin.GetFromJsonAsync<List<ShoppingItemDto>>(items, TestJson.Options);
        _factory.Clock.Advance(TimeSpan.FromDays(7));
        var stillPending = await RunAsync();
        var listed = await admin.GetFromJsonAsync<List<RecurringItemDto>>($"/api/households/{household}/lists/{list.Id}/recurring", TestJson.Options);

        Assert.Equal(0, before);
        Assert.Equal(1, runs.Sum());
        var eggs = Assert.Single(onTheList!);
        Assert.Equal(("Ovos", "12"), (eggs.Name, eggs.Quantity));
        Assert.Equal(0, stillPending);
        Assert.Equal(recurring.NextDate.AddDays(14), Assert.Single(listed!).NextDate);
    }

    [Fact]
    public void The_category_order_follows_the_order_items_are_picked_up()
    {
        var day = new DateOnly(2026, 11, 1);
        var user = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);
        (PurchaseRecord, DateOnly) Bought(string name, string category, int minutes, int dayOffset = 0) => (new PurchaseRecord
        {
            ItemId = Guid.NewGuid(), Name = name, NameKey = ShoppingInsights.Key(name), Category = category, UserId = user,
            PurchasedAt = t0.AddDays(dayOffset).AddMinutes(minutes),
        }, day.AddDays(dayOffset));

        var order = ShoppingInsights.CategoryOrder([
            Bought("Maçãs", "Fruta", 0), Bought("Pão", "Padaria", 5), Bought("Leite", "Laticínios", 10),
            Bought("Peras", "Fruta", 0, 3), Bought("Iogurte", "Laticínios", 4, 3), Bought("Broa", "Padaria", 9, 3),
        ]);

        Assert.Equal(["Fruta", "Padaria", "Laticínios"], order);
    }

    private async Task<int> RunAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RecurringItemsService>().RunAsync(default);
    }

    private async Task<int> CountPurchasesAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<Infrastructure.Persistence.KubicoDbContext>().PurchaseRecords.Count();
    }

    public void Dispose() => _factory.Dispose();
}
