using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using Kubico.Api.Auth;
using Kubico.Api.Activity;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Api.Support;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Households;

/// <summary>Criar uma casa; com <c>Template</c>, já vem com as listas, tarefas e recompensas desse modelo.</summary>
public record CreateHouseholdRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Name,
    [property: StringLength(64)] string? TimeZoneId = null,
    HouseholdTemplate Template = HouseholdTemplate.Blank);

/// <summary>
/// Um convite com o papel de quem entra (não há convites para administrador: passa-se a administração depois).
/// <c>MaxUses</c>: quantas pessoas podem entrar com ele (por omissão uma, nos de adulto; sem limite nos outros).
/// </summary>
public record CreateInviteRequest(HouseholdRole Role = HouseholdRole.Adult, int ValidForDays = 7, int? MaxUses = null);

/// <summary><c>Access</c>: o que o meu papel me deixa fazer em cada área da casa.</summary>
public record HouseholdSummaryDto(
    Guid Id, string Name, HouseholdRole Role, IReadOnlyDictionary<HouseholdModule, ModuleAccess> Access);

/// <summary>Um membro da casa. <c>IsChildProfile</c>: perfil de criança, sem conta própria (entra com PIN).</summary>
public record MemberDto(
    Guid UserId, string DisplayName, HouseholdRole Role, string Color, bool IsChildProfile = false, DateOnly? BirthDate = null);

public record HouseholdDto(
    Guid Id,
    string Name,
    string TimeZoneId,
    HouseholdRole MyRole,
    IReadOnlyList<MemberDto> Members,
    IReadOnlyDictionary<HouseholdModule, ModuleAccess> MyAccess);

public record InviteDto(
    string Code, HouseholdRole Role, DateTimeOffset ExpiresAt, int? MaxUses = null, int UsedCount = 0, DateTimeOffset? CreatedAt = null);

public record InvitePreviewDto(
    Guid HouseholdId, string HouseholdName, HouseholdRole Role, DateTimeOffset ExpiresAt, bool AlreadyMember);

/// <summary><c>IsSupport</c>: esta conta mantém o Kubico e lê as mensagens enviadas da ajuda.</summary>
public record MeDto(UserDto User, IReadOnlyList<HouseholdSummaryDto> Households, bool IsSupport = false);

public static class HouseholdEndpoints
{
    // Sem 0/O, 1/I/L: códigos fáceis de ditar ou escrever à mão.
    private const string InviteAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int InviteCodeLength = 8;

    // Cores dos membros: cada um fica com uma que ninguém da casa usa (só se repetem quando as 8 estão em uso).
    private static readonly string[] MemberColors =
        ["#4F7CAC", "#E07A5F", "#81B29A", "#F2CC8F", "#9C6ADE", "#3D9970", "#D96C9C", "#5BC0BE"];

    public static RouteGroupBuilder MapHouseholdEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", GetMe).WithTags("Households").RequireAuthorization();
        api.MapGet("/household-templates", (HttpContext http) => Results.Ok(HouseholdTemplates.List(Texts.LanguageOf(http)))).WithTags("Households").RequireAuthorization();

        var households = api.MapGroup("/households").WithTags("Households").RequireAuthorization();
        households.MapPost("/", CreateHousehold);
        households.MapGet("/{householdId:guid}", GetHousehold);
        households.MapPost("/{householdId:guid}/invites", CreateInvite).RequireActiveSession();
        households.MapGet("/{householdId:guid}/invites", GetInvites);
        households.MapDelete("/{householdId:guid}/invites/{code}", RevokeInvite).RequireActiveSession();

        api.MapGet("/invites/{code}", PreviewInvite).WithTags("Households").RequireAuthorization();
        api.MapPost("/invites/{code}/accept", AcceptInvite).WithTags("Households").RequireAuthorization();

