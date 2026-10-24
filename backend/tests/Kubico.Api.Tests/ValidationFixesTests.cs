using System.Net;
using System.Net.Http.Json;
using System.Text;
using Kubico.Api.Households;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;

namespace Kubico.Api.Tests;

/// <summary>Pedidos mal formados e validações (T13, T14, T17, T18).</summary>
public class ValidationFixesTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Malformed_requests_get_400_with_a_translated_title_instead_of_500()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var url = $"/api/households/{household}";
        static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

        var noBody = await admin.PostAsync($"{url}/lists", Json(""));
        var notJson = await admin.PostAsync($"{url}/lists", Json("{nome"));
        var impossibleDate = await admin.PostAsync($"{url}/chores", Json("""{"title":"t","dueDate":"2027-02-30"}"""));
        var badQuery = await admin.GetAsync($"{url}/events?from=2026-02-30&to=2026-03-02");
        var badTemplate = await admin.PostAsync("/api/households", Json("""{"name":"Castelo","template":"Castelo"}"""));
        var badLevel = await admin.PutAsync($"{url}/permissions", Json("""{"roles":[{"role":"Child","access":{"Shopping":"Dono"}}]}"""));
        admin.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var english = await admin.PostAsync($"{url}/lists", Json(""));

        foreach (var response in new[] { noBody, notJson, impossibleDate, badQuery, badTemplate, badLevel })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("Pedido inválido.", (await Fase1.ProblemAsync(response)).Title);
        }

        Assert.Equal("Invalid request.", (await Fase1.ProblemAsync(english)).Title);
    }

    [Fact]
    public async Task An_item_cannot_be_renamed_to_blanks()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var list = await NotificationTests.Post<ShoppingListDto>(admin, $"/api/households/{household}/lists", new CreateShoppingListRequest("Casa"));
        var items = $"/api/households/{household}/lists/{list.Id}/items";
        var milk = await NotificationTests.Post<ShoppingItemDto>(admin, items, new AddShoppingItemRequest("Leite"));

        var blank = await admin.PatchAsJsonAsync($"{items}/{milk.Id}", new UpdateShoppingItemRequest(Name: "   "), TestJson.Options);
        var stored = await admin.GetFromJsonAsync<List<ShoppingItemDto>>(items, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal("Leite", Assert.Single(stored!).Name);
    }

    [Fact]
    public async Task Absences_that_overlap_are_refused()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var (_, carla) = await Fase1.MemberAsync(factory, admin, household, "Carla", HouseholdRole.Child);
        var url = $"/api/households/{household}/absences";
        var today = factory.Today;

        var first = await admin.PostAsJsonAsync(url, new SaveAbsenceRequest(carla, today.AddDays(20), today.AddDays(22)), TestJson.Options);
        var same = await admin.PostAsJsonAsync(url, new SaveAbsenceRequest(carla, today.AddDays(20), today.AddDays(22)), TestJson.Options);
        var overlapping = await admin.PostAsJsonAsync(url, new SaveAbsenceRequest(carla, today.AddDays(21), today.AddDays(25)), TestJson.Options);
        var after = await admin.PostAsJsonAsync(url, new SaveAbsenceRequest(carla, today.AddDays(23), today.AddDays(25)), TestJson.Options);
        var parallel = await Fase1.ParallelAsync(4, _ => admin.PostAsJsonAsync(
            url, new SaveAbsenceRequest(carla, today.AddDays(40), today.AddDays(41)), TestJson.Options));
        var list = await admin.GetFromJsonAsync<List<AbsenceDto>>(url, TestJson.Options);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, same.StatusCode);
        Assert.Equal("Já está marcada uma ausência nesses dias.", (await Fase1.ProblemAsync(overlapping)).Title);
        Assert.Equal(HttpStatusCode.Created, after.StatusCode);
        Assert.Equal(1, parallel.Count(s => s == HttpStatusCode.Created));
        Assert.Equal(3, list!.Count);
    }

    [Fact]
    public async Task A_child_profile_cannot_be_born_in_the_future_or_be_an_adult()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);
        var url = $"/api/households/{household}/children";
        var today = factory.Today;

        var future = await admin.PostAsJsonAsync(url, new CreateChildProfileRequest("Rita", "1234", today.AddDays(1)), TestJson.Options);
        var adult = await admin.PostAsJsonAsync(url, new CreateChildProfileRequest("Rita", "1234", today.AddYears(-18)), TestJson.Options);
        var child = await admin.PostAsJsonAsync(url, new CreateChildProfileRequest("Rita", "1234", today.AddYears(-8)), TestJson.Options);
        var created = (await child.Content.ReadFromJsonAsync<MemberDto>(TestJson.Options))!;
        var edit = await admin.PutAsJsonAsync($"{url}/{created.UserId}", new UpdateChildProfileRequest("Rita", today.AddYears(2)), TestJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, future.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, adult.StatusCode);
        Assert.Equal(HttpStatusCode.Created, child.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, edit.StatusCode);
    }
}
