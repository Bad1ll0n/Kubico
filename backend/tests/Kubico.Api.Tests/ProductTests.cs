using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Households;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;
using Kubico.Domain.Shopping;
using Microsoft.AspNetCore.Mvc;

namespace Kubico.Api.Tests;

/// <summary>Ler códigos de barras: o produto vem da base pública, e a casa fica com o nome que lhe dá.</summary>
public class ProductTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task A_scanned_product_comes_from_the_public_catalog_and_is_only_asked_for_once()
    {
        var (client, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var barcode = FakeProductCatalog.Ean13("560000000001");
        factory.Products.Add(barcode, new ExternalProduct(
            "Leite meio-gordo", "Mimosa, Lactogal", "1 L", ["en:dairies", "en:milks"]));

        var first = await Product(client, household.Id, barcode);
        var again = await Product(client, household.Id, barcode);

        Assert.Equal(new ProductDto(barcode, "Leite meio-gordo Mimosa 1 L", "Laticínios", "Mimosa, Lactogal", "1 L", false), first);
        Assert.Equal(first, again);
        Assert.Equal(1, factory.Products.Lookups(barcode));
    }

    [Fact]
    public async Task An_unknown_product_takes_the_name_the_household_gives_it_and_only_that_household_sees_it()
    {
        var (client, _) = await factory.CreateUserAsync();
        var (neighbour, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var other = await HouseholdTests.CreateHousehold(neighbour, "Casa do lado");
        var list = await NotificationTests.Post<ShoppingListDto>(client, $"/api/households/{household.Id}/lists",
            new CreateShoppingListRequest("Supermercado"));
        var items = $"/api/households/{household.Id}/lists/{list.Id}/items";
        var barcode = FakeProductCatalog.Ean13("560000000002");

        var unknown = await client.GetAsync($"/api/households/{household.Id}/products/{barcode}");
        var problem = await unknown.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options);
        var unknownAgain = await client.GetAsync($"/api/households/{household.Id}/products/{barcode}");

        // Escreve o nome à mão e põe na lista: da próxima vez, o código já diz o que é.
        var item = await NotificationTests.Post<ShoppingItemDto>(client, items,
            new AddShoppingItemRequest("Bolachas da avó", Category: "Doces e snacks", Barcode: barcode));
        var learned = await Product(client, household.Id, barcode);
        var renamed = await client.PatchAsJsonAsync($"{items}/{item.Id}",
            new UpdateShoppingItemRequest(Name: "Bolachas de manteiga"), TestJson.Options);
        var relearned = await Product(client, household.Id, barcode);
        var elsewhere = await neighbour.GetAsync($"/api/households/{other.Id}/products/{barcode}");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("Não conhecemos este produto. Escreve o nome: fica guardado para a próxima vez.", problem!.Title);
        Assert.Equal(HttpStatusCode.NotFound, unknownAgain.StatusCode);
        Assert.Equal(1, factory.Products.Lookups(barcode));
        Assert.Equal(barcode, item.Barcode);
        Assert.Equal(new ProductDto(barcode, "Bolachas da avó", "Doces e snacks", null, null, true), learned);
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Bolachas de manteiga", relearned.Name);
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
    }

    [Fact]
    public async Task Invalid_codes_and_an_unreachable_catalog_get_clear_answers()
    {
        var (client, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(client, "Casa");
        var list = await NotificationTests.Post<ShoppingListDto>(client, $"/api/households/{household.Id}/lists",
            new CreateShoppingListRequest("Supermercado"));
        var products = $"/api/households/{household.Id}/products";
        var barcode = FakeProductCatalog.Ean13("560000000003");
        var wrongDigit = barcode[..^1] + (char)('0' + (barcode[^1] - '0' + 1) % 10);
        factory.Products.Add(barcode, new ExternalProduct("Arroz agulha", "Cigala", "1 kg", ["en:rices"]));
        factory.Products.Unreachable(barcode);

        var tooShort = await client.GetAsync($"{products}/123");
        var misread = await client.GetAsync($"{products}/{wrongDigit}");
        var itemWithBadCode = await client.PostAsJsonAsync($"/api/households/{household.Id}/lists/{list.Id}/items",
            new AddShoppingItemRequest("Arroz", Barcode: wrongDigit), TestJson.Options);
        var offline = await client.GetAsync($"{products}/{barcode}");
        var problem = await offline.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options);
        factory.Products.Reachable(barcode);
        var online = await Product(client, household.Id, barcode);

        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, misread.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, itemWithBadCode.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, offline.StatusCode);
        Assert.Equal("Não foi possível procurar o produto agora. Escreve o nome à mão.", problem!.Title);
        Assert.Equal(("Arroz agulha Cigala 1 kg", "Mercearia"), (online.Name, online.Category));
    }

    [Fact]
    public async Task Only_members_who_use_the_shopping_lists_look_up_products()
    {
        var (admin, _) = await factory.CreateUserAsync();
        var (guest, _) = await factory.CreateUserAsync();
        var (stranger, _) = await factory.CreateUserAsync();
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        await NotificationTests.Join(admin, guest, household.Id, HouseholdRole.Guest);
        (await admin.PutAsJsonAsync($"/api/households/{household.Id}/permissions", new SavePermissionsRequest([
            PermissionTests.Role(HouseholdRole.Guest, (HouseholdModule.Shopping, ModuleAccess.View)),
        ]), TestJson.Options)).EnsureSuccessStatusCode();
        var url = $"/api/households/{household.Id}/products/5449000000996";

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData("5449000000996", true)]
    [InlineData("96385074", true)]
    [InlineData("5000112548167", true)]
    [InlineData("5449000000997", false)]
    [InlineData("544900000099", false)]
    [InlineData("54490000009a6", false)]
    [InlineData("", false)]
    public void Barcodes_need_the_right_length_and_check_digit(string code, bool valid) =>
        Assert.Equal(valid, Barcodes.IsValid(code));

    [Fact]
    public void Categories_and_names_are_tidied_for_the_list()
    {
        Assert.Equal("Congelados", ProductCategories.FromTags(["en:frozen-foods", "en:meats"]));
        Assert.Equal("Bebidas", ProductCategories.FromTags(["en:plant-based-foods", "en:beverages"]));
        Assert.Null(ProductCategories.FromTags(["en:something-else"]));
        Assert.Equal("Mercearia", ProductCategories.FromTags(["en:breakfasts", "en:spreads", "en:sweet-spreads"]));
        Assert.Equal("400 g", ProductInfo.TidySize("400 g e"));
        Assert.Equal("400 g", ProductInfo.TidySize("400 g ℮"));
        Assert.Equal("1 L", ProductInfo.TidySize("1 L"));
        Assert.Null(ProductInfo.TidySize(" ℮ "));
        Assert.Equal("Coca-Cola 330 ml", new ProductInfo { Barcode = "x", Name = "Coca-Cola", Brand = "Coca-Cola", Size = "330 ml" }.SuggestedName());
        Assert.Equal("Iogurte natural", new ProductInfo { Barcode = "x", Name = "Iogurte natural" }.SuggestedName());
        Assert.Equal("Coca-Cola Original 330 ml", new ProductInfo
        {
            Barcode = "x", Name = "Coca-Cola Original", Brand = "COCA-COLA SERVICES SA/NV", Size = "330 ml",
        }.SuggestedName());
        Assert.Equal("Corn Flakes 500 g", new ProductInfo
        {
            Barcode = "x", Name = "Corn Flakes", Brand = "KELLOGG ESPAÑA S.L.", Size = "500 g",
        }.SuggestedName());
        Assert.Equal("Arroz agulha", new ProductInfo
        {
            Barcode = "x", Name = "Arroz agulha", Brand = "Companhia Industrial de Arroz do Norte",
        }.SuggestedName());
        Assert.Null(new ProductInfo { Barcode = "x" }.SuggestedName());
    }

    [Fact]
    public async Task The_open_food_facts_answer_is_read_in_portuguese_when_there_is_one()
    {
        const string found = """
            {"code":"5449000000996","product":{"product_name":"Coca-Cola","product_name_pt":"Coca-Cola Original",
             "brands":"Coca-Cola","quantity":"330 ml","categories_tags":["en:beverages","en:sodas"]},
             "status":1,"status_verbose":"product found"}
            """;
        const string nameless = """{"code":"96385074","product":{"brands":"Marca"},"status":1}""";
        const string missing = """{"code":"5000112548167","status":0,"status_verbose":"product not found"}""";
        var answers = new Dictionary<string, (HttpStatusCode, string)>
        {
            ["5449000000996"] = (HttpStatusCode.OK, found),
            ["96385074"] = (HttpStatusCode.OK, nameless),
            ["5000112548167"] = (HttpStatusCode.NotFound, missing),
        };
        string? asked = null;
        var catalog = new OpenFoodFactsCatalog(new HttpClient(new StubHandler(request =>
        {
            asked = request.RequestUri!.ToString();
            var (status, body) = answers[request.RequestUri.Segments[^1]];
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        })));

        var product = await catalog.FindAsync("5449000000996", CancellationToken.None);

        Assert.Equal("Coca-Cola Original", product!.Name);
        Assert.Equal(("Coca-Cola", "330 ml"), (product.Brand, product.Size));
        Assert.Equal(["en:beverages", "en:sodas"], product.Categories);
        Assert.StartsWith("https://world.openfoodfacts.org/api/v2/product/5449000000996?fields=", asked);
        Assert.Null(await catalog.FindAsync("96385074", CancellationToken.None));
        Assert.Null(await catalog.FindAsync("5000112548167", CancellationToken.None));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(answer(request));
    }

    private static async Task<ProductDto> Product(HttpClient client, Guid householdId, string barcode)
    {
        var response = await client.GetAsync($"/api/households/{householdId}/products/{barcode}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ProductDto>(TestJson.Options))!;
    }
}