        return api;
    }

    private static async Task<IResult> GetMe(
        ClaimsPrincipal principal, KubicoDbContext db, IOptions<SupportOptions> support, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var memberships = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Household.Name)
            .Select(m => new { m.HouseholdId, m.Household.Name, m.Role })
            .ToListAsync(ct);
        var ids = memberships.Select(m => m.HouseholdId).ToList();
        var chosen = await db.HouseholdPermissions.AsNoTracking().Where(p => ids.Contains(p.HouseholdId)).ToListAsync(ct);
        var households = memberships
            .Select(m => new HouseholdSummaryDto(
                m.HouseholdId, m.Name, m.Role, ModulePermissions.ResolveAll(m.Role, chosen.Where(p => p.HouseholdId == m.HouseholdId))))
            .ToList();

        return Results.Ok(new MeDto(user.ToDto(), households, support.Value.IsAdmin(user.Email)));
    }

    private static async Task<IResult> CreateHousehold(
        CreateHouseholdRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct)
    {
        var timeZoneId = string.IsNullOrWhiteSpace(request.TimeZoneId) ? Household.DefaultTimeZoneId : request.TimeZoneId.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.TimeZoneId)] = ["Fuso horário desconhecido (usa um nome IANA, ex.: Europe/Lisbon)."],
            });
        }

        if (!Enum.IsDefined(request.Template))
        {
            return Results.Problem(title: "Esse modelo de casa não existe.", statusCode: StatusCodes.Status400BadRequest);
        }

        var userId = principal.GetUserId();
        if (await IsChildProfile(db, userId, ct))
        {
            return ChildProfileForbidden();
        }

        var now = clock.GetUtcNow();

        var household = new Household { Name = request.Name.Trim(), TimeZoneId = timeZoneId, CreatedAt = now };
        household.Members.Add(new Membership
        {
            UserId = userId,
            Role = HouseholdRole.Admin,
            Color = MemberColors[0],
            JoinedAt = now,
        });

        db.Households.Add(household);
        HouseholdTemplates.Apply(db, household, request.Template, userId, now, Texts.LanguageOf(http));
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/households/{household.Id}",
            new HouseholdSummaryDto(household.Id, household.Name, HouseholdRole.Admin, ModulePermissions.ResolveAll(HouseholdRole.Admin, [])));
    }

    private static async Task<IResult> GetHousehold(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, CancellationToken ct)
    {
        var (role, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct);
        if (error is not null)
        {
            return error;
        }

        var household = await db.Households.AsNoTracking()
            .Where(h => h.Id == householdId)
            .Select(h => new
            {
                h.Id,
                h.Name,
                h.TimeZoneId,
                Members = h.Members
                    .OrderBy(m => m.User.DisplayName)
                    .Select(m => new MemberDto(
                        m.UserId, m.User.DisplayName, m.Role, m.Color, m.User.ManagedByHouseholdId != null, m.User.BirthDate))
                    .ToList(),
            })
            .SingleAsync(ct);

        // A data de nascimento das crianças só vai para quem gere os perfis (minimização de dados).
        var members = role!.Value.CanManageContent()
            ? household.Members
            : [.. household.Members.Select(m => m with { BirthDate = null })];
        var myAccess = await access.GetAllAccessAsync(householdId, role.Value, ct);
        return Results.Ok(new HouseholdDto(
            household.Id, household.Name, household.TimeZoneId, role.Value, members, myAccess));
    }

    private static async Task<IResult> CreateInvite(
        Guid householdId,
        CreateInviteRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, error) = await access.RequireMemberAsync(userId, householdId, ct, r => r.CanManageMembers());
        if (error is not null)
        {
            return error;
        }

        if (request.ValidForDays is < 1 or > 30)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.ValidForDays)] = ["O convite tem de ser válido entre 1 e 30 dias."],
            });
        }

        if (!Enum.IsDefined(request.Role))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["Role"] = ["Esse papel não existe."] });
        }

        if (request.Role == HouseholdRole.Admin)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Role"] = ["Não se convida para administrador: depois de a pessoa entrar, usa «Passar a administração»."],
            });
        }

        if (request.MaxUses is < 1 or > 50)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.MaxUses)] = ["O convite pode servir para 1 a 50 pessoas."],
            });
        }

        var now = clock.GetUtcNow();
        var invite = new Invite
        {
            HouseholdId = householdId,
            Code = GenerateCode(),
            Role = request.Role,
            CreatedByUserId = userId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(request.ValidForDays),
            MaxUses = request.MaxUses ?? (request.Role == HouseholdRole.Adult ? 1 : null),
        };

        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);

        return Results.Ok(ToDto(invite));
    }

    /// <summary>Os convites que ainda servem (não expiraram, não foram anulados nem gastos), dos mais recentes primeiro.</summary>
    private static async Task<IResult> GetInvites(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, TimeProvider clock,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct, r => r.CanManageMembers());
        if (error is not null)
        {
            return error;
        }

        var now = clock.GetUtcNow();
        var invites = await db.Invites.AsNoTracking().Where(i => i.HouseholdId == householdId).ToListAsync(ct);
        return Results.Ok(invites.Where(i => i.IsValid(now)).OrderByDescending(i => i.CreatedAt).Select(ToDto));
    }

    /// <summary>Anula um convite (por exemplo, o link foi parar a quem não devia): deixa de servir já.</summary>
    private static async Task<IResult> RevokeInvite(
        Guid householdId, string code, ClaimsPrincipal principal, KubicoDbContext db, ActivityLog activity,
        HouseholdAccess access, CancellationToken ct)
    {
        var me = principal.GetUserId();
        var (_, error) = await access.RequireMemberAsync(me, householdId, ct, r => r.CanManageMembers());
        if (error is not null)
        {
            return error;
        }

        var normalized = code.Trim().ToUpperInvariant();
        var invite = await db.Invites.SingleOrDefaultAsync(i => i.Code == normalized && i.HouseholdId == householdId, ct);
        if (invite is null)
        {
            return Results.NotFound();
        }

        db.Invites.Remove(invite);
        activity.Capture(householdId, me, null, $"anulou o convite {invite.Code}", undoable: false);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>Antes de aceitar: que casa é, com que papel, e se a pessoa já lá está (para a app perguntar).</summary>
    private static async Task<IResult> PreviewInvite(
        string code, ClaimsPrincipal principal, KubicoDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var normalized = code.Trim().ToUpperInvariant();
        var invite = await db.Invites.AsNoTracking()
            .Include(i => i.Household)
            .SingleOrDefaultAsync(i => i.Code == normalized, ct);
        if (invite is null || !invite.IsValid(clock.GetUtcNow()))
        {
            return Results.Problem(title: "Convite inválido ou expirado.", statusCode: StatusCodes.Status404NotFound);
        }

        var alreadyMember = await db.Memberships.AnyAsync(m => m.HouseholdId == invite.HouseholdId && m.UserId == userId, ct);
        return Results.Ok(new InvitePreviewDto(invite.HouseholdId, invite.Household.Name, invite.Role, invite.ExpiresAt, alreadyMember));
    }

    private static async Task<IResult> AcceptInvite(
        string code,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        if (await IsChildProfile(db, userId, ct))
        {
            return ChildProfileForbidden();
        }

        var now = clock.GetUtcNow();
        var normalized = code.Trim().ToUpperInvariant();

        var invite = await db.Invites
            .Include(i => i.Household)
            .SingleOrDefaultAsync(i => i.Code == normalized, ct);

        if (invite is null || invite.IsExpired(now))
        {
            return Results.Problem(
                title: "Convite inválido ou expirado.",
                statusCode: StatusCodes.Status404NotFound);
        }

        if (await AlreadyMemberAsync(db, access, invite, userId, ct) is { } member)
        {
            return member;
        }

        // Conta a utilização e entra na mesma transação: com pedidos em simultâneo, um convite de uma pessoa só deixa
        // entrar uma (o UPDATE condicional só passa enquanto houver utilizações).
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Invites
            .Where(i => i.Id == invite.Id && (i.MaxUses == null || i.UsedCount < i.MaxUses))
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.UsedCount, i => i.UsedCount + 1), ct);
        if (claimed == 0)
        {
            return Results.Problem(title: "Convite inválido ou expirado.", statusCode: StatusCodes.Status404NotFound);
        }

        var membership = new Membership
        {
            HouseholdId = invite.HouseholdId,
            User = await db.Users.SingleAsync(u => u.Id == userId, ct),
            Role = invite.Role,
            Color = await NextColorAsync(db, invite.HouseholdId, ct),
            JoinedAt = now,
        };
        db.Memberships.Add(membership);
        activity.Capture(invite.HouseholdId, userId, null, "entrou na casa", undoable: false);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException e) when (e is not DbUpdateConcurrencyException)
        {
            // A mesma pessoa aceitou duas vezes ao mesmo tempo (índice único casa + pessoa): já está dentro.
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            if (await AlreadyMemberAsync(db, access, invite, userId, ct) is { } joined)
            {
                return joined;
            }

            throw;
        }

        // Quem já está na casa vê o novo membro aparecer.
        await notifier.MemberUpserted(invite.HouseholdId, MemberEndpoints.ToDto(membership), ct);

        return Results.Ok(new HouseholdSummaryDto(
            invite.HouseholdId, invite.Household.Name, invite.Role,
            await access.GetAllAccessAsync(invite.HouseholdId, invite.Role, ct)));
    }

    /// <summary>Um código de convite ou da família: 8 letras e algarismos fáceis de ditar.</summary>
    internal static string GenerateCode() =>
        RandomNumberGenerator.GetString(InviteAlphabet, InviteCodeLength);

    /// <summary>
    /// A cor de quem entra: a primeira que ninguém da casa usa; com as 8 em uso, a que menos se repete.
    /// </summary>
    internal static async Task<string> NextColorAsync(KubicoDbContext db, Guid householdId, CancellationToken ct)
    {
        var used = await db.Memberships.Where(m => m.HouseholdId == householdId).Select(m => m.Color).ToListAsync(ct);
        return MemberColors
            .Select((color, index) => (color, index, count: used.Count(c => string.Equals(c, color, StringComparison.OrdinalIgnoreCase))))
            .OrderBy(c => c.count)
            .ThenBy(c => c.index)
            .First().color;
    }

    private static async Task<IResult?> AlreadyMemberAsync(
        KubicoDbContext db, HouseholdAccess access, Invite invite, Guid userId, CancellationToken ct)
    {
        var existing = await db.Memberships.AsNoTracking()
            .SingleOrDefaultAsync(m => m.HouseholdId == invite.HouseholdId && m.UserId == userId, ct);
        return existing is null
            ? null
            : Results.Ok(new HouseholdSummaryDto(
                invite.HouseholdId, invite.Household.Name, existing.Role,
                await access.GetAllAccessAsync(invite.HouseholdId, existing.Role, ct)));
    }

    private static InviteDto ToDto(Invite i) => new(i.Code, i.Role, i.ExpiresAt, i.MaxUses, i.UsedCount, i.CreatedAt);

    /// <summary>Os perfis de criança só existem na sua casa: não criam outras nem aceitam convites.</summary>
    private static Task<bool> IsChildProfile(KubicoDbContext db, Guid userId, CancellationToken ct) =>
        db.Users.AnyAsync(u => u.Id == userId && u.ManagedByHouseholdId != null, ct);

    private static IResult ChildProfileForbidden() => Results.Problem(
        title: "Os perfis de criança não podem criar nem juntar-se a outras casas.",
        statusCode: StatusCodes.Status403Forbidden);
}
