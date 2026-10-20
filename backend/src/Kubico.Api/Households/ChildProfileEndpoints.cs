using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Activity;
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

        household.MapPost("/children", CreateChild);
        household.MapPut("/children/{userId:guid}", UpdateChild);
        household.MapPut("/children/{userId:guid}/pin", ChangePin);
        household.MapPost("/family-code", GetOrCreateFamilyCode);
        household.MapDelete("/family-code", RevokeFamilyCode);

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
        var child = new User
        {
            DisplayName = request.DisplayName.Trim(),
            ManagedByHouseholdId = householdId,
            BirthDate = request.BirthDate,
            CreatedAt = now,
        };
        child.PinHash = hasher.HashPassword(child, request.Pin);

        var memberCount = await db.Memberships.CountAsync(m => m.HouseholdId == householdId, ct);
        var membership = new Membership
        {
            HouseholdId = householdId,
            User = child,
            Role = HouseholdRole.Child,
            Color = HouseholdEndpoints.ColorFor(memberCount),
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

        membership.User.DisplayName = request.DisplayName.Trim();
        membership.User.BirthDate = request.BirthDate;
        await db.SaveChangesAsync(ct);

        var dto = MemberEndpoints.ToDto(membership);
        await notifier.MemberUpserted(householdId, dto, ct);
        return Results.Ok(dto);
    }

    /// <summary>Muda o PIN (por exemplo, quando a criança se esquece dele) e desbloqueia o perfil.</summary>
    private static async Task<IResult> ChangePin(
        Guid householdId,
        Guid userId,
        ChildPinRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        IPasswordHasher<User> hasher,
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

    /// <summary>O código atual deixa de servir (as sessões já abertas continuam); o próximo pedido gera outro.</summary>
    private static async Task<IResult> RevokeFamilyCode(
        Guid householdId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, CancellationToken ct)
    {
        var (_, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct, r => r.CanManageContent());
        if (error is not null)
        {
            return error;
        }

        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        household.ChildSignInCode = null;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static Task<Membership?> FindChild(KubicoDbContext db, Guid householdId, Guid userId, CancellationToken ct) =>
        db.Memberships.Include(m => m.User).SingleOrDefaultAsync(
            m => m.HouseholdId == householdId && m.UserId == userId && m.User.ManagedByHouseholdId == householdId, ct);

    /// <summary>O PIN tem 4 a 6 algarismos.</summary>
    internal static IResult? InvalidPin(string? pin) =>
        pin is { Length: >= 4 and <= 6 } && pin.All(char.IsAsciiDigit)
            ? null
            : Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["Pin"] = ["O PIN tem de ter 4 a 6 algarismos."],
            });
}
