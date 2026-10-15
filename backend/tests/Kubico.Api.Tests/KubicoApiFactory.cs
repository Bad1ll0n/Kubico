using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Kubico.Api.Tests;

/// <summary>
/// Arranca a API real em memória com uma base de dados SQLite temporária,
/// para os testes não dependerem de Docker nem de PostgreSQL.
/// </summary>
public sealed class KubicoApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"kubico-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Kubico", $"Data Source={_databasePath}");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-0123456789");
    }

    /// <summary>Regista um utilizador novo e devolve um cliente já autenticado.</summary>
    public async Task<(HttpClient Client, AuthResponse Auth)> CreateUserAsync(string? name = null)
    {
        var client = CreateClient();
        var email = $"{Guid.NewGuid():N}@kubico.test";
        var response = await client.PostAsJsonAsync("/api/auth/register",
            new RegisterRequest(email, "palavra-passe-segura", name ?? "Teste"));
        response.EnsureSuccessStatusCode();

        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>(TestJson.Options))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth.AccessToken);
        return (client, auth);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
            // Ficheiro temporário; se ainda estiver em uso, o sistema limpa-o mais tarde.
        }
    }
}
