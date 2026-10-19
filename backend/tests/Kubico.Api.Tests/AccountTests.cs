using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kubico.Api.Auth;
using Kubico.Api.Chores;
using Kubico.Api.Households;
using Kubico.Api.Shopping;
using Kubico.Api.Support;
using Kubico.Domain.Households;
using Kubico.Domain.Support;

namespace Kubico.Api.Tests;

/// <summary>A minha conta (RGPD): descarregar os meus dados e apagar a conta.</summary>
public class AccountTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    private const string Password = "palavra-passe-segura";

    [Fact]
    public async Task The_export_has_my_data_and_nothing_secret_or_from_other_people()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (me, meAuth) = await factory.CreateUserAsync("Mãe", email);
        var (partner, _) = await factory.CreateUserAsync("Pai");
        var household = await HouseholdTests.CreateHousehold(me, "Casa do Monte");
        await NotificationTests.Join(me, partner, household.Id, HouseholdRole.Adult);
        var list = await NotificationTests.Post<ShoppingListDto>(me, $"/api/households/{household.Id}/lists", new CreateShoppingListRequest("Mercearia"));
        await NotificationTests.Post<ShoppingItemDto>(me, $"/api/households/{household.Id}/lists/{list.Id}/items", new AddShoppingItemRequest("Azeite"));
        await NotificationTests.Post<ShoppingItemDto>(partner, $"/api/households/{household.Id}/lists/{list.Id}/items", new AddShoppingItemRequest("Cerveja"));
        var chore = await NotificationTests.Post<ChoreDto>(me, $"/api/households/{household.Id}/chores", new SaveChoreRequest("Regar a horta", Points: 3));
        await NotificationTests.Post<ChoreCompletedDto>(me, $"/api/households/{household.Id}/chores/{chore.Id}/complete", null);
        (await me.PostAsJsonAsync("/api/feedback", new SendFeedbackRequest(FeedbackKind.Idea, "Gostava de uma lista de prendas."), TestJson.Options))
            .EnsureSuccessStatusCode();

        var response = await me.GetAsync("/api/account/export");
        var text = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType!.MediaType);
        Assert.StartsWith("kubico-dados-", response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal(email, root.GetProperty("Conta").GetProperty("Email").GetString());
        Assert.Equal(meAuth.User.Id, root.GetProperty("Conta").GetProperty("Id").GetGuid());
        Assert.Equal("Casa do Monte", root.GetProperty("Casas")[0].GetProperty("Casa").GetString());
        Assert.Equal("Regar a horta", root.GetProperty("TarefasFeitas")[0].GetProperty("Tarefa").GetString());
        Assert.Equal(["Azeite"], root.GetProperty("ArtigosDeCompras").EnumerateArray().Select(i => i.GetProperty("Artigo").GetString()));
        Assert.Equal(1, root.GetProperty("SugestoesEnviadas").GetArrayLength());
        Assert.Equal(1, root.GetProperty("Sessoes").GetArrayLength());
        Assert.DoesNotContain("Cerveja", text);
        Assert.DoesNotContain("Hash", text);
        Assert.DoesNotContain(meAuth.RefreshToken!, text);
    }

    [Fact]
    public async Task Deleting_the_account_hands_over_the_administration_and_removes_houses_where_no_one_else_has_an_account()
    {
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var (me, meAuth) = await factory.CreateUserAsync("Mãe", email);
        var (partner, partnerAuth) = await factory.CreateUserAsync("Pai");
        var home = await HouseholdTests.CreateHousehold(me, "Casa");
        var beach = await HouseholdTests.CreateHousehold(me, "Casa da praia");
        await NotificationTests.Join(me, partner, home.Id, HouseholdRole.Adult);
        var beachKid = await ChildProfileTests.CreateChild(me, beach.Id, "Rita", "1234");
        var code = await ChildProfileTests.FamilyCode(me, beach.Id);

        var wrongPassword = await Delete(me, "outra-coisa");
        var deleted = await Delete(me, Password);
        var meAfter = await me.GetAsync("/api/me");
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login", new LoginRequest(email, Password));
        var members = await MemberTests.Members(partner, home.Id);
        var beachForPartner = await partner.GetAsync($"/api/households/{beach.Id}");
        var kidLogin = await factory.CreateClient().PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, beachKid.UserId, "1234"));

        Assert.Equal(HttpStatusCode.BadRequest, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, meAfter.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        var only = Assert.Single(members);
        Assert.Equal((partnerAuth.User.Id, HouseholdRole.Admin), (only.UserId, only.Role));
        Assert.Equal(HttpStatusCode.NotFound, beachForPartner.StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, kidLogin.StatusCode);
        Assert.NotEqual(meAuth.User.Id, partnerAuth.User.Id);
    }

    [Fact]
    public async Task A_child_profile_cannot_delete_itself()
    {
        var (admin, _) = await factory.CreateUserAsync("Mãe");
        var household = await HouseholdTests.CreateHousehold(admin, "Casa");
        var rita = await ChildProfileTests.CreateChild(admin, household.Id, "Rita", "1234");
        var code = await ChildProfileTests.FamilyCode(admin, household.Id);
        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/child/login", new ChildLoginRequest(code, rita.UserId, "1234"));
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
        var child = factory.CreateClient();
        child.DefaultRequestHeaders.Authorization = new("Bearer", auth.AccessToken);

        var response = await Delete(child, "1234");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(2, (await MemberTests.Members(admin, household.Id)).Count);
    }

    private static Task<HttpResponseMessage> Delete(HttpClient client, string password) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/account")
        {
            Content = JsonContent.Create(new DeleteAccountRequest(password), options: TestJson.Options),
        });
}
