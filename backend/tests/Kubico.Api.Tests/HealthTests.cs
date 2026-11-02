using System.Net;

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
