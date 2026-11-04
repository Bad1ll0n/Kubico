using System.Net;
using System.Net.Http.Json;

namespace Kubico.Api.Tests;

/// <summary>"Vivo" e "pronto" para o Docker e para um orquestrador (OPS-02, OPS-13).</summary>
public class HealthTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task The_health_endpoints_answer_without_signing_in(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }
}

/// <summary>O dia na casa, para a app (QA-11).</summary>
public class HouseholdOffsetTests(KubicoApiFactory factory) : IClassFixture<KubicoApiFactory>
{
    [Fact]
    public async Task The_household_says_its_current_offset_from_utc()
    {
        var (admin, _, household) = await Fase1.HouseholdAsync(factory);

        var dto = await admin.GetFromJsonAsync<Kubico.Api.Households.HouseholdDto>($"/api/households/{household}", TestJson.Options);
        var lisbon = TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon").GetUtcOffset(factory.Clock.GetUtcNow());

        Assert.Equal((int)lisbon.TotalMinutes, dto!.UtcOffsetMinutes);
    }
}
