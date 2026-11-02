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
/// Arranca a API real em memória com uma base de dados SQLite temporária, para os testes não dependerem de Docker nem
/// de PostgreSQL. Com a variável KUBICO_TEST_POSTGRES (uma ligação a um PostgreSQL, sem base), cada servidor de testes
/// usa uma base PostgreSQL própria, criada pelas migrações e apagada no fim: é assim que o CI prova as migrações, a
/// concorrência e as consultas no motor de produção.
/// </summary>
public sealed class KubicoApiFactory : WebApplicationFactory<Program>
{
    /// <summary>
    /// Relógio controlado pelos testes: meia-noite UTC de hoje (no passado, para os tokens
    /// JWT, que são validados com o relógio real, serem aceites).
    /// </summary>
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero));

    public const string SupportEmail = "suporte@kubico.test";

    /// <summary>"Hoje" na casa (Europe/Lisbon) segundo o relógio dos testes.</summary>
    public DateOnly Today => DateOnly.FromDateTime(
        TimeZoneInfo.ConvertTime(Clock.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById("Europe/Lisbon")).DateTime);

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"kubico-tests-{Guid.NewGuid():N}.db");

    /// <summary>A ligação ao PostgreSQL dos testes (sem base), ou null para usar SQLite.</summary>
    private static readonly string? Postgres = Environment.GetEnvironmentVariable("KUBICO_TEST_POSTGRES");

    private readonly string _postgresDatabase = $"kubico_test_{Guid.NewGuid():N}";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (Postgres is null)
        {
            builder.UseSetting("ConnectionStrings:Kubico", $"Data Source={_databasePath}");
            builder.UseSetting("Database:Provider", "Sqlite");
        }
        else
        {
            builder.UseSetting("ConnectionStrings:Kubico", $"{Postgres};Database={_postgresDatabase}");
            builder.UseSetting("Database:Provider", "Postgres");
        }

        builder.UseSetting("Database:MigrateOnStartup", "true");
        builder.UseSetting("Jwt:SigningKey", "test-signing-key-that-is-long-enough-0123456789");

        // O relógio dos testes está à meia-noite e o JWT é validado com o relógio real: um token de acesso
        // de 15 minutos já estaria expirado. Os testes dos refresh tokens usam o relógio falso (ver RefreshTokenTests).
        builder.UseSetting("Jwt:AccessTokenLifetimeMinutes", "1440");

        // O envio dos avisos para os telemóveis corre nos testes à mão (NotificationDispatcher), com o relógio falso.
        builder.UseSetting("Notifications:DispatchEnabled", "false");

        // A sincronização com calendários externos também (CalendarSyncService, com serviços falsos).
        builder.UseSetting("CalendarSync:Enabled", "false");

        // E as limpezas diárias (MaintenanceService, chamado à mão nos testes que as verificam).
        builder.UseSetting("Maintenance:Enabled", "false");

        // O limite de pedidos por IP desliga-se aqui (todos os testes vêm do mesmo "IP"); RateLimitTests liga-o.
        builder.UseSetting("RateLimiting:Enabled", "false");

        // Quem lê as mensagens enviadas da ajuda (ver FeedbackTests).
        builder.UseSetting("Support:AdminEmails:0", SupportEmail);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<Kubico.Api.Shopping.IProductCatalog>(Products);
        });
    }

    /// <summary>A base pública de produtos dos testes (não vai à internet).</summary>
    public FakeProductCatalog Products { get; } = new();

    /// <summary>Regista um utilizador novo e devolve um cliente já autenticado.</summary>
    public async Task<(HttpClient Client, AuthResponse Auth)> CreateUserAsync(string? name = null, string? email = null)
    {
        var client = CreateClient();
        email ??= $"{Guid.NewGuid():N}@kubico.test";
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
        if (Postgres is not null)
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            using var connection = new Npgsql.NpgsqlConnection(Postgres);
            connection.Open();
            using var drop = connection.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{_postgresDatabase}\" WITH (FORCE)";
            drop.ExecuteNonQuery();
            return;
        }

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
