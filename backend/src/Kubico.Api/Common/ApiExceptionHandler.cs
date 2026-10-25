using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Kubico.Api.Common;

/// <summary>
/// Os erros que não são falhas do servidor, como ProblemDetails com o título pronto a mostrar (na língua da app):
/// um pedido mal formado (corpo que não é JSON, um valor que não existe num enum, uma data impossível) dá 400, e
/// dois pedidos em simultâneo sobre a mesma coisa (concorrência otimista, ou uma linha repetida que um índice único
/// recusa) dão 409 ao que chegou depois.
/// </summary>
public sealed class ApiExceptionHandler(IProblemDetailsService problems, ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public const string InvalidRequest = "Pedido inválido.";

    public const string ChangedMeanwhile = "Alguém mudou isto ao mesmo tempo. Atualiza e tenta outra vez.";

    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            BadHttpRequestException bad => (bad.StatusCode, InvalidRequest),
            DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, ChangedMeanwhile),
            DbUpdateException update when IsUniqueViolation(update) => (StatusCodes.Status409Conflict, ChangedMeanwhile),
            _ => (0, null),
        };
        if (title is null)
        {
            return false;
        }

        logger.LogInformation("Pedido recusado ({Status}): {Message}", status, exception.Message);
        http.Response.StatusCode = status;
        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = new ProblemDetails { Status = status, Title = title },
        });
    }

    /// <summary>
    /// Uma linha que já existe (chave primária ou índice único): o que acontece quando dois pedidos iguais chegam
    /// ao mesmo tempo e os dois passam a verificação antes de gravar.
    /// </summary>
    public static bool IsUniqueViolation(DbUpdateException exception) => exception.InnerException switch
    {
        PostgresException postgres => postgres.SqlState == PostgresErrorCodes.UniqueViolation,
        SqliteException sqlite => sqlite.SqliteExtendedErrorCode is SqliteUniqueConstraint or SqlitePrimaryKeyConstraint,
        _ => false,
    };

    private const int SqliteUniqueConstraint = 2067;

    private const int SqlitePrimaryKeyConstraint = 1555;
}
