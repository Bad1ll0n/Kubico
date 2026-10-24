using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Activity;
using Kubico.Api.Auth;
using Kubico.Api.Calendar;
using Kubico.Api.Chores;
using Kubico.Api.Common;
using Kubico.Api.Households;
using Kubico.Api.Notifications;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Kubico.Api.Tests;

/// <summary>Português e inglês: os erros na língua da app, os avisos na língua de quem os recebe.</summary>
public class LanguageTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Errors_come_in_the_language_of_the_app()
    {
        var client = factory.CreateClient();
        var wrong = new LoginRequest("ninguem@kubico.test", "palavra-passe-errada");

        var portuguese = await Problem(await client.PostAsJsonAsync("/api/auth/login", wrong));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB");
        var english = await Problem(await client.PostAsJsonAsync("/api/auth/login", wrong));

        Assert.Equal("Email ou palavra-passe incorretos.", portuguese.Title);
        Assert.Equal("Incorrect email or password.", english.Title);
    }

    [Fact]
    public async Task Validation_messages_are_simple_sentences_in_both_languages()
    {
        var client = factory.CreateClient();
        var tooShort = new RegisterRequest($"{Guid.NewGuid():N}@kubico.test", "curta", "Ana");
        var noName = new { email = $"{Guid.NewGuid():N}@kubico.test", password = "palavra-passe-segura" };

        var portuguese = await Errors(await client.PostAsJsonAsync("/api/auth/register", tooShort));
        var missing = await Errors(await client.PostAsJsonAsync("/api/auth/register", noName));
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var english = await Errors(await client.PostAsJsonAsync("/api/auth/register", tooShort));

        Assert.Equal("A palavra-passe tem de ter entre 8 e 128 caracteres.", portuguese);
        Assert.Equal("Falta o nome.", missing);
        Assert.Equal("The password must have between 8 and 128 characters.", english);
    }

    [Fact]
    public async Task Each_person_gets_notifications_in_their_own_language()
    {
        var (mother, _) = await factory.CreateUserAsync("Mãe");
        var (son, sonAuth) = await factory.CreateUserAsync("Tiago");
        var household = await HouseholdTests.CreateHousehold(mother, "Casa");
        var invite = await HouseholdTests.CreateInvite(mother, household.Id, HouseholdRole.Adult);
        (await son.PostAsync($"/api/invites/{invite.Code}/accept", null)).EnsureSuccessStatusCode();

        var changed = await son.PutAsJsonAsync("/api/account/language", new LanguageRequest("en"));
        var refused = await son.PutAsJsonAsync("/api/account/language", new LanguageRequest("fr"));
        (await mother.PostAsJsonAsync($"/api/households/{household.Id}/chores",
            new SaveChoreRequest("Tirar o lixo", AssigneeUserId: sonAuth.User.Id, DueDate: factory.Today), TestJson.Options))
            .EnsureSuccessStatusCode();
        var inbox = await son.GetFromJsonAsync<NotificationsDto>("/api/notifications", TestJson.Options);
        var me = await son.GetFromJsonAsync<MeDto>("/api/me", TestJson.Options);

        Assert.Equal("en", (await changed.Content.ReadFromJsonAsync<UserDto>(TestJson.Options))!.Language);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal("en", me!.User.Language);
        var notice = Assert.Single(inbox!.Items);
        Assert.Equal("A chore for you: Tirar o lixo", notice.Title);
        Assert.Equal($"Due: {factory.Today:dd/MM}", notice.Body);
    }

    [Fact]
    public async Task History_templates_and_holidays_follow_the_language_of_the_app()
    {
        var (client, _) = await factory.CreateUserAsync("Mãe");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
        var created = await client.PostAsJsonAsync("/api/households",
            new CreateHouseholdRequest("Casa", Template: HouseholdTemplate.FamilyWithKids), TestJson.Options);
        var household = (await created.Content.ReadFromJsonAsync<HouseholdSummaryDto>(TestJson.Options))!;
        var url = $"/api/households/{household.Id}";
        var lists = await client.GetFromJsonAsync<List<ShoppingListDto>>($"{url}/lists", TestJson.Options);
        var chores = await client.GetFromJsonAsync<List<ChoreDto>>($"{url}/chores", TestJson.Options);
        (await client.PostAsJsonAsync($"{url}/lists/{lists![0].Id}/items", new AddShoppingItemRequest("Leite"), TestJson.Options))
            .EnsureSuccessStatusCode();

        var templates = await client.GetFromJsonAsync<List<HouseholdTemplateDto>>("/api/household-templates", TestJson.Options);
        var history = await client.GetFromJsonAsync<List<ActivityDto>>($"{url}/activity", TestJson.Options);
        var year = factory.Today.Year;
        var december = await client.GetFromJsonAsync<CalendarDto>($"{url}/events?from={year}-12-01&to={year}-12-31", TestJson.Options);

        Assert.Equal("Family with children", templates![0].Name);
        Assert.Equal(["Pharmacy", "Supermarket"], lists.Select(l => l.Name).Order());
        Assert.Equal(["Brush teeth", "Get dressed", "Make the bed"], chores!.Single(c => c.Title == "Morning routine").Steps);
        Assert.Equal($"added “Leite” to the list “{lists[0].Name}”", history![0].Summary);
        Assert.Contains(december!.Holidays, h => h.Name == "Christmas Day");
    }

    [Theory]
    [InlineData("Tarefa para ti: Lavar às 5", "A chore for you: Lavar às 5")]
    [InlineData("Tarefa para ti: Loiça (Rita está fora)", "A chore for you: Loiça (Rita is away)")]
    [InlineData("desfez: pôs «Leite» na lista «Casa»", "undid: added “Leite” to the list “Casa”")]
    [InlineData("Recebeste a mesada: 12,50 €", "You got your allowance: €12.50")]
    [InlineData("Com 3 tarefas feitas.", "With 3 chores done.")]
    [InlineData("mudou o papel de Rita para Criança", "changed Rita's role to Child")]
    [InlineData("Uma frase sem tradução.", "Uma frase sem tradução.")]
    public void Composed_texts_keep_names_and_numbers(string portuguese, string english)
    {
        Assert.Equal(english, Texts.Translate(portuguese, Texts.English));
        Assert.Equal(portuguese, Texts.Translate(portuguese, Texts.Portuguese));
    }

    private static async Task<ProblemDetails> Problem(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemDetails>(TestJson.Options))!;

    private static async Task<string> Errors(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<HttpValidationProblemDetails>(TestJson.Options))!;
        return Assert.Single(problem.Errors.SelectMany(e => e.Value));
    }
}
