using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using Kubico.Api.Auth;
using Kubico.Api.Common;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Households;

public record CreateHouseholdRequest(
    [property: Required, StringLength(100, MinimumLength = 1)] string Name);

public record CreateInviteRequest(HouseholdRole Role = HouseholdRole.Adult, int ValidForDays = 7);

public record HouseholdSummaryDto(Guid Id, string Name, HouseholdRole Role);

public record MemberDto(Guid UserId, string DisplayName, HouseholdRole Role, string Color);

public record HouseholdDto(Guid Id, string Name, HouseholdRole MyRole, IReadOnlyList<MemberDto> Members);

public record InviteDto(string Code, HouseholdRole Role, DateTimeOffset ExpiresAt);

public record MeDto(UserDto User, IReadOnlyList<HouseholdSummaryDto> Households);

public static class HouseholdEndpoints
{
    // Sem 0/O, 1/I/L: códigos fáceis de ditar ou escrever à mão.
    private const string InviteAlphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int InviteCodeLength = 8;

    // Cores atribuídas a novos membros, por ordem de entrada.
    private static readonly string[] MemberColors =
        ["#4F7CAC", "#E07A5F", "#81B29A", "#F2CC8F", "#9C6ADE", "#3D9970", "#D96C9C", "#5BC0BE"];

    public static RouteGroupBuilder MapHouseholdEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/me", GetMe).WithTags("Households").RequireAuthorization();

        var households = api.MapGroup("/households").WithTags("Households").RequireAuthorization();
        households.MapPost("/", CreateHousehold);
        households.MapGet("/{householdId:guid}", GetHousehold);
        households.MapPost("/{householdId:guid}/invites", CreateInvite);

        api.MapPost("/invites/{code}/accept", AcceptInvite).WithTags("Households").RequireAuthorization();

        return api;
    }

    private static async Task<IResult> GetMe(ClaimsPrincipal principal, KubicoDbContext db, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
        {
            return Results.Unauthorized();
        }

        var households = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId)
            .OrderBy(m => m.Household.Name)
            .Select(m => new HouseholdSummaryDto(m.HouseholdId, m.Household.Name, m.Role))
            .ToListAsync(ct);

        return Results.Ok(new MeDto(user.ToDto(), households));
    }

    private static async Task<IResult> CreateHousehold(
        CreateHouseholdRequest request, ClaimsPrincipal principal, KubicoDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var now = clock.GetUtcNow();

        var household = new Household { Name = request.Name.Trim(), CreatedAt = now };
        household.Members.Add(new Membership
        {
            UserId = userId,
            Role = HouseholdRole.Admin,
            Color = MemberColors[0],
            JoinedAt = now,
        });

        db.Households.Add(household);
        await db.SaveChangesAsync(ct);

        return Results.Created(
            $"/api/households/{household.Id}",
            new HouseholdSummaryDto(household.Id, household.Name, HouseholdRole.Admin));
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
                Members = h.Members
                    .OrderBy(m => m.User.DisplayName)
                    .Select(m => new MemberDto(m.UserId, m.User.DisplayName, m.Role, m.Color))
                    .ToList(),
            })
            .SingleAsync(ct);

        return Results.Ok(new HouseholdDto(household.Id, household.Name, role!.Value, household.Members));
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

        var now = clock.GetUtcNow();
        var invite = new Invite
        {
            HouseholdId = householdId,
            Code = GenerateInviteCode(),
            Role = request.Role,
            CreatedByUserId = userId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(request.ValidForDays),
        };

        db.Invites.Add(invite);
        await db.SaveChangesAsync(ct);

        return Results.Ok(new InviteDto(invite.Code, invite.Role, invite.ExpiresAt));
    }

    private static async Task<IResult> AcceptInvite(
        string code, ClaimsPrincipal principal, KubicoDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
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

        var existing = await db.Memberships
            .SingleOrDefaultAsync(m => m.HouseholdId == invite.HouseholdId && m.UserId == userId, ct);
        if (existing is not null)
        {
            return Results.Ok(new HouseholdSummaryDto(invite.HouseholdId, invite.Household.Name, existing.Role));
        }

        var memberCount = await db.Memberships.CountAsync(m => m.HouseholdId == invite.HouseholdId, ct);
        db.Memberships.Add(new Membership
        {
            HouseholdId = invite.HouseholdId,
            UserId = userId,
            Role = invite.Role,
            Color = MemberColors[memberCount % MemberColors.Length],
            JoinedAt = now,
        });
        await db.SaveChangesAsync(ct);

        return Results.Ok(new HouseholdSummaryDto(invite.HouseholdId, invite.Household.Name, invite.Role));
    }

    private static string GenerateInviteCode() =>
        RandomNumberGenerator.GetString(InviteAlphabet, InviteCodeLength);
}
