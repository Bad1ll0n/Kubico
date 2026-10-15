using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Kubico.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Regista a base de dados. Por omissão usa PostgreSQL; "Database:Provider" = "Sqlite"
    /// serve para testes e para correr sem Docker.
    /// </summary>
    public static IServiceCollection AddKubicoPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        var provider = configuration["Database:Provider"] ?? "Postgres";
        var connectionString = configuration.GetConnectionString("Kubico")
            ?? throw new InvalidOperationException("Falta a connection string 'Kubico'.");

        services.AddDbContext<KubicoDbContext>(options =>
        {
            if (provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                options.UseSqlite(connectionString);
            }
            else
            {
                options.UseNpgsql(connectionString);
            }
        });

        return services;
    }
}
