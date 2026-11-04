using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using Kubico.Api.Activity;
using Kubico.Api.Auth;
using Kubico.Api.Common;
using Kubico.Domain.Assistant;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Assistant;

public enum AssistantActionKind
{
    AddItems,
    CheckItems,
    CreateEvent,
    CreateChore,
    CreateExpense,
}

/// <summary>O assistente nesta casa: se existe no servidor, se a casa o tem ligado e quantos pedidos já fez hoje.</summary>
public record AssistantStatusDto(bool Available, bool Enabled, int UsedToday, int DailyLimit);

public record AssistantSettingsRequest(bool Enabled);

/// <summary>
/// Uma frase (até 1000 caracteres) e/ou um ficheiro em base64: a fotografia de um talão (JPEG, PNG ou WebP até 5 MB)
/// ou um PDF (até 10 MB), como o calendário da escola.
/// </summary>
public record AssistantRequest([property: MaxLength(1000)] string? Text, string? FileBase64, string? MediaType);

/// <summary>
/// Uma ação proposta, já validada e pronta para a app executar pelos endpoints normais. Eventos com hora levam
/// <c>StartsAt</c>/<c>EndsAt</c> (UTC, no fuso da casa); de dia inteiro, <c>Date</c>/<c>EndDate</c>.
/// </summary>
public record AssistantActionDto(
    AssistantActionKind Kind,
    Guid? ListId = null,
    string? ListName = null,
    IReadOnlyList<string>? Items = null,
    IReadOnlyList<Guid>? ItemIds = null,
    string? Title = null,
    DateOnly? Date = null,
    DateOnly? EndDate = null,
    DateTimeOffset? StartsAt = null,
    DateTimeOffset? EndsAt = null,
    int? AmountCents = null,
    string? Location = null);

/// <summary>
/// As propostas e a resposta do assistente. <c>Skipped</c> conta as que se deixaram de fora (sem permissão para as
/// fazer ou com dados que não se aproveitam).
/// </summary>
public record AssistantProposalDto(string? Reply, IReadOnlyList<AssistantActionDto> Actions, int Skipped, int UsedToday, int DailyLimit);

/// <summary>
/// O assistente (F4-24): frase, talão ou PDF da escola → ações propostas. Só propõe; a app pede confirmação e cada
/// ação passa pelos endpoints normais. As crianças não o usam e nada sobre elas vai para o modelo.
/// </summary>
public static class AssistantEndpoints
{
    public const int MaxImageBytes = 5 * 1024 * 1024;
    public const int MaxPdfBytes = 10 * 1024 * 1024;
    private const int MaxActions = 40;
    private const int MaxItemsPerAction = 50;
    private const int MaxUncheckedSent = 300;

    public static RouteGroupBuilder MapAssistantEndpoints(this RouteGroupBuilder api)
    {
        var household = api.MapGroup("/households/{householdId:guid}/assistant")
            .WithTags("Assistant")
            .RequireAuthorization();

        household.MapGet("/", GetStatus);
        household.MapPost("/", Propose);
        household.MapPut("/settings", SaveSettings).RequireActiveSession();
        return api;
    }

    private static async Task<IResult> GetStatus(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        IAssistant assistant,
        IOptions<AssistantOptions> options,
        TimeProvider clock,
        CancellationToken ct)
    {
        var (role, error) = await access.RequireMemberAsync(principal.GetUserId(), householdId, ct);
        if (error is not null)
        {
            return error;
        }

        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        var today = household.Today(clock.GetUtcNow());
        var used = await db.AssistantUsages.Where(u => u.HouseholdId == householdId && u.Day == today)
            .Select(u => u.Count).SingleOrDefaultAsync(ct);
        return Results.Ok(new AssistantStatusDto(
            assistant.IsAvailable && role != HouseholdRole.Child, household.AssistantEnabled, used, options.Value.DailyLimit));
    }

