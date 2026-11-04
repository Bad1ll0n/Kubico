using System.Net;
using System.Net.Http.Json;
using Kubico.Api.Households;
using Kubico.Api.Support;
using Kubico.Domain.Support;

namespace Kubico.Api.Tests;

/// <summary>"Reportar um problema" e "Sugerir uma ideia": chegam a quem mantém o Kubico, e só essa conta as lê.</summary>
public class FeedbackTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task Messages_from_the_help_screen_reach_only_the_support_account()
    {
        var (member, _) = await factory.CreateUserAsync("Mãe");
        var (stranger, _) = await factory.CreateUserAsync("Vizinho");
        var (support, _) = await factory.CreateUserAsync("Suporte", KubicoApiFactory.SupportEmail);
        var household = await HouseholdTests.CreateHousehold(member, "Casa");
        member.DefaultRequestHeaders.Add("X-Kubico-Device", "android");

        var problem = await member.PostAsJsonAsync("/api/feedback",
            new SendFeedbackRequest(FeedbackKind.Problem, "A lista não atualiza no telemóvel.", household.Id), TestJson.Options);
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        var idea = await member.PostAsJsonAsync("/api/feedback",
            new SendFeedbackRequest(FeedbackKind.Idea, "Era bom ter receitas."), TestJson.Options);
        var tooShort = await member.PostAsJsonAsync("/api/feedback", new SendFeedbackRequest(FeedbackKind.Idea, "ok"), TestJson.Options);
        var notSupport = await member.GetAsync("/api/feedback");
        var memberMe = await member.GetFromJsonAsync<MeDto>("/api/me", TestJson.Options);
        var supportMe = await support.GetFromJsonAsync<MeDto>("/api/me", TestJson.Options);
        var inbox = (await support.GetFromJsonAsync<List<FeedbackDto>>("/api/feedback", TestJson.Options))!;

        Assert.Equal(HttpStatusCode.NoContent, problem.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, idea.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, notSupport.StatusCode);
        Assert.False(memberMe!.IsSupport);
        Assert.True(supportMe!.IsSupport);
        Assert.Equal(["Era bom ter receitas.", "A lista não atualiza no telemóvel."], inbox.Select(f => f.Message));
        Assert.Equal((FeedbackKind.Problem, "android", "Mãe"), (inbox[1].Kind, inbox[1].Device, inbox[1].UserName));

        // Um erro na app não pode encher a caixa: no máximo 20 mensagens por pessoa e por dia.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 20; i++)
        {
            statuses.Add((await stranger.PostAsJsonAsync("/api/feedback",
                new SendFeedbackRequest(FeedbackKind.Problem, $"Mensagem número {i}"), TestJson.Options)).StatusCode);
        }

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.NoContent, s));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await stranger.PostAsJsonAsync("/api/feedback",
            new SendFeedbackRequest(FeedbackKind.Problem, "Mais uma mensagem"), TestJson.Options)).StatusCode);
    }
}
