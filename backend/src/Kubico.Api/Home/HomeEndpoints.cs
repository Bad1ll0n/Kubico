using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Kubico.Api.Activity;
using Kubico.Api.Auth;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Chores;
using Kubico.Domain.Home;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Home;

public record MaintenanceDto(Guid Id, string Title, int IntervalDays, DateOnly? LastDone, DateOnly NextDue);

public record ApplianceDto(
    Guid Id,
    string Name,
    ApplianceKind Kind,
    string? Room,
    string? Brand,
    string? Model,
    DateOnly? PurchasedOn,
    DateOnly? WarrantyUntil,
    string? Plate,
    DateOnly? FirstRegistration,
    DateOnly? NextIuc,
    DateOnly? NextInspection,
    string? Notes,
    IReadOnlyList<MaintenanceDto> Maintenance,
    IReadOnlyList<HomeFileDto> Files);

public record SaveMaintenanceRequest([property: Required, StringLength(200, MinimumLength = 1)] string Title, [property: Range(1, 3650)] int IntervalDays, DateOnly? NextDue = null);

public record SaveApplianceRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    ApplianceKind Kind = ApplianceKind.General,
    [property: StringLength(100)] string? Room = null,
    [property: StringLength(100)] string? Brand = null,
    [property: StringLength(100)] string? Model = null,
    DateOnly? PurchasedOn = null,
    DateOnly? WarrantyUntil = null,
    [property: StringLength(20)] string? Plate = null,
    DateOnly? FirstRegistration = null,
    [property: StringLength(2000)] string? Notes = null,
    IReadOnlyList<SaveMaintenanceRequest>? Maintenance = null);

public record HomeFileDto(Guid Id, string FileName, string ContentType, int Size);

/// <summary>Um documento. <c>Number</c> só vai para a própria pessoa e para quem gere a área Casa.</summary>
public record DocumentDto(Guid Id, DocumentKind Kind, string Name, Guid? OwnerUserId, string? Number, DateOnly? ExpiresOn, IReadOnlyList<HomeFileDto> Files);

public record SaveDocumentRequest(
    DocumentKind Kind,
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    Guid? OwnerUserId = null,
    [property: StringLength(100)] string? Number = null,
    DateOnly? ExpiresOn = null);

public record NoteDto(Guid Id, string Text, DateOnly? Until, Guid CreatedByUserId, DateTimeOffset CreatedAt);

public record SaveNoteRequest([property: Required, StringLength(500, MinimumLength = 1)] string Text, DateOnly? Until = null);

public record ContactDto(Guid Id, string Name, string? Role, string? Phone, string? Notes, DateOnly? Birthday);

public record SaveContactRequest(
    [property: Required, StringLength(200, MinimumLength = 1)] string Name,
    [property: StringLength(100)] string? Role = null,
    [property: StringLength(40)] string? Phone = null,
    [property: StringLength(1000)] string? Notes = null,
    DateOnly? Birthday = null);

public record BirthdayDto(string Name, DateOnly Date);

public record HomeDto(
    IReadOnlyList<ApplianceDto> Appliances,
    IReadOnlyList<DocumentDto> Documents,
    IReadOnlyList<NoteDto> Notes,
    IReadOnlyList<ContactDto> Contacts,
    IReadOnlyList<BirthdayDto> Birthdays,
    bool IrsReminders);

public record HomeSettingsRequest(bool IrsReminders);

/// <summary>
/// A área Casa (F4-07, F4-08, F4-12, F4-13): equipamentos e garantias (com o carro: IUC e inspeção), o cofre de
/// documentos, o quadro de recados, os contactos úteis e os aniversários. Ver é <c>View</c>; recados, documentos
/// próprios e marcar manutenções é <c>Participate</c>; o resto é <c>Manage</c>.
/// </summary>
public static class HomeEndpoints
{
    public const int MaxFileBytes = 10 * 1024 * 1024;
    public const int MaxFilesPerHousehold = 200;
    private const string NumberPurpose = "Kubico.Home.DocumentNumber.v1";