    /// <summary>Ligar ou desligar o assistente na casa: só o administrador.</summary>
    private static async Task<IResult> SaveSettings(
        Guid householdId,
        AssistantSettingsRequest request,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        ActivityLog activity,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, error) = await access.RequireMemberAsync(userId, householdId, ct, r => r.CanManageMembers());
        if (error is not null)
        {
            return error;
        }

        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        household.AssistantEnabled = request.Enabled;
        activity.Capture(householdId, userId, null,
            request.Enabled ? "ligou o assistente" : "desligou o assistente", undoable: false);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> Propose(
        Guid householdId,
        AssistantRequest request,
        ClaimsPrincipal principal,
        HttpContext http,
        KubicoDbContext db,
        HouseholdAccess access,
        IAssistant assistant,
        IOptions<AssistantOptions> options,
        TimeProvider clock,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (role, error) = await access.RequireMemberAsync(userId, householdId, ct);
        if (error is not null)
        {
            return error;
        }

        if (role == HouseholdRole.Child)
        {
            return Results.Problem(title: "O assistente não está disponível para as crianças.", statusCode: StatusCodes.Status403Forbidden);
        }

        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        if (!assistant.IsAvailable)
        {
            return Results.Problem(title: "O assistente não está disponível neste servidor.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (!household.AssistantEnabled)
        {
            return Results.Problem(title: "O assistente está desligado nesta casa.", statusCode: StatusCodes.Status409Conflict);
        }

        var (file, mediaType, invalid) = ReadFile(request);
        if (invalid is not null)
        {
            return invalid;
        }

        if (file is null && string.IsNullOrWhiteSpace(request.Text))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["text"] = ["Escreve o que queres ou junta uma fotografia ou um PDF."],
            });
        }

        var now = clock.GetUtcNow();
        var today = household.Today(now);
        var limit = options.Value.DailyLimit;
        if (!await ReserveAsync(db, householdId, today, limit, ct))
        {
            return Results.Problem(
                title: "A casa já usou todos os pedidos ao assistente de hoje. Volta a tentar amanhã.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        var levels = await access.GetAllAccessAsync(householdId, role!.Value, ct);
        bool Can(HouseholdModule module, ModuleAccess needed) => levels.GetValueOrDefault(module) >= needed;

        // O que o modelo precisa de saber da casa: as listas e, com um ficheiro (talão), os artigos por comprar.
        var lists = Can(HouseholdModule.Shopping, ModuleAccess.View)
            ? await db.ShoppingLists.AsNoTracking().Where(l => l.HouseholdId == householdId)
                .OrderBy(l => l.CreatedAt).Select(l => new { l.Id, l.Name }).ToListAsync(ct)
            : [];
        var toBuy = file is not null && lists.Count > 0
            ? await db.ShoppingItems.AsNoTracking()
                .Where(i => i.List.HouseholdId == householdId && !i.IsChecked)
                .OrderBy(i => i.CreatedAt).Take(MaxUncheckedSent)
                .Select(i => new { i.Id, i.ListId, i.Name }).ToListAsync(ct)
            : [];

        var language = Texts.LanguageOf(http);
        AssistantDraft draft;
        try
        {
            draft = await assistant.ProposeAsync(new AssistantPrompt(
                request.Text?.Trim(), file, mediaType, today, language,
                [.. lists.Select(l => l.Name)], [.. toBuy.Select(i => i.Name).Distinct()]), ct);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            // Uma falha do serviço não gasta o pedido do dia.
            loggers.CreateLogger(typeof(AssistantEndpoints)).LogWarning(e, "O assistente falhou na casa {HouseholdId}.", householdId);
            await db.AssistantUsages.Where(u => u.HouseholdId == householdId && u.Day == today && u.Count > 0)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Count, u => u.Count - 1), CancellationToken.None);
            return Results.Problem(title: "O assistente não respondeu. Tenta outra vez daqui a pouco.", statusCode: StatusCodes.Status502BadGateway);
        }

