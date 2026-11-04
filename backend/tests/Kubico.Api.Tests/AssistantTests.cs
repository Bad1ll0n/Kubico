using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Kubico.Api.Assistant;
using Kubico.Api.Shopping;
using Kubico.Domain.Households;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Api.Tests;

/// <summary>O assistente (F4-24): propõe ações a partir de uma frase, de um talão ou de um PDF, sem fazer nada sozinho.</summary>
public class AssistantTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] Pdf = "%PDF-1.4 calendário escolar"u8.ToArray();

    [Fact]
    public async Task A_sentence_becomes_validated_actions_and_the_model_never_hears_about_the_children()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        await Fase1.ChildProfileAsync(factory, mum, household, "Ritinha");
        var list = await NotificationTests.Post<ShoppingListDto>(mum, $"/api/households/{household}/lists", new CreateShoppingListRequest("Supermercado"));
        factory.Assistant.Prompts.Clear();
        factory.Assistant.Respond = _ => new AssistantDraft("Proponho quatro coisas.",
        [
            FakeAssistant.Action("add_items", list: "supermercado", items: ["Leite", "leite", " Pão ", ""]),
            FakeAssistant.Action("create_event", title: "Dentista", date: "2027-07-09", start: "18:30", location: "Clínica"),
            FakeAssistant.Action("create_event", title: "Férias da Páscoa", date: "2027-03-22", endDate: "2027-04-02"),
            FakeAssistant.Action("create_chore", title: "Regar as plantas", date: "2027-07-10"),
            FakeAssistant.Action("create_expense", title: "Jantar", euros: 12.5m),
            FakeAssistant.Action("create_event", title: "Sem data"),
            FakeAssistant.Action("hack_the_planet", title: "?"),
        ]);

        var proposal = await Propose(mum, household, new AssistantRequest("Põe leite e pão, marca o dentista sexta às 18h30…", null, null));

        Assert.Equal("Proponho quatro coisas.", proposal.Reply);
        Assert.Equal(2, proposal.Skipped);
        Assert.Equal(1, proposal.UsedToday);
        var items = Assert.Single(proposal.Actions, a => a.Kind == AssistantActionKind.AddItems);
        Assert.Equal(list.Id, items.ListId);
        Assert.Equal(["Leite", "Pão"], items.Items);
        var dentist = Assert.Single(proposal.Actions, a => a.Title == "Dentista");
        Assert.Equal(DateTimeOffset.Parse("2027-07-09T17:30:00Z"), dentist.StartsAt); // Lisboa no verão: UTC+1
        Assert.Equal(dentist.StartsAt!.Value.AddHours(1), dentist.EndsAt);
        Assert.Equal("Clínica", dentist.Location);
        var easter = Assert.Single(proposal.Actions, a => a.Title == "Férias da Páscoa");
        Assert.Null(easter.StartsAt);
        Assert.Equal(new DateOnly(2027, 4, 2), easter.EndDate);
        Assert.Equal(new DateOnly(2027, 7, 10), Assert.Single(proposal.Actions, a => a.Kind == AssistantActionKind.CreateChore).Date);
        var dinner = Assert.Single(proposal.Actions, a => a.Kind == AssistantActionKind.CreateExpense);
        Assert.Equal(1250, dinner.AmountCents);
        Assert.Equal(factory.Today, dinner.Date);

        var prompt = Assert.Single(factory.Assistant.Prompts);
        Assert.Equal(factory.Today, prompt.Today);
        Assert.Equal(["Supermercado"], prompt.Lists);
        Assert.Null(prompt.File);
        Assert.DoesNotContain("Ritinha", AssistantInstructions.Context(prompt));
    }

    [Fact]
    public async Task A_receipt_photo_ticks_the_items_already_on_the_list_and_proposes_the_expense()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        var list = await NotificationTests.Post<ShoppingListDto>(mum, $"/api/households/{household}/lists", new CreateShoppingListRequest("Mercearia"));
        var items = $"/api/households/{household}/lists/{list.Id}/items";
        var milk = await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Leite"));
        var bread = await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Pão"));
        await NotificationTests.Post<ShoppingItemDto>(mum, items, new AddShoppingItemRequest("Detergente"));
        factory.Assistant.Prompts.Clear();
        factory.Assistant.Respond = _ => new AssistantDraft("Encontrei 2 artigos.",
        [
            FakeAssistant.Action("check_items", items: ["leite", "PAO", "Bananas"]),
            FakeAssistant.Action("create_expense", title: "Pingo Doce", euros: 7.99m, date: "2026-11-03"),
        ]);

        // A app diz JPEG, mas os bytes são de um PNG: conta o que o ficheiro é.
        var proposal = await Propose(mum, household, new AssistantRequest(null, Convert.ToBase64String(Png), "image/jpeg"));
        var notAnImage = await mum.PostAsJsonAsync($"/api/households/{household}/assistant",
            new AssistantRequest(null, Convert.ToBase64String("olá"u8.ToArray()), "image/png"), TestJson.Options);
        var nothing = await mum.PostAsJsonAsync($"/api/households/{household}/assistant",
            new AssistantRequest("  ", null, null), TestJson.Options);

        var check = Assert.Single(proposal.Actions, a => a.Kind == AssistantActionKind.CheckItems);
        Assert.Equal(list.Id, check.ListId);
        Assert.Equal(new[] { milk.Id, bread.Id }.Order(), check.ItemIds!.Order());
        Assert.Equal(799, Assert.Single(proposal.Actions, a => a.Kind == AssistantActionKind.CreateExpense).AmountCents);
        var prompt = Assert.Single(factory.Assistant.Prompts);
        Assert.Equal("image/png", prompt.MediaType);
        Assert.Equal(["Leite", "Pão", "Detergente"], prompt.UncheckedItems);
        Assert.Equal(HttpStatusCode.BadRequest, notAnImage.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, nothing.StatusCode);
    }

    [Fact]
    public async Task Proposals_follow_the_permissions_children_cannot_use_it_and_strangers_get_404()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        await NotificationTests.Post<ShoppingListDto>(mum, $"/api/households/{household}/lists", new CreateShoppingListRequest("Casa"));
        var (guest, _) = await Fase1.MemberAsync(factory, mum, household, "Rui", HouseholdRole.Guest);
        var (child, _, _) = await Fase1.ChildProfileAsync(factory, mum, household, "Rita");
        var (stranger, _, _) = await Fase1.HouseholdAsync(factory);
        factory.Assistant.Respond = _ => new AssistantDraft(null,
        [
            FakeAssistant.Action("add_items", items: ["Fruta"]),
            FakeAssistant.Action("create_event", title: "Reunião", date: "2027-01-10"),
            FakeAssistant.Action("create_chore", title: "Lixo"),
        ]);

        var asGuest = await Propose(guest, household, new AssistantRequest("fruta, reunião e lixo", null, null));
        var asChild = await child.PostAsJsonAsync($"/api/households/{household}/assistant", new AssistantRequest("olá", null, null), TestJson.Options);
        var childStatus = await child.GetFromJsonAsync<AssistantStatusDto>($"/api/households/{household}/assistant", TestJson.Options);
        var asStranger = await stranger.PostAsJsonAsync($"/api/households/{household}/assistant", new AssistantRequest("olá", null, null), TestJson.Options);

        Assert.Equal(AssistantActionKind.AddItems, Assert.Single(asGuest.Actions).Kind);
        Assert.Equal(2, asGuest.Skipped);
        Assert.Equal(HttpStatusCode.Forbidden, asChild.StatusCode);
        Assert.False(childStatus!.Available);
        Assert.Equal(HttpStatusCode.NotFound, asStranger.StatusCode);
    }

    [Fact]
    public async Task Each_household_has_a_daily_limit_even_in_parallel_and_failures_do_not_count()
    {
        using var limited = factory.WithWebHostBuilder(b => b.UseSetting("Assistant:DailyLimit", "3"));
        var (mum, auth) = await CreateUserAsync(limited);
        var household = (await HouseholdTests.CreateHousehold(mum, "Limite")).Id;
        var url = $"/api/households/{household}/assistant";

        factory.Assistant.Respond = _ => throw new HttpRequestException("Sem ligação ao Claude.");
        var failed = await mum.PostAsJsonAsync(url, new AssistantRequest("olá", null, null), TestJson.Options);
        var afterFailure = await mum.GetFromJsonAsync<AssistantStatusDto>(url, TestJson.Options);

        factory.Assistant.Respond = _ => new AssistantDraft("ok", []);
        var statuses = await Fase1.ParallelAsync(6, _ => mum.PostAsJsonAsync(url, new AssistantRequest("olá", null, null), TestJson.Options));
        var status = await mum.GetFromJsonAsync<AssistantStatusDto>(url, TestJson.Options);

        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal(0, afterFailure!.UsedToday);
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(3, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(new AssistantStatusDto(true, true, 3, 3), status);
        _ = auth;
    }

    [Fact]
    public async Task Only_the_admin_turns_it_off_and_without_an_api_key_it_does_not_exist()
    {
        var (mum, _, household) = await Fase1.HouseholdAsync(factory);
        var (dad, _) = await Fase1.MemberAsync(factory, mum, household, "Pai", HouseholdRole.Adult);
        var url = $"/api/households/{household}/assistant";

        var dadTries = await dad.PutAsJsonAsync($"{url}/settings", new AssistantSettingsRequest(false), TestJson.Options);
        (await mum.PutAsJsonAsync($"{url}/settings", new AssistantSettingsRequest(false), TestJson.Options)).EnsureSuccessStatusCode();
        var whenOff = await dad.PostAsJsonAsync(url, new AssistantRequest("olá", null, null), TestJson.Options);
        var status = await dad.GetFromJsonAsync<AssistantStatusDto>(url, TestJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, dadTries.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, whenOff.StatusCode);
        Assert.False(status!.Enabled);

        using var withoutKey = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s => s.AddSingleton<IAssistant>(new DisabledAssistant())));
        var (other, _) = await CreateUserAsync(withoutKey);
        var otherHousehold = (await HouseholdTests.CreateHousehold(other, "Sem chave")).Id;
        var unavailable = await other.GetFromJsonAsync<AssistantStatusDto>($"/api/households/{otherHousehold}/assistant", TestJson.Options);
        var tried = await other.PostAsJsonAsync($"/api/households/{otherHousehold}/assistant", new AssistantRequest("olá", null, null), TestJson.Options);

        Assert.False(unavailable!.Available);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, tried.StatusCode);
    }

    [Fact]
    public void The_request_to_claude_uses_structured_output_the_pdf_and_server_side_fallbacks()
    {
        var prompt = new AssistantPrompt("Calendário da escola", Pdf, "application/pdf", new DateOnly(2026, 11, 4), "pt", ["Supermercado"], ["Leite"]);

        var json = JsonSerializer.Serialize(ClaudeAssistant.Request(prompt, new AssistantOptions()).RawBodyData);
        using var request = JsonDocument.Parse(json);
        var root = request.RootElement;

        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
        Assert.False(root.TryGetProperty("thinking", out _));
        var content = root.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("document", content[0].GetProperty("type").GetString());
        Assert.Equal("application/pdf", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Contains("Hoje: 2026-11-04", content[1].GetProperty("text").GetString());
        Assert.Contains("Artigos por comprar: Leite.", content[1].GetProperty("text").GetString());
    }

    [Fact]
    public void The_model_answer_is_parsed_from_the_json_schema()
    {
        var draft = AssistantInstructions.Parse("""
            {"reply":"Feito","actions":[{"kind":"add_items","list":null,"items":["Ovos"],"title":null,"date":null,
            "endDate":null,"start":null,"end":null,"amountEuros":null,"location":null}]}
            """);

        Assert.Equal("Feito", draft.Reply);
        Assert.Equal(["Ovos"], Assert.Single(draft.Actions).Items);
    }

    private static async Task<AssistantProposalDto> Propose(HttpClient client, Guid household, AssistantRequest request)
    {
        var response = await client.PostAsJsonAsync($"/api/households/{household}/assistant", request, TestJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<AssistantProposalDto>(TestJson.Options))!;
    }

    private static async Task<(HttpClient Client, Auth.AuthResponse Auth)> CreateUserAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new Auth.RegisterRequest($"{Guid.NewGuid():N}@kubico.test", "palavra-passe-segura", "Mãe"));
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<Auth.AuthResponse>(TestJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth);
    }
}
