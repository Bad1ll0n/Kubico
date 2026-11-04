using Kubico.Api.Common;
using Kubico.Api.Realtime;

namespace Kubico.Api.Auth;

/// <summary>
/// Para as ações sensíveis (apagar a conta, verificação em dois passos, gerir membros, convites, permissões e perfis
/// de criança): além do token de acesso ser válido, a sessão de onde veio tem de estar ativa. Terminar a sessão
/// (noutro dispositivo, ou em todos) corta-as logo, sem esperar que o token de acesso expire.
/// </summary>
public sealed class ActiveSessionFilter(SessionService sessions) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated == true
            && !await sessions.IsActiveAsync(user.GetUserId(), HouseholdHub.SessionOf(user), context.HttpContext.RequestAborted))
        {
            return Results.Problem(title: "A sessão terminou. Entra novamente.", statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }
}

public static class ActiveSessionExtensions
{
    /// <summary>A ação só passa com a sessão ativa (ver <see cref="ActiveSessionFilter"/>).</summary>
    public static TBuilder RequireActiveSession<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter<TBuilder, ActiveSessionFilter>();
}
