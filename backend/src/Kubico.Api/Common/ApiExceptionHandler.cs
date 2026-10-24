using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Common;

/// <summary>
/// Os erros que não são falhas do servidor, como ProblemDetails com o título pronto a mostrar (na língua da app):
/// um pedido mal formado (corpo que não é JSON, um valor que não existe num enum, uma data impossível) dá 400, e
/// dois pedidos em simultâneo sobre a mesma coisa (concorrência otimista) dão 409 ao que chegou depois.
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
}
