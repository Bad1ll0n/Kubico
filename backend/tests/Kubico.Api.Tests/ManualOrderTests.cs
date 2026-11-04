using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>Arrastar para reordenar as listas e os artigos (a ordem fica no servidor e chega a todos).</summary>
public class ManualOrderTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Lists_keep_the_order_chosen_by_who_manages_shopping_and_new_ones_go_last()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        var (guest, _) = await Fase1.MemberAsync(factory, mum, household, "Rui", HouseholdRole.Guest);
        var url = $"/api/households/{household}/lists";
        var a = await NotificationTests.Post<ShoppingListDto>(mum, url, new CreateShoppingListRequest("Aldi"));
        var b = await NotificationTests.Post<ShoppingListDto>(mum, url, new CreateShoppingListRequest("Bricolage"));
        var c = await NotificationTests.Post<ShoppingListDto>(mum, url, new CreateShoppingListRequest("Continente"));

        var guestTries = await guest.PutAsJsonAsync($"{url}/order", new ReorderRequest([c.Id, a.Id]), TestJson.Options);
        (await mum.PutAsJsonAsync($"{url}/order", new ReorderRequest([c.Id, a.Id, b.Id, Guid.NewGuid()]), TestJson.Options))
            .EnsureSuccessStatusCode();
        var d = await NotificationTests.Post<ShoppingListDto>(mum, url, new CreateShoppingListRequest("Drogaria"));
        var lists = await mum.GetFromJsonAsync<List<ShoppingListDto>>(url, TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, guestTries.StatusCode);
        Assert.Equal([c.Id, a.Id, b.Id, d.Id], lists!.Select(l => l.Id));
        Assert.Equal(3, d.Position);
    }

    [Fact]
    public async Task Items_follow_the_dragged_order_inside_their_category_and_other_households_cannot_touch_them()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        var (guest, _) = await Fase1.MemberAsync(factory, mum, household, "Rui", HouseholdRole.Guest);
        var (stranger, _, strangerHousehold) = await Fase1.HouseholdAsync(factory);
        var list = await NotificationTests.Post<ShoppingListDto>(mum, $"/api/households/{household}/lists", new CreateShoppingListRequest("Super"));
        var other = await NotificationTests.Post<ShoppingListDto>(mum, $"/api/households/{household}/lists", new CreateShoppingListRequest("Outra"));
        var items = $"/api/households/{household}/lists/{list.Id}/items";
        var milk = await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Leite", Category: "Lacticínios"));
        var yogurt = await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Iogurtes", Category: "Lacticínios"));
        var cheese = await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Queijo", Category: "Lacticínios"));
        var apples = await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Maçãs", Category: "Fruta"));
        var elsewhere = await NotificationTests.Post<ShoppingItemDto>(mum, $"/api/households/{household}/lists/{other.Id}/items", new AddShoppingItemRequest("Pilhas"));

        // Quem usa as Compras (um convidado) também arruma a lista; o artigo de outra lista não se mexe.
        (await guest.PutAsJsonAsync($"{items}/order", new ReorderRequest([cheese.Id, milk.Id, elsewhere.Id]), TestJson.Options))
            .EnsureSuccessStatusCode();
        var stranger1 = await stranger.PutAsJsonAsync($"{items}/order", new ReorderRequest([yogurt.Id]), TestJson.Options);
        var stranger2 = await stranger.PutAsJsonAsync($"/api/households/{strangerHousehold}/lists/{list.Id}/items/order",
            new ReorderRequest([yogurt.Id]), TestJson.Options);
        var after = await mum.GetFromJsonAsync<List<ShoppingItemDto>>(items, TestJson.Options);
        var pilhas = await mum.GetFromJsonAsync<List<ShoppingItemDto>>($"/api/households/{household}/lists/{other.Id}/items", TestJson.Options);

        Assert.Equal([apples.Id, cheese.Id, milk.Id, yogurt.Id], after!.Select(i => i.Id));
        Assert.Equal(0, after.Single(i => i.Id == cheese.Id).Position);
        Assert.Null(after.Single(i => i.Id == yogurt.Id).Position);
        Assert.Null(Assert.Single(pilhas!).Position);
        Assert.Equal(HttpStatusCode.NotFound, stranger1.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, stranger2.StatusCode);
    }
}
