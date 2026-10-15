using System.Net.Http.Headers;
using System.Net.Http.Json;
using Kubico.Api.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Kubico.Api.Tests;

/// <summary>
/// Arranca a API real em memória com uma base de dados SQLite temporária,
/// para os testes não dependerem de Docker nem de PostgreSQL.
/// </summary>
public sealed class KubicoApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Relógio controlado pelos testes: meia-noite UTC de hoje (no passado, para os tokens
    /// JWT, que são validados com o relógio real, serem aceites).
    /// </summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero));

    /// <summary>"Hoje" na casa (Europe/Lisbon) segundo o relógio dos testes.</summary>
    public DateOnly Today => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon")).DateTime);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"kubico-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Kubico", $"Data Source={_databasePath}");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-0123456789");

        // O relógio dos testes está à meia-noite e o JWT é validado com o relógio real: um token de acesso
        // de 15 minutos já estaria expirado. Os testes dos refresh tokens usam o relógio falso (ver RefreshTokenTests).
        builder.UseSetting("Jwt:AccessTokenLifetimeMinutes", "1440");

        // O envio dos avisos para os telemóveis corre nos testes à mão (NotificationDispatcher), com o relógio falso.
        builder.UseSetting("Notifications:DispatchEnabled", "false");
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));
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
