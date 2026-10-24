using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Kubico.Api.Common;
using Kubico.Api.Households;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Kubico.Domain.Users;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Auth;

public record DeleteAccountRequest([property: Required] string Password);

public record TwoFactorStatusDto(bool Enabled, int RecoveryCodesLeft);

/// <summary>Para ligar: o segredo (para escrever à mão) e o endereço que vai no código QR.</summary>
public record TwoFactorSetupDto(string Secret, string Uri);

public record TwoFactorCodeRequest([property: Required] string Code);

/// <summary>Ficou ligada. Os códigos de recuperação só se mostram desta vez.</summary>
public record TwoFactorEnabledDto(IReadOnlyList<string> RecoveryCodes);

public record DisableTwoFactorRequest([property: Required] string Password, [property: Required] string Code);

/// <summary>Começar a ligar a 2FA pede a palavra-passe: quem só tem uma sessão aberta não tranca a pessoa fora da conta.</summary>
public record TwoFactorSetupRequest([property: Required] string Password);

/// <summary>
/// O que acontece a cada casa se a conta for apagada: <c>Deleted</c> (mais ninguém tem conta: a casa é apagada),
/// <c>AdminPassesTo</c> (a administração passa para <c>SuccessorName</c>), <c>Blocked</c> (é o único administrador e não
/// há outro adulto: tem de passar a administração antes) ou <c>Leaves</c> (sai, e a casa continua como está).
/// </summary>
public record AccountDeletionHouseholdDto(Guid HouseholdId, string HouseholdName, string Outcome, string? SuccessorName);

/// <summary>"pt" ou "en".</summary>
public record LanguageRequest([property: Required] string Language);

/// <summary>
/// A minha conta (RGPD): descarregar tudo o que o Kubico guarda sobre mim, e apagar a conta. Ao apagar, sai de todas
/// as casas como se saísse de cada uma (a administração passa a outra pessoa se for preciso, e as casas onde mais
/// ninguém tem conta são apagadas).
/// </summary>
public static class AccountEndpoints
{
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static RouteGroupBuilder MapAccountEndpoints(this RouteGroupBuilder api)
    {
        var account = api.MapGroup("/account").WithTags("Auth").RequireAuthorization();

        // Ações sensíveis: só com a sessão ativa (terminar a sessão noutro lado corta-as logo).
        account.MapGet("/export", Export).RequireActiveSession();
        account.MapGet("/deletion", DeletionPreview);
        account.MapDelete("/", DeleteAccount).RequireActiveSession();
        account.MapGet("/2fa", GetTwoFactor);
        account.MapPost("/2fa/setup", SetUpTwoFactor).RequireActiveSession();
        account.MapPost("/2fa/enable", EnableTwoFactor).RequireActiveSession();
        account.MapPost("/2fa/disable", DisableTwoFactor).RequireActiveSession();
        account.MapPut("/language", SetLanguage);
        return api;
    }

