using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Activity;
using Kubico.Api.Auth;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Households;
using Kubico.Domain.Users;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Households;

public record CreateChildProfileRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string DisplayName,
    [property: Required] string Pin,
    DateOnly? BirthDate = null);

public record UpdateChildProfileRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string DisplayName,
    DateOnly? BirthDate = null);

public record ChildPinRequest([property: Required] string Pin);

/// <summary>O código da família: com ele e o PIN, cada criança entra no seu perfil.</summary>
public record FamilyCodeDto(string Code);

/// <summary>
/// Perfis de criança: membros sem email nem palavra-passe, criados por um adulto, que entram com o código da família
/// e um PIN. Pertencem só a esta casa e têm sempre o papel Criança.
/// </summary>
public static class ChildProfileEndpoints
{
    public static RouteGroupBuilder MapChildProfileEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup("/households/{householdId:guid}")
            .WithTags("Households")
            .RequireAuthorization();

        household.MapPost("/children", CreateChild).RequireActiveSession();
        household.MapPut("/children/{userId:guid}", UpdateChild).RequireActiveSession();
        household.MapPut("/children/{userId:guid}/pin", ChangePin).RequireActiveSession();
        household.MapPost("/children/{userId:guid}/sign-out", SignOutChild).RequireActiveSession();
        household.MapPost("/family-code", GetOrCreateFamilyCode).RequireActiveSession();
        household.MapDelete("/family-code", RevokeFamilyCode).RequireActiveSession();