    public static RouteGroupBuilder MapHomeEndpoints(this RouteGroupBuilder api)
    {
        var home = api.MapGroup("/households/{householdId:guid}/home")
            .WithTags("Home")
            .RequireAuthorization();

        home.MapGet("/", GetHome);
        home.MapPut("/settings", SaveSettings);
        home.MapPost("/appliances", (Guid householdId, SaveApplianceRequest r, ClaimsPrincipal p, KubicoDbContext db, ActivityLog a, HouseholdAccess x, HouseholdNotifier n, TimeProvider c, CancellationToken ct)
            => SaveAppliance(householdId, null, r, p, db, a, x, n, c, ct));
        home.MapPut("/appliances/{applianceId:guid}", (Guid householdId, Guid applianceId, SaveApplianceRequest r, ClaimsPrincipal p, KubicoDbContext db, ActivityLog a, HouseholdAccess x, HouseholdNotifier n, TimeProvider c, CancellationToken ct)
            => SaveAppliance(householdId, applianceId, r, p, db, a, x, n, c, ct));
        home.MapDelete("/appliances/{applianceId:guid}", DeleteAppliance);
        home.MapPost("/appliances/{applianceId:guid}/maintenance/{taskId:guid}/done", MaintenanceDone);
        home.MapPost("/documents", (Guid householdId, SaveDocumentRequest r, ClaimsPrincipal p, KubicoDbContext db, ActivityLog a, HouseholdAccess x, HouseholdNotifier n, IDataProtectionProvider dp, TimeProvider c, CancellationToken ct)
            => SaveDocument(householdId, null, r, p, db, a, x, n, dp, c, ct)).RequireActiveSession();
        home.MapPut("/documents/{documentId:guid}", (Guid householdId, Guid documentId, SaveDocumentRequest r, ClaimsPrincipal p, KubicoDbContext db, ActivityLog a, HouseholdAccess x, HouseholdNotifier n, IDataProtectionProvider dp, TimeProvider c, CancellationToken ct)
            => SaveDocument(householdId, documentId, r, p, db, a, x, n, dp, c, ct)).RequireActiveSession();
        home.MapDelete("/documents/{documentId:guid}", DeleteDocument);
        home.MapPost("/files", UploadFile).DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(MaxFileBytes + 64 * 1024));
        home.MapGet("/files/{fileId:guid}", GetFile).RequireActiveSession();
        home.MapDelete("/files/{fileId:guid}", DeleteFile);
        home.MapPost("/notes", CreateNote);
        home.MapDelete("/notes/{noteId:guid}", DeleteNote);
        home.MapPost("/contacts", (Guid householdId, SaveContactRequest r, ClaimsPrincipal p, KubicoDbContext db, ActivityLog a, HouseholdAccess x, HouseholdNotifier n, CancellationToken ct)
            => SaveContact(householdId, null, r, p, db, a, x, n, ct));
        home.MapPut("/contacts/{contactId:guid}", (Guid householdId, Guid contactId, SaveContactRequest r, ClaimsPrincipal p, KubicoDbContext db, ActivityLog a, HouseholdAccess x, HouseholdNotifier n, CancellationToken ct)
            => SaveContact(householdId, contactId, r, p, db, a, x, n, ct));
        home.MapDelete("/contacts/{contactId:guid}", DeleteContact);
        return api;
    }

    private static async Task<IResult> GetHome(
        Guid householdId,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        IDataProtectionProvider protection,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (role, level, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct);
        var today = household.Today(clock.GetUtcNow());
        var files = (await db.HomeFiles.AsNoTracking()
                .Where(f => f.HouseholdId == householdId)
                .Select(f => new { f.Id, f.OwnerId, f.FileName, f.ContentType, Size = f.Content.Length })
                .ToListAsync(ct))
            .ToLookup(f => f.OwnerId, f => new HomeFileDto(f.Id, f.FileName, f.ContentType, f.Size));
        var appliances = await db.Appliances.AsNoTracking().Where(a => a.HouseholdId == householdId).ToListAsync(ct);
        var documents = await db.HomeDocuments.AsNoTracking().Where(d => d.HouseholdId == householdId).ToListAsync(ct);
        var notes = await db.HouseNotes.AsNoTracking()
            .Where(n => n.HouseholdId == householdId && (n.Until == null || n.Until >= today))
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync(ct);
        var contacts = await db.HouseContacts.AsNoTracking().Where(c => c.HouseholdId == householdId).ToListAsync(ct);
        var protector = protection.CreateProtector(NumberPurpose);
        var manager = level >= ModuleAccess.Manage;

        // Os documentos de outra pessoa só os vê quem gere a área (e os da casa, sem dono, toda a gente que a vê).
        var visibleDocuments = documents.Where(d => manager || d.OwnerUserId == userId || d.OwnerUserId == null);

        // Os aniversários: os dos contactos e, para quem gere o conteúdo da casa, os das crianças (minimização).
        var birthdays = contacts.Where(c => c.Birthday is not null)
            .Select(c => (c.Name, Birthday: c.Birthday!.Value))
            .ToList();
        if (role!.Value.CanManageContent())
        {
            birthdays.AddRange((await db.Memberships.AsNoTracking()
                    .Where(m => m.HouseholdId == householdId && m.User.BirthDate != null)
                    .Select(m => new { m.User.DisplayName, m.User.BirthDate })
                    .ToListAsync(ct))
                .Select(m => (m.DisplayName, m.BirthDate!.Value)));
        }

        return Results.Ok(new HomeDto(
            [.. appliances.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).Select(a => ToDto(a, files[a.Id], today))],
            [.. visibleDocuments.OrderBy(d => d.ExpiresOn ?? DateOnly.MaxValue).Select(d => ToDto(d, files[d.Id],
                manager || d.OwnerUserId == userId ? Unprotect(protector, d.ProtectedNumber) : null))],
            [.. notes.Select(ToDto)],
            [.. contacts.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(ToDto)],
            [.. birthdays.Select(b => new BirthdayDto(b.Item1, NextBirthday(b.Item2, today))).OrderBy(b => b.Date)],
            household.IrsReminders));
    }

    /// <summary>O próximo dia de anos a partir de hoje (o 29 de fevereiro passa a 28 nos anos comuns).</summary>
    public static DateOnly NextBirthday(DateOnly birthday, DateOnly today)
    {
        DateOnly In(int year) => new(year, birthday.Month, Math.Min(birthday.Day, DateTime.DaysInMonth(year, birthday.Month)));
        return In(today.Year) >= today ? In(today.Year) : In(today.Year + 1);
    }

    private static async Task<IResult> SaveSettings(
        Guid householdId, HomeSettingsRequest request, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access,
        HouseholdNotifier notifier, CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(principal.GetUserId(), householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var household = await db.Households.SingleAsync(h => h.Id == householdId, ct);
        household.IrsReminders = request.IrsReminders;
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveAppliance(
        Guid householdId, Guid? applianceId, SaveApplianceRequest request, ClaimsPrincipal principal, KubicoDbContext db,
        ActivityLog activity, HouseholdAccess access, HouseholdNotifier notifier, TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var dates = new[] { request.PurchasedOn, request.WarrantyUntil, request.FirstRegistration }
            .Concat((request.Maintenance ?? []).Select(m => m.NextDue));
        if (string.IsNullOrWhiteSpace(request.Name) || dates.Any(d => d is { } day && !SupportedDates.IsSupported(day))
            || (request.Maintenance?.Count ?? 0) > 20)
        {
            return Invalid("Confirma o nome, as datas e as manutenções (no máximo 20).");
        }

        var appliance = applianceId is null
            ? null
            : await db.Appliances.SingleOrDefaultAsync(a => a.Id == applianceId && a.HouseholdId == householdId, ct);
        if (applianceId is not null && appliance is null)
        {
            return Results.NotFound();
        }

        var now = clock.GetUtcNow();
        var today = (await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct)).Today(now);
        if (appliance is null)
        {
            appliance = new Appliance { HouseholdId = householdId, Name = request.Name.Trim() };
            db.Appliances.Add(appliance);
        }

        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        var previous = appliance.Maintenance.ToDictionary(m => m.Title, StringComparer.OrdinalIgnoreCase);
        appliance.Name = request.Name.Trim();
        appliance.Kind = request.Kind;
        appliance.Room = Clean(request.Room);
        appliance.Brand = Clean(request.Brand);
        appliance.Model = Clean(request.Model);
        appliance.PurchasedOn = request.PurchasedOn;
        appliance.WarrantyUntil = request.WarrantyUntil;
        appliance.Plate = request.Kind == ApplianceKind.Car ? Clean(request.Plate)?.ToUpperInvariant() : null;
        appliance.FirstRegistration = request.Kind == ApplianceKind.Car ? request.FirstRegistration : null;
        appliance.Notes = Clean(request.Notes);
        // As manutenções que já existiam guardam quando se fizeram; as novas vencem no dia escolhido ou daqui a um intervalo.
        appliance.Maintenance = [.. (request.Maintenance ?? []).Select(m => previous.TryGetValue(m.Title.Trim(), out var old)
            ? new MaintenanceTask { Id = old.Id, Title = m.Title.Trim(), IntervalDays = m.IntervalDays, LastDone = old.LastDone, NextDue = m.NextDue ?? old.NextDue }
            : new MaintenanceTask { Title = m.Title.Trim(), IntervalDays = m.IntervalDays, NextDue = m.NextDue ?? today.AddDays(m.IntervalDays) })];
        appliance.UpdatedAt = now;
        activity.Capture(householdId, userId, HouseholdModule.Home,
            applianceId is null ? $"juntou o equipamento «{appliance.Name}»" : $"mudou o equipamento «{appliance.Name}»", undoable: false);
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.Ok(ToDto(appliance, [], today));
    }

    private static async Task<IResult> DeleteAppliance(
        Guid householdId, Guid applianceId, ClaimsPrincipal principal, KubicoDbContext db, ActivityLog activity,
        HouseholdAccess access, HouseholdNotifier notifier, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var appliance = await db.Appliances.SingleOrDefaultAsync(a => a.Id == applianceId && a.HouseholdId == householdId, ct);
        if (appliance is null)
        {
            return Results.NotFound();
        }

        db.Appliances.Remove(appliance);
        db.HomeFiles.RemoveRange(await db.HomeFiles.Where(f => f.OwnerId == applianceId).ToListAsync(ct));
        activity.Capture(householdId, userId, HouseholdModule.Home, $"tirou o equipamento «{appliance.Name}»", undoable: false);
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MaintenanceDone(
        Guid householdId, Guid applianceId, Guid taskId, ClaimsPrincipal principal, KubicoDbContext db, ActivityLog activity,
        HouseholdAccess access, HouseholdNotifier notifier, TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var appliance = await db.Appliances.SingleOrDefaultAsync(a => a.Id == applianceId && a.HouseholdId == householdId, ct);
        var task = appliance?.Maintenance.SingleOrDefault(m => m.Id == taskId);
        if (appliance is null || task is null)
        {
            return Results.NotFound();
        }

        var today = (await db.Households.AsNoTracking().SingleAsync(h => h.Id == householdId, ct)).Today(clock.GetUtcNow());
        task.LastDone = today;
        task.NextDue = today.AddDays(task.IntervalDays);
        appliance.Maintenance = [.. appliance.Maintenance];
        appliance.UpdatedAt = clock.GetUtcNow();
        activity.Capture(householdId, userId, HouseholdModule.Home, $"fez a manutenção «{task.Title}» ({appliance.Name})",
            undoable: false, access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.Ok(new MaintenanceDto(task.Id, task.Title, task.IntervalDays, task.LastDone, task.NextDue));
    }

    private static async Task<IResult> SaveDocument(
        Guid householdId, Guid? documentId, SaveDocumentRequest request, ClaimsPrincipal principal, KubicoDbContext db,
        ActivityLog activity, HouseholdAccess access, HouseholdNotifier notifier, IDataProtectionProvider protection,
        TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var manager = level >= ModuleAccess.Manage;
        // Quem não gere a área só guarda (e muda) os seus documentos.
        if (!manager && request.OwnerUserId != userId)
        {
            return Results.Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.Name) || !Enum.IsDefined(request.Kind)
            || request.ExpiresOn is { } expires && !SupportedDates.IsSupported(expires)
            || request.OwnerUserId is { } owner && !await db.Memberships.AnyAsync(m => m.HouseholdId == householdId && m.UserId == owner, ct))
        {
            return Invalid("Confirma o nome, a validade e de quem é o documento.");
        }

        var document = documentId is null
            ? null
            : await db.HomeDocuments.SingleOrDefaultAsync(d => d.Id == documentId && d.HouseholdId == householdId, ct);
        if (documentId is not null && (document is null || !manager && document.OwnerUserId != userId))
        {
            return Results.NotFound();
        }

        if (document is null)
        {
            document = new HomeDocument { HouseholdId = householdId, Name = request.Name.Trim() };
            db.HomeDocuments.Add(document);
        }

        document.Kind = request.Kind;
        document.Name = request.Name.Trim();
        document.OwnerUserId = request.OwnerUserId;
        document.ProtectedNumber = string.IsNullOrWhiteSpace(request.Number)
            ? null
            : protection.CreateProtector(NumberPurpose).Protect(request.Number.Trim());
        document.ExpiresOn = request.ExpiresOn;
        document.UpdatedAt = clock.GetUtcNow();
        // O histórico não leva o número.
        activity.Capture(householdId, userId, HouseholdModule.Home, $"guardou o documento «{document.Name}»", undoable: false,
            access: ModuleAccess.Participate, privateTo: document.OwnerUserId);
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.Ok(ToDto(document, [], request.Number?.Trim()));
    }

    private static async Task<IResult> DeleteDocument(
        Guid householdId, Guid documentId, ClaimsPrincipal principal, KubicoDbContext db, ActivityLog activity,
        HouseholdAccess access, HouseholdNotifier notifier, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var document = await db.HomeDocuments.SingleOrDefaultAsync(d => d.Id == documentId && d.HouseholdId == householdId, ct);
        if (document is null || level < ModuleAccess.Manage && document.OwnerUserId != userId)
        {
            return Results.NotFound();
        }

        db.HomeDocuments.Remove(document);
        db.HomeFiles.RemoveRange(await db.HomeFiles.Where(f => f.OwnerId == documentId).ToListAsync(ct));
        activity.Capture(householdId, userId, HouseholdModule.Home, $"apagou o documento «{document.Name}»", undoable: false,
            access: ModuleAccess.Participate, privateTo: document.OwnerUserId);
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.NoContent();
    }

    /// <summary>Um PDF ou uma imagem (sem metadados) de um equipamento ou documento, até 10 MB.</summary>
    private static async Task<IResult> UploadFile(
        Guid householdId,
        Guid ownerId,
        IFormFile file,
        ClaimsPrincipal principal,
        KubicoDbContext db,
        HouseholdAccess access,
        HouseholdNotifier notifier,
        TimeProvider clock,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        if (!await CanTouchOwnerAsync(db, householdId, ownerId, userId, level, ct))
        {
            return Results.NotFound();
        }

        if (file.Length == 0 || file.Length > MaxFileBytes)
        {
            return Invalid("O ficheiro tem de ter no máximo 10 MB.");
        }

        if (await db.HomeFiles.CountAsync(f => f.HouseholdId == householdId, ct) >= MaxFilesPerHousehold)
        {
            return Results.Problem(title: "A casa já tem ficheiros a mais. Apaga alguns antes de juntar mais.",
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var received = buffer.ToArray();
        byte[]? content;
        string? contentType;
        if (received.AsSpan().StartsWith("%PDF-"u8))
        {
            (content, contentType) = (received, "application/pdf");
        }
        else
        {
            contentType = ChorePhoto.DetectContentType(received);
            content = contentType is null ? null : PhotoMetadata.Strip(received, contentType);
        }

        if (content is null || contentType is null)
        {
            return Invalid("Junta um PDF ou uma imagem (JPEG, PNG ou WebP).");
        }

        var name = Path.GetFileName(file.FileName);
        var stored = new HomeFile
        {
            HouseholdId = householdId,
            OwnerId = ownerId,
            FileName = string.IsNullOrWhiteSpace(name) ? "ficheiro" : name[..Math.Min(name.Length, 200)],
            ContentType = contentType,
            Content = content,
            UploadedByUserId = userId,
            CreatedAt = clock.GetUtcNow(),
        };
        db.HomeFiles.Add(stored);
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.Created($"/api/households/{householdId}/home/files/{stored.Id}",
            new HomeFileDto(stored.Id, stored.FileName, stored.ContentType, stored.Content.Length));
    }

    private static async Task<IResult> GetFile(
        Guid householdId, Guid fileId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access, HttpContext http,
        CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.View, ct);
        if (error is not null)
        {
            return error;
        }

        var file = await db.HomeFiles.AsNoTracking().SingleOrDefaultAsync(f => f.Id == fileId && f.HouseholdId == householdId, ct);
        if (file is null || !await CanSeeOwnerAsync(db, householdId, file.OwnerId, userId, level, ct))
        {
            return Results.NotFound();
        }

        var etag = HttpCaching.ETagOf(file.Id);
        HttpCaching.PrivateWithETag(http, etag);
        return HttpCaching.ClientHas(http, etag)
            ? Results.StatusCode(StatusCodes.Status304NotModified)
            : Results.File(file.Content, file.ContentType, file.FileName);
    }

    private static async Task<IResult> DeleteFile(
        Guid householdId, Guid fileId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access,
        HouseholdNotifier notifier, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var file = await db.HomeFiles.SingleOrDefaultAsync(f => f.Id == fileId && f.HouseholdId == householdId, ct);
        if (file is null || !await CanTouchOwnerAsync(db, householdId, file.OwnerId, userId, level, ct))
        {
            return Results.NotFound();
        }

        db.HomeFiles.Remove(file);
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.NoContent();
    }

    /// <summary>Juntar ou apagar ficheiros: num equipamento, quem gere; num documento, o dono ou quem gere.</summary>
    private static async Task<bool> CanTouchOwnerAsync(
        KubicoDbContext db, Guid householdId, Guid ownerId, Guid userId, ModuleAccess level, CancellationToken ct)
    {
        if (await db.Appliances.AnyAsync(a => a.Id == ownerId && a.HouseholdId == householdId, ct))
        {
            return level >= ModuleAccess.Manage;
        }

        return await db.HomeDocuments.AnyAsync(d => d.Id == ownerId && d.HouseholdId == householdId
            && (level >= ModuleAccess.Manage || d.OwnerUserId == userId), ct);
    }

    /// <summary>Ver um ficheiro: os dos equipamentos, quem vê a área; os dos documentos, quem vê o documento.</summary>
    private static async Task<bool> CanSeeOwnerAsync(
        KubicoDbContext db, Guid householdId, Guid ownerId, Guid userId, ModuleAccess level, CancellationToken ct) =>
        await db.Appliances.AnyAsync(a => a.Id == ownerId && a.HouseholdId == householdId, ct)
        || await db.HomeDocuments.AnyAsync(d => d.Id == ownerId && d.HouseholdId == householdId
            && (level >= ModuleAccess.Manage || d.OwnerUserId == userId || d.OwnerUserId == null), ct);

    private static async Task<IResult> CreateNote(
        Guid householdId, SaveNoteRequest request, ClaimsPrincipal principal, KubicoDbContext db, ActivityLog activity,
        HouseholdAccess access, HouseholdNotifier notifier, TimeProvider clock, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(request.Text) || request.Until is { } until && !SupportedDates.IsSupported(until))
        {
            return Invalid("Escreve o recado.");
        }

        var note = new HouseNote
        {
            HouseholdId = householdId, Text = request.Text.Trim(), Until = request.Until, CreatedByUserId = userId,
            CreatedAt = clock.GetUtcNow(),
        };
        db.HouseNotes.Add(note);
        activity.Capture(householdId, userId, HouseholdModule.Home, "deixou um recado", undoable: false, access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.Created($"/api/households/{householdId}/home/notes/{note.Id}", ToDto(note));
    }

    private static async Task<IResult> DeleteNote(
        Guid householdId, Guid noteId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access,
        HouseholdNotifier notifier, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, level, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        var note = await db.HouseNotes.SingleOrDefaultAsync(n => n.Id == noteId && n.HouseholdId == householdId, ct);
        if (note is null)
        {
            return Results.NotFound();
        }

        if (level < ModuleAccess.Manage && note.CreatedByUserId != userId)
        {
            return Results.Forbid();
        }

        db.HouseNotes.Remove(note);
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> SaveContact(
        Guid householdId, Guid? contactId, SaveContactRequest request, ClaimsPrincipal principal, KubicoDbContext db,
        ActivityLog activity, HouseholdAccess access, HouseholdNotifier notifier, CancellationToken ct)
    {
        var userId = principal.GetUserId();
        var (_, _, error) = await access.RequireAsync(userId, householdId, HouseholdModule.Home, ModuleAccess.Participate, ct);
        if (error is not null)
        {
            return error;
        }

        if (string.IsNullOrWhiteSpace(request.Name) || request.Birthday is { } birthday && !SupportedDates.IsSupported(birthday))
        {
            return Invalid("Escreve o nome do contacto.");
        }

        var contact = contactId is null
            ? null
            : await db.HouseContacts.SingleOrDefaultAsync(c => c.Id == contactId && c.HouseholdId == householdId, ct);
        if (contactId is not null && contact is null)
        {
            return Results.NotFound();
        }

        if (contact is null)
        {
            contact = new HouseContact { HouseholdId = householdId, Name = request.Name.Trim() };
            db.HouseContacts.Add(contact);
        }

        static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        contact.Name = request.Name.Trim();
        contact.Role = Clean(request.Role);
        contact.Phone = Clean(request.Phone);
        contact.Notes = Clean(request.Notes);
        contact.Birthday = request.Birthday;
        activity.Capture(householdId, userId, HouseholdModule.Home, $"guardou o contacto «{contact.Name}»", undoable: false,
            access: ModuleAccess.Participate);
        await db.SaveChangesAsync(ct);
        await notifier.HomeChanged(householdId, ct);
        return Results.Ok(ToDto(contact));
    }

    private static async Task<IResult> DeleteContact(
        Guid householdId, Guid contactId, ClaimsPrincipal principal, KubicoDbContext db, HouseholdAccess access,
        HouseholdNotifier notifier, CancellationToken ct)
    {
        var (_, _, error) = await access.RequireAsync(principal.GetUserId(), householdId, HouseholdModule.Home, ModuleAccess.Manage, ct);
        if (error is not null)
        {
            return error;
        }

        var deleted = await db.HouseContacts.Where(c => c.Id == contactId && c.HouseholdId == householdId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            return Results.NotFound();
        }

        await notifier.HomeChanged(householdId, ct);
        return Results.NoContent();
    }

    /// <summary>O número de um documento, decifrado (para a exportação RGPD da própria pessoa).</summary>
    public static string? UnprotectNumber(IDataProtectionProvider protection, string? value) =>
        Unprotect(protection.CreateProtector(NumberPurpose), value);

    private static string? Unprotect(IDataProtector protector, string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return protector.Unprotect(value);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }

    private static IResult Invalid(string message) =>
        Results.ValidationProblem(new Dictionary<string, string[]> { ["Name"] = [message] });

    private static ApplianceDto ToDto(Appliance a, IEnumerable<HomeFileDto> files, DateOnly today) => new(
        a.Id, a.Name, a.Kind, a.Room, a.Brand, a.Model, a.PurchasedOn, a.WarrantyUntil, a.Plate, a.FirstRegistration,
        a.FirstRegistration is { } first ? PortugalCalendar.NextIuc(first, today) : null,
        a.FirstRegistration is { } registered ? PortugalCalendar.NextInspection(registered, today) : null,
        a.Notes,
        [.. a.Maintenance.OrderBy(m => m.NextDue).Select(m => new MaintenanceDto(m.Id, m.Title, m.IntervalDays, m.LastDone, m.NextDue))],
        [.. files]);

    private static DocumentDto ToDto(HomeDocument d, IEnumerable<HomeFileDto> files, string? number) =>
        new(d.Id, d.Kind, d.Name, d.OwnerUserId, number, d.ExpiresOn, [.. files]);

    private static NoteDto ToDto(HouseNote n) => new(n.Id, n.Text, n.Until, n.CreatedByUserId, n.CreatedAt);

    private static ContactDto ToDto(HouseContact c) => new(c.Id, c.Name, c.Role, c.Phone, c.Notes, c.Birthday);
}