    /// <summary>Um ficheiro JSON com os dados da conta e o que a pessoa fez em cada casa.</summary>
    private static async Task<IResult> Export(ClaimsPrincipal principal, KubicoDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var households = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Select(m => new { m.HouseholdId, m.Household.Name, m.Role, m.Color, m.JoinedAt })
            .ToListAsync(ct);
        var names = households.ToDictionary(h => h.HouseholdId, h => h.Name);
        string? Casa(Guid? id) => id is { } value ? names.GetValueOrDefault(value) : null;

        var completions = await db.ChoreCompletions.AsNoTracking()
            .Where(c => c.UserId == userId)
            .Select(c => new { c.Chore.HouseholdId, c.Chore.Title, c.Points, c.CompletedAt, c.PhotoId, c.RejectedAt })
            .ToListAsync(ct);
        var shopping = await db.ShoppingItems.AsNoTracking()
            .Where(i => i.AddedByUserId == userId)
            .Select(i => new { i.List.HouseholdId, Lista = i.List.Name, i.Name, i.Quantity, i.CreatedAt })
            .ToListAsync(ct);
        var events = await db.CalendarEvents.AsNoTracking()
            .Where(e => e.CreatedByUserId == userId)
            .Select(e => new { e.HouseholdId, e.Title, e.StartsAt, e.StartDate, e.CreatedAt })
            .ToListAsync(ct);

        var export = new
        {
            ExportadoEm = clock.GetUtcNow(),
            Conta = new
            {
                user.Id, Nome = user.DisplayName, user.Email, CriadaEm = user.CreatedAt, DataDeNascimento = user.BirthDate,
                VerificacaoEmDoisPassos = user.TwoFactorEnabled, Lingua = user.Language,
            },
            Casas = households.Select(h => new { Casa = h.Name, Papel = h.Role.ToString(), Cor = h.Color, EntrouEm = h.JoinedAt }),
            Sessoes = await db.RefreshTokens.AsNoTracking()
                .Where(t => t.UserId == userId)
                .Select(t => new { Dispositivo = t.Device, IniciadaEm = t.CreatedAt, ExpiraEm = t.ExpiresAt, TerminadaEm = t.RevokedAt })
                .ToListAsync(ct),
            PreferenciasDeAvisos = await db.NotificationPreferences.AsNoTracking()
                .Where(p => p.UserId == userId)
                .Select(p => new { Envio = p.Delivery.ToString(), ResumoAs = p.DigestAt, SilencioDas = p.QuietFrom, SilencioAte = p.QuietUntil, Fuso = p.TimeZoneId })
                .SingleOrDefaultAsync(ct),
            Telemoveis = await db.PushDevices.AsNoTracking()
                .Where(d => d.UserId == userId)
                .Select(d => new { Plataforma = d.Platform, RegistadoEm = d.CreatedAt, VistoEm = d.LastSeenAt })
                .ToListAsync(ct),
            Avisos = (await db.Notifications.AsNoTracking()
                .Where(n => n.UserId == userId)
                .Select(n => new { n.HouseholdId, n.Title, n.Body, n.CreatedAt, n.ReadAt })
                .ToListAsync(ct))
                .Select(n => new { Casa = Casa(n.HouseholdId), Titulo = n.Title, Texto = n.Body, CriadoEm = n.CreatedAt, LidoEm = n.ReadAt }),
            TarefasFeitas = completions.Select(c => new
            {
                Casa = Casa(c.HouseholdId), Tarefa = c.Title, Pontos = c.Points, FeitaEm = c.CompletedAt,
                ComFoto = c.PhotoId != null, NaoAceite = c.RejectedAt != null,
            }),
            ArtigosDeCompras = shopping.Select(i => new { Casa = Casa(i.HouseholdId), i.Lista, Artigo = i.Name, Quantidade = i.Quantity, AdicionadoEm = i.CreatedAt }),
            EventosCriados = events.Select(e => new
            {
                Casa = Casa(e.HouseholdId), Evento = e.Title, Comeca = e.StartsAt, Dia = e.StartDate, CriadoEm = e.CreatedAt,
            }),
            PedidosDeRecompensa = (await db.RewardRedemptions.AsNoTracking()
                .Where(r => r.UserId == userId)
                .Select(r => new { r.HouseholdId, r.RewardTitle, r.Points, r.Status, r.RequestedAt, r.DecidedAt })
                .ToListAsync(ct))
                .Select(r => new { Casa = Casa(r.HouseholdId), Recompensa = r.RewardTitle, Pontos = r.Points, Estado = r.Status.ToString(), PedidoEm = r.RequestedAt, DecididoEm = r.DecidedAt }),
            Ausencias = (await db.MemberAbsences.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct))
                .Select(a => new { Casa = Casa(a.HouseholdId), De = a.StartDate, Ate = a.EndDate, Motivo = a.Reason }),
            Mesadas = (await db.Allowances.AsNoTracking().Where(a => a.UserId == userId).ToListAsync(ct))
                .Select(a => new { Casa = Casa(a.HouseholdId), Cents = a.AmountCents, ExtraPorTarefaCents = a.BonusPerChoreCents, Periodo = a.Period.ToString() }),
            MesadasRecebidas = (await db.AllowancePayments.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct))
                .Select(p => new { Casa = Casa(p.HouseholdId), De = p.PeriodStart, Ate = p.PeriodEnd, Cents = p.AmountCents, PagaEm = p.PaidAt }),
            SugestoesEnviadas = await db.Feedback.AsNoTracking()
                .Where(f => f.UserId == userId)
                .Select(f => new { Tipo = f.Kind.ToString(), Mensagem = f.Message, EnviadaEm = f.CreatedAt })
                .ToListAsync(ct),
            CalendariosLigados = (await db.CalendarConnections.AsNoTracking().Where(c => c.UserId == userId).ToListAsync(ct))
                .Select(c => new { Casa = Casa(c.HouseholdId), Servico = c.Provider.ToString(), Conta = c.AccountEmail, LigadoEm = c.CreatedAt }),
            Historico = (await db.ActivityEntries.AsNoTracking()
                .Where(a => a.UserId == userId)
                .Select(a => new { a.HouseholdId, a.Summary, a.CreatedAt, a.UndoneAt })
                .ToListAsync(ct))
                .Select(a => new { Casa = Casa(a.HouseholdId), OQueFez = a.Summary, Quando = a.CreatedAt, DesfeitoEm = a.UndoneAt }),
            FotosDeProva = await db.ChorePhotos.AsNoTracking()
                .Where(p => p.UploadedByUserId == userId)
                .Select(p => new { p.Id, EnviadaEm = p.CreatedAt, Tipo = p.ContentType })
                .ToListAsync(ct),
        };

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(export, ExportJson));
        var day = clock.GetUtcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        return Results.File(bytes, "application/json", $"kubico-dados-{day}.json");
    }

    /// <summary>A língua da app: os avisos (na app e no telemóvel) passam a chegar nela.</summary>
    private static async Task<IResult> SetLanguage(
        LanguageRequest request, ClaimsPrincipal principal, KubicoDbContext db, CancellationToken ct)
    {
        if (!Texts.Languages.Contains(request.Language))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["language"] = ["Escolhe português (pt) ou inglês (en)."],
            });
        }

        var userId = principal.GetUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        user.Language = request.Language;
        await db.SaveChangesAsync(ct);
        return Results.Ok(user.ToDto());
    }

    private static async Task<IResult> GetTwoFactor(ClaimsPrincipal principal, KubicoDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        return user is null
            ? Results.Unauthorized()
            : Results.Ok(new TwoFactorStatusDto(user.TwoFactorEnabled, user.RecoveryCodeHashes.Count));
    }

    /// <summary>
    /// Começa a ligar a verificação em dois passos: um segredo novo para a app de autenticação. Pede a palavra-passe
    /// (com o limite de tentativas da entrada).
    /// </summary>
    private static async Task<IResult> SetUpTwoFactor(
        TwoFactorSetupRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        TwoFactorService twoFactor,
        IPasswordHasher<User> hasher,
        LoginThrottle throttle,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (user.IsChildProfile)
        {
            return Results.Problem(
                title: "Os perfis de criança entram com o PIN e não têm verificação em dois passos.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        if (user.TwoFactorEnabled)
        {
            return Results.Problem(
                title: "A verificação em dois passos já está ligada.", statusCode: StatusCodes.Status409Conflict);
        }

        if (AuthEndpoints.CheckPassword(throttle, hasher, http, user, request.Password) is { } wrong)
        {
            return wrong;
        }

        var secret = twoFactor.BeginSetup(user);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new TwoFactorSetupDto(secret, Totp.SetupUri(secret, user.Email!)));
    }

    /// <summary>
    /// Confirma com um código da app: fica ligada e recebe os códigos de recuperação. As outras sessões terminam (só
    /// fica esta).
    /// </summary>
    private static async Task<IResult> EnableTwoFactor(
        TwoFactorCodeRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        TwoFactorService twoFactor,
        SessionService sessions,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (twoFactor.Enable(user, request.Code, clock.GetUtcNow()) is not { } codes)
        {
            return Results.Problem(
                title: "O código não está certo. Confirma que o telemóvel está com a hora certa e tenta outra vez.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await db.SaveChangesAsync(ct);
        await sessions.SignOutEverywhereAsync(userId, ct, keep: HouseholdHub.SessionOf(principal));
        return Results.Ok(new TwoFactorEnabledDto(codes));
    }

    /// <summary>
    /// Desliga (pede a palavra-passe e um código da app ou de recuperação, com os mesmos limites de tentativas da
    /// entrada). As outras sessões terminam.
    /// </summary>
    private static async Task<IResult> DisableTwoFactor(
        DisableTwoFactorRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        IPasswordHasher<User> hasher,
        TwoFactorService twoFactor,
        LoginThrottle throttle,
        SessionService sessions,
        HttpContext http,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (AuthEndpoints.CheckPassword(throttle, hasher, http, user, request.Password) is { } wrong)
        {
            return wrong;
        }

        if (!user.TwoFactorEnabled)
        {
            return Results.NoContent();
        }

        if (await AuthEndpoints.CheckTwoFactorAsync(
            db, twoFactor, user, request.Code, clock.GetUtcNow(), ct, StatusCodes.Status400BadRequest) is { } refused)
        {
            return refused;
        }

        TwoFactorService.Disable(user);
        await db.SaveChangesAsync(ct);
        await sessions.SignOutEverywhereAsync(userId, ct, keep: HouseholdHub.SessionOf(principal));
        return Results.NoContent();
    }

    /// <summary>
    /// Antes de apagar a conta: o que acontece a cada casa (para a app dizer quem fica a administrar, ou que é preciso
    /// passar a administração primeiro).
    /// </summary>
    private static async Task<IResult> DeletionPreview(ClaimsPrincipal principal, KubicoDbContext db, CancellationToken ct)
    {
        var plan = await PlanDeletionAsync(db, principal.GetUserId(), ct);
        return Results.Ok(plan.Select(p => new AccountDeletionHouseholdDto(
            p.Membership.HouseholdId, p.Membership.Household.Name, p.Outcome, p.Successor?.User.DisplayName)));
    }

    /// <summary>
    /// Para cada casa: apagá-la (mais ninguém tem conta), passar a administração ao adulto que está lá há mais tempo,
    /// ou recusar (é o único administrador e não há adultos: a administração nunca passa para um convidado ou uma
    /// criança).
    /// </summary>
    private static async Task<List<(Membership Membership, string Outcome, Membership? Successor)>> PlanDeletionAsync(
        KubicoDbContext db, Guid userId, CancellationToken ct)
    {
        var plan = new List<(Membership, string, Membership?)>();
        var mine = await db.Memberships.Include(m => m.User).Include(m => m.Household)
            .Where(m => m.UserId == userId).OrderBy(m => m.Household.Name).ToListAsync(ct);
        foreach (var membership in mine)
        {
            var others = await db.Memberships
                .Include(m => m.User)
                .Where(m => m.HouseholdId == membership.HouseholdId && m.UserId != userId && m.User.ManagedByHouseholdId == null)
                .OrderBy(m => m.JoinedAt)
                .ToListAsync(ct);
            if (others.Count == 0)
            {
                plan.Add((membership, "Deleted", null));
            }
            else if (membership.Role != HouseholdRole.Admin || others.Any(m => m.Role == HouseholdRole.Admin))
            {
                plan.Add((membership, "Leaves", null));
            }
            else if (others.FirstOrDefault(m => m.Role == HouseholdRole.Adult) is { } adult)
            {
                plan.Add((membership, "AdminPassesTo", adult));
            }
            else
            {
                plan.Add((membership, "Blocked", null));
            }
        }

        return plan;
    }

    /// <summary>Apaga a conta (pede a palavra-passe outra vez). Um perfil de criança é apagado por um adulto da casa.</summary>
    private static async Task<IResult> DeleteAccount(
        [FromBody] DeleteAccountRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        IPasswordHasher<User> hasher,
        LoginThrottle throttle,
        MembershipService memberships,
        SessionService sessions,
        HouseholdNotifier notifier,
        HttpContext http,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        if (user.IsChildProfile)
        {
            return Results.Problem(
                title: "Um perfil de criança é apagado por um adulto da casa.", statusCode: StatusCodes.Status403Forbidden);
        }

        if (AuthEndpoints.CheckPassword(throttle, hasher, http, user, request.Password) is { } wrong)
        {
            return wrong;
        }

        // Primeiro confirma-se tudo (nada muda se alguma casa ficasse sem administrador adulto).
        var plan = await PlanDeletionAsync(db, userId, ct);
        if (plan.FirstOrDefault(p => p.Outcome == "Blocked") is { Membership: { } blocked })
        {
            return Results.Problem(
                title: $"Antes de apagares a conta, passa a administração de «{blocked.Household.Name}» a um adulto.",
                statusCode: StatusCodes.Status409Conflict);
        }

        foreach (var (membership, outcome, successor) in plan)
        {
            if (outcome == "Deleted")
            {
                // Mais ninguém com conta: a casa (e os perfis de criança) vão com ela.
                await memberships.DeleteHouseholdAsync(membership.HouseholdId, ct);
                continue;
            }

            if (successor is not null)
            {
                // A casa fica sempre com um administrador: o adulto que está lá há mais tempo.
                successor.Role = HouseholdRole.Admin;
                await db.SaveChangesAsync(ct);
                await notifier.MemberUpserted(membership.HouseholdId, MemberEndpoints.ToDto(successor), ct);
            }

            await memberships.RemoveAsync(membership, ct);
        }

        // As ligações de tempo real e os telemóveis desta conta deixam de receber já.
        await sessions.SignOutEverywhereAsync(userId, ct);

        // As fotos que enviou saem do histórico; o resto (sessões, avisos, telemóveis, sugestões) vai com a conta.
        var photos = await db.ChorePhotos.Where(p => p.UploadedByUserId == userId).Select(p => p.Id).ToListAsync(ct);
        if (photos.Count > 0)
        {
            await db.ChoreCompletions.Where(c => c.PhotoId != null && photos.Contains(c.PhotoId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.PhotoId, (Guid?)null), ct);
            await db.ChorePhotos.Where(p => photos.Contains(p.Id)).ExecuteDeleteAsync(ct);
        }

        db.ChangeTracker.Clear();
        await db.Users.Where(u => u.Id == userId).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }
}