        return api;
    }

    private static async Task<IResult> CreateChild(
        Guid householdId,
        CreateChildProfileRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        IPasswordHasher<User> hasher,
        HouseholdNotifier notifier,
        TimeProvider clock,
        HttpContext http,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        if (InvalidPin(request.Pin) is { } invalid)
        {
            return invalid;
        }

        var now = clock.GetUtcNow();
        if (await InvalidBirthDate(db, householdId, request.BirthDate, now, ct) is { } badDate)
        {
            return badDate;
        }

        var child = new User
        {
            DisplayName = request.DisplayName.Trim(),
            Language = Texts.LanguageOf(http),
            ManagedByHouseholdId = householdId,
            BirthDate = request.BirthDate,
            CreatedAt = now,
        };
        child.PinHash = hasher.HashPassword(child, request.Pin);

        var membership = new Membership
        {
            HouseholdId = householdId,
            User = child,
            Role = HouseholdRole.Child,
            Color = await HouseholdEndpoints.NextColorAsync(db, householdId, ct),
            JoinedAt = now,
        };
        db.Users.Add(child);
        db.Memberships.Add(membership);
        activity.Capture(householdId, principal.GetUserId(), null, $"criou o perfil de criança de {membership.User.DisplayName}", undoable: false);
        await db.SaveChangesAsync(ct);

        var dto = MemberEndpoints.ToDto(membership);
        await notifier.MemberUpserted(householdId, dto, ct);
        return Results.Created($"/api/households/{householdId}/members/{child.Id}", dto);
    }

    private static async Task<IResult> UpdateChild(
        Guid householdId,
        Guid userId,
        UpdateChildProfileRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var membership = await FindChild(db, householdId, userId, ct);
        if (membership is null)
        {
            return Results.NotFound();
        }

        if (await InvalidBirthDate(db, householdId, request.BirthDate, clock.GetUtcNow(), ct) is { } badDate)
        {
            return badDate;
        }

        membership.User.DisplayName = request.DisplayName.Trim();
        membership.User.BirthDate = request.BirthDate;
        await db.SaveChangesAsync(ct);

        var dto = MemberEndpoints.ToDto(membership);
        await notifier.MemberUpserted(householdId, dto, ct);
        return Results.Ok(dto);
    }

    /// <summary>
    /// Muda o PIN (por exemplo, quando a criança se esquece dele, ou alguém o viu) e desbloqueia o perfil. As sessões
    /// já abertas do perfil terminam: quem entrou com o PIN antigo noutro aparelho sai.
    /// </summary>
    private static async Task<IResult> ChangePin(
        Guid householdId,
        Guid userId,
        ChildPinRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        IPasswordHasher<User> hasher,
        SessionService sessions,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var membership = await FindChild(db, householdId, userId, ct);
        if (membership is null)
        {
            return Results.NotFound();
        }

        if (InvalidPin(request.Pin) is { } invalid)
        {
            return invalid;
        }

        membership.User.PinHash = hasher.HashPassword(membership.User, request.Pin);
        membership.User.RegisterRightPin();
        activity.Capture(householdId, principal.GetUserId(), null, $"mudou o PIN de {membership.User.DisplayName}", undoable: false);
        await db.SaveChangesAsync(ct);
        await sessions.SignOutEverywhereAsync(userId, ct);
        return Results.NoContent();
    }

    /// <summary>Termina as sessões do perfil de criança em todos os aparelhos (sem mudar o PIN).</summary>
    private static async Task<IResult> SignOutChild(
        Guid householdId,
        Guid userId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        ActivityLog activity,
        HouseholdAccess access,
        SessionService sessions,
        CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var membership = await FindChild(db, householdId, userId, ct);
        if (membership is null)
        {
            return Results.NotFound();
        }

        await sessions.SignOutEverywhereAsync(userId, ct);
        activity.Capture(householdId, principal.GetUserId(), null,
            $"terminou as sessões de {membership.User.DisplayName}", undoable: false);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetOrCreateFamilyCode(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        if (household.ChildSignInCode is null)
        {
            household.ChildSignInCode = HouseholdEndpoints.GenerateCode();
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(new FamilyCodeDto(household.ChildSignInCode));
    }

    /// <summary>
    /// O código atual deixa de servir e as sessões abertas dos perfis de criança da casa terminam (quem tinha o código
    /// já não entra, nem fica lá dentro); o próximo pedido gera outro código.
    /// </summary>
    private static async Task<IResult> RevokeFamilyCode(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, ActivityLog activity, HouseholdAccess access,
        SessionService sessions, CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        household.ChildSignInCode = null;
        activity.Capture(householdId, principal.GetUserId(), null, "renovou o código da família", undoable: false);
        await db.SaveChangesAsync(ct);

        var children = await db.Users.Where(u => u.ManagedByHouseholdId == householdId).Select(u => u.Id).ToListAsync(ct);
        foreach (var child in children)
        {
            await sessions.SignOutEverywhereAsync(child, ct);
        }

        return Results.NoContent();
    }

    private static Task<Membership?> FindChild(KubicoDbContext db, Guid householdId, Guid userId, CancellationToken ct) =>
        db.Memberships.Include(m => m.User).SingleOrDefaultAsync(
            m => m.HouseholdId == householdId && m.UserId == userId && m.User.ManagedByHouseholdId == householdId, ct);

    /// <summary>A idade mínima de um adulto: um perfil de criança tem menos.</summary>
    private const int AdultAge = 18;

    /// <summary>A data de nascimento (opcional) não pode ser no futuro nem de alguém com 18 anos ou mais.</summary>
    private static async Task<IResult?> InvalidBirthDate(
        KubicoDbContext db, Guid householdId, DateOnly? birthDate, DateTimeOffset now, CancellationToken ct)
    {
        if (birthDate is not { } date)
        {
            return null;
        }

        var today = (await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct)).Today(now);
        return date <= today && date > today.AddYears(-AdultAge)
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["BirthDate"] = ["A data de nascimento não pode ser no futuro, e um perfil de criança é para quem tem menos de 18 anos."],
            });
    }

    /// <summary>O PIN tem 4 a 6 algarismos.</summary>
    internal static IResult? InvalidPin(string? pin) =>
        pin is { Length: >= 4 and <= 6 } && pin.All(char.IsAsciiDigit)
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Pin"] = ["O PIN tem de ter 4 a 6 algarismos."],
            });
}