        var actions = new List<AssistantActionDto>();
        var skipped = 0;
        foreach (var draftAction in draft.Actions.Take(MaxActions))
        {
            var mapped = Map(draftAction);
            if (mapped.Count == 0)
            {
                skipped++;
            }

            actions.AddRange(mapped);
        }

        skipped += Math.Max(0, draft.Actions.Count - MaxActions);
        var used = await db.AssistantUsages.Where(u => u.HouseholdId == householdId && u.Day == today)
            .Select(u => u.Count).SingleOrDefaultAsync(ct);
        var reply = draft.Reply is { Length: > 0 } r ? r.Trim()[..Math.Min(r.Trim().Length, 500)] : null;
        return Results.Ok(new AssistantProposalDto(reply, actions, skipped, used, limit));

        // Valida uma ação do modelo; devolve vazio se não se aproveita ou se quem pediu não a pode fazer.
        List<AssistantActionDto> Map(DraftAction a)
        {
            switch (a.Kind)
            {
                case "add_items":
                {
                    if (!Can(HouseholdModule.Shopping, ModuleAccess.Participate) || lists.Count == 0)
                    {
                        return [];
                    }

                    var items = CleanItems(a.Items);
                    var list = lists.FirstOrDefault(l => Same(l.Name, a.List)) ?? lists[0];
                    return items.Count == 0 ? [] : [new(AssistantActionKind.AddItems, ListId: list.Id, ListName: list.Name, Items: items)];
                }

                case "check_items":
                {
                    if (!Can(HouseholdModule.Shopping, ModuleAccess.Participate))
                    {
                        return [];
                    }

                    var names = CleanItems(a.Items);
                    var matches = toBuy.Where(i => names.Any(n => Same(n, i.Name))).ToList();
                    return [.. matches.GroupBy(i => i.ListId).Select(g => new AssistantActionDto(
                        AssistantActionKind.CheckItems,
                        ListId: g.Key,
                        ListName: lists.First(l => l.Id == g.Key).Name,
                        Items: [.. g.Select(i => i.Name)],
                        ItemIds: [.. g.Select(i => i.Id)]))];
                }

                case "create_event":
                {
                    var title = Clean(a.Title, 200);
                    var date = ParseDate(a.Date);
                    if (!Can(HouseholdModule.Calendar, ModuleAccess.Manage) || title is null || date is null)
                    {
                        return [];
                    }

                    var location = Clean(a.Location, 200);
                    if (ParseTime(a.Start) is { } start)
                    {
                        var startsAt = At(household, date.Value, start);
                        var endsAt = ParseTime(a.End) is { } end && At(household, date.Value, end) is var e && e > startsAt
                            ? e
                            : startsAt.AddHours(1);
                        return [new(AssistantActionKind.CreateEvent, Title: title, Date: date, StartsAt: startsAt, EndsAt: endsAt, Location: location)];
                    }

                    var endDate = ParseDate(a.EndDate) is { } last && last >= date.Value && last.DayNumber - date.Value.DayNumber <= 366
                        ? last
                        : date.Value;
                    return [new(AssistantActionKind.CreateEvent, Title: title, Date: date, EndDate: endDate, Location: location)];
                }

                case "create_chore":
                {
                    var title = Clean(a.Title, 200);
                    return !Can(HouseholdModule.Chores, ModuleAccess.Manage) || title is null
                        ? []
                        : [new(AssistantActionKind.CreateChore, Title: title, Date: ParseDate(a.Date))];
                }

                case "create_expense":
                {
                    var title = Clean(a.Title, 200);
                    var cents = a.AmountEuros is { } euros && euros > 0 && euros < 100_000 ? (int)Math.Round(euros * 100) : 0;
                    return !Can(HouseholdModule.Expenses, ModuleAccess.Participate) || title is null || cents <= 0
                        ? []
                        : [new(AssistantActionKind.CreateExpense, Title: title, Date: ParseDate(a.Date) ?? today, AmountCents: cents)];
                }

                default:
                    return [];
            }
        }
    }

    /// <summary>Reserva um pedido do dia: só passa se o contador ainda estiver abaixo do limite (atómico).</summary>
    private static async Task<bool> ReserveAsync(KubicoDbContext db, Guid householdId, DateOnly day, int limit, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reserved = await db.AssistantUsages
                .Where(u => u.HouseholdId == householdId && u.Day == day && u.Count < limit)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Count, u => u.Count + 1), ct);
            if (reserved == 1)
            {
                return true;
            }

            if (limit <= 0 || await db.AssistantUsages.AnyAsync(u => u.HouseholdId == householdId && u.Day == day, ct))
            {
                return false;
            }

            // O primeiro pedido do dia cria a linha; se outro pedido a criar ao mesmo tempo, volta-se a tentar.
            var usage = new AssistantUsage { HouseholdId = householdId, Day = day, Count = 1 };
            db.AssistantUsages.Add(usage);
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException e) when (ApiExceptionHandler.IsUniqueViolation(e))
            {
                db.Entry(usage).State = EntityState.Detached;
            }
        }

        return false;
    }

    private static (byte[]? File, string? MediaType, IResult? Error) ReadFile(AssistantRequest request)
    {
        if (string.IsNullOrEmpty(request.FileBase64))
        {
            return (null, null, null);
        }

        static IResult Invalid(string message) =>
            Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = [message] });

        if (request.FileBase64.Length > (MaxPdfBytes / 3 + 1) * 4)
        {
            return (null, null, Invalid("O ficheiro é demasiado grande."));
        }

        byte[] data;
        try
        {
            data = Convert.FromBase64String(request.FileBase64);
        }
        catch (FormatException)
        {
            return (null, null, Invalid("O ficheiro não é válido."));
        }

        // O tipo confirma-se pelos primeiros bytes, não pelo que o pedido diz.
        var detected = data switch
        {
            [0xFF, 0xD8, 0xFF, ..] => "image/jpeg",
            [0x89, 0x50, 0x4E, 0x47, ..] => "image/png",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "image/webp",
            [0x25, 0x50, 0x44, 0x46, ..] => "application/pdf",
            _ => null,
        };
        if (detected is null)
        {
            return (null, null, Invalid("Só se aceitam fotografias (JPEG, PNG ou WebP) e PDF."));
        }

        var max = detected == "application/pdf" ? MaxPdfBytes : MaxImageBytes;
        return data.Length > max
            ? (null, null, Invalid("O ficheiro é demasiado grande."))
            : (data, detected, null);
    }

    private static List<string> CleanItems(IReadOnlyList<string>? items) =>
        [.. (items ?? []).Select(i => Clean(i, 200)).OfType<string>().Distinct(StringComparer.CurrentCultureIgnoreCase).Take(MaxItemsPerAction)];

    /// <summary>Tira espaços e caracteres de controlo; null se ficar vazio. Corta no tamanho máximo.</summary>
    private static string? Clean(string? text, int max)
    {
        if (text is null)
        {
            return null;
        }

        var clean = new string([.. text.Where(c => !char.IsControl(c))]).Trim();
        return clean.Length == 0 ? null : clean[..Math.Min(clean.Length, max)];
    }

    private static DateOnly? ParseDate(string? text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
        && SupportedDates.IsSupported(date)
            ? date
            : null;

    private static TimeOnly? ParseTime(string? text) =>
        TimeOnly.TryParseExact(text, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    /// <summary>A hora local da casa nesse dia, em UTC.</summary>
    private static DateTimeOffset At(Household household, DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, household.TimeZone.GetUtcOffset(local)).ToUniversalTime();
    }

    /// <summary>Compara nomes sem maiúsculas, acentos nem espaços a mais ("Pão" = "pao").</summary>
    private static bool Same(string? a, string? b) =>
        a is not null && b is not null && string.Equals(Fold(a), Fold(b), StringComparison.Ordinal);

    private static string Fold(string text)
    {
        var builder = new StringBuilder();
        foreach (var c in text.Trim().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }
}
