using System.Security.Cryptography;
using System.Text.Json;
using Kubico.Api.Common;
using Kubico.Api.Realtime;
using Kubico.Domain.Calendar;
using Kubico.Domain.Households;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Kubico.Api.Calendar.Sync;

/// <summary>
/// Sincroniza o calendário da casa com o calendário do Kubico na conta Google ou Microsoft de um membro, nos dois
/// sentidos. Primeiro lê o que mudou lá e aplica-o no Kubico; depois copia para lá o que mudou no Kubico.
/// Quando os dois lados mudaram o mesmo evento, fica a alteração mais recente.
/// </summary>
public class CalendarSyncService(
    KubicoDbContext db,
    HouseholdAccess access,
    IEnumerable<ICalendarProvider> providers,
    CalendarSecrets secrets,
    CalendarSyncLocks locks,
    CalendarAccessTokens accessTokens,
    HouseholdNotifier notifier,
    TimeProvider clock,
    ILogger<CalendarSyncService> logger)
{
    /// <summary>Marca, na descrição da cópia, o início das informações do Kubico (participantes e boleias).</summary>
    public const string DescriptionMarker = "— Kubico —";

    /// <summary>Quantos avisos da última sincronização ficam guardados para mostrar.</summary>
    private const int MaxWarnings = 3;

    public ICalendarProvider? Provider(CalendarProvider kind) => providers.FirstOrDefault(p => p.Kind == kind);

    public static string CalendarName(string householdName) => $"Kubico · {householdName}";

    /// <summary>
    /// As ligações ativas (para a volta completa do <see cref="CalendarSyncWorker"/>, que as corre em paralelo, cada
    /// uma com o seu DbContext).
    /// </summary>
    public Task<List<Guid>> ActiveConnectionsAsync(CancellationToken ct) =>
        db.CalendarConnections
            .Where(c => c.Status == CalendarConnectionStatus.Active)
            .Select(c => c.Id)
            .ToListAsync(ct);

    public async Task SyncAllAsync(CancellationToken ct)
    {
        foreach (var id in await ActiveConnectionsAsync(ct))
        {
            await SyncAsync(id, ct);
        }
    }

    public async Task SyncHouseholdAsync(Guid householdId, CancellationToken ct)
    {
        var ids = await db.CalendarConnections
            .Where(c => c.HouseholdId == householdId && c.Status == CalendarConnectionStatus.Active)
            .Select(c => c.Id)
            .ToListAsync(ct);
        foreach (var id in ids)
        {
            await SyncAsync(id, ct);
        }
    }

    /// <summary>Sincroniza uma ligação. Devolve-a atualizada (estado, avisos), ou null se já não existir.</summary>
    public async Task<CalendarConnection?> SyncAsync(Guid connectionId, CancellationToken ct)
    {
        using var _ = await locks.AcquireAsync(connectionId, ct);

        // Cada volta começa do zero: outra sincronização pode ter mudado tudo entretanto.
        db.ChangeTracker.Clear();
        var connection = await db.CalendarConnections.SingleOrDefaultAsync(c => c.Id == connectionId, ct);
        if (connection is null)
        {
            return null;
        }

        var role = await access.GetRoleAsync(connection.UserId, connection.HouseholdId, ct);
        if (role is null)
        {
            // Saiu da casa: a ligação deixa de fazer sentido (a cópia fica na conta dele, sem novidades).
            db.CalendarConnections.Remove(connection);
            await db.SaveChangesAsync(ct);
            return null;
        }

        var provider = Provider(connection.Provider);
        var household = await db.Households.AsNoTracking().SingleAsync(h => h.Id == connection.HouseholdId, ct);
        var level = await access.GetAccessAsync(connection.HouseholdId, role.Value, HouseholdModule.Calendar, ct);
        var language = await db.Users.Where(u => u.Id == connection.UserId).Select(u => u.Language).SingleAsync(ct);
        var run = new SyncRun(connection, household, level >= ModuleAccess.Manage, language);
        if (provider is not { IsConfigured: true })
        {
            connection.LastError = "Este serviço deixou de estar disponível no servidor do Kubico.";
        }
        else if (level < ModuleAccess.View)
        {
            connection.LastError = "Já não tens acesso ao calendário desta casa.";
        }
        else
        {
            await RunAsync(run, provider, ct);
        }

        await db.SaveChangesAsync(ct);
        await PublishAsync(run, ct);
        return connection;
    }

    private async Task RunAsync(SyncRun run, ICalendarProvider provider, CancellationToken ct, bool retried = false)
    {
        var connection = run.Connection;
        try
        {
            var calendar = await OpenAsync(run, provider, ct);
            try
            {
                await SyncOnceAsync(run, provider, calendar, ct);
            }
            catch (CalendarNotFoundException)
            {
                // Apagaram o calendário do Kubico na conta: cria-se outro e copia-se tudo de novo.
                var calendarId = await provider.CreateCalendarAsync(
                    calendar.AccessToken, CalendarName(run.Household.Name), run.Zone, ct);
                connection.ExternalCalendarId = calendarId;
                connection.SyncCursor = null;
                db.CalendarEventLinks.RemoveRange(run.Links);
                run.Links.Clear();
                await db.SaveChangesAsync(ct);
                run.Warn("O calendário do Kubico tinha sido apagado na conta e foi criado de novo.");
                await SyncOnceAsync(run, provider, calendar with { CalendarId = calendarId }, ct);
            }

            connection.Status = CalendarConnectionStatus.Active;
            connection.LastSyncedAt = clock.GetUtcNow();
            connection.LastError = run.Warnings.Count == 0 ? null : string.Join("\n", run.Warnings.Take(MaxWarnings));
        }
        catch (CalendarAuthException) when (!retried && accessTokens.Forget(connection.Id))
        {
            // O token de acesso guardado deixou de servir antes do tempo (acesso retirado?): tenta-se já uma vez com um
            // novo; se a renovação também falhar, é a falta de acesso de sempre (voltar a ligar).
            await RunAsync(run, provider, ct, retried: true);
        }
        catch (CalendarAuthException e)
        {
            logger.LogInformation("A ligação {Connection} perdeu o acesso: {Reason}", connection.Id, e.Message);
            connection.Status = CalendarConnectionStatus.NeedsReconnect;
            connection.LastError = "O acesso à conta expirou ou foi retirado. Volta a ligar a conta.";
        }
        catch (Exception e) when (e is CalendarProviderException or CalendarNotFoundException or HttpRequestException
            or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning(e, "Falha a sincronizar a ligação {Connection}.", connection.Id);
            connection.LastError = "Não foi possível sincronizar agora; o Kubico volta a tentar daqui a pouco.";
        }
    }

    /// <summary>
    /// O token de acesso: o que já se tem em memória (<see cref="CalendarAccessTokens"/>, enquanto não expira), ou um
    /// novo a partir do refresh token (a Microsoft troca também o refresh token, que se guarda). Antes, renovava-se em
    /// todas as voltas, embora o token sirva cerca de uma hora.
    /// </summary>
    private async Task<ExternalCalendar> OpenAsync(SyncRun run, ICalendarProvider provider, CancellationToken ct)
    {
        string refreshToken;
        try
        {
            refreshToken = secrets.UnprotectToken(run.Connection.ProtectedRefreshToken);
        }
        catch (CryptographicException)
        {
            throw new CalendarAuthException("O token guardado já não se consegue decifrar.");
        }

        if (accessTokens.Get(run.Connection.Id, refreshToken) is not { } accessToken)
        {
            var tokens = await provider.RefreshAsync(refreshToken, ct);
            if (tokens.RefreshToken is { } rotated && rotated != refreshToken)
            {
                run.Connection.ProtectedRefreshToken = secrets.ProtectToken(rotated);
                refreshToken = rotated;
            }

            accessToken = tokens.AccessToken;
            accessTokens.Set(run.Connection.Id, refreshToken, accessToken, tokens.ExpiresIn);
        }

        return new ExternalCalendar(accessToken, run.Connection.ExternalCalendarId, run.Zone);
    }

    private async Task SyncOnceAsync(SyncRun run, ICalendarProvider provider, ExternalCalendar calendar, CancellationToken ct)
    {
        run.Events = await db.CalendarEvents
            .Where(e => e.HouseholdId == run.Household.Id)
            .ToDictionaryAsync(e => e.Id, ct);
        run.Links = new LinkIndex(await db.CalendarEventLinks
            .Where(l => l.ConnectionId == run.Connection.Id)
            .ToListAsync(ct));

        // Quem não pode criar eventos no Kubico (crianças, convidados) só recebe a cópia.
        if (run.CanEdit)
        {
            await PullAsync(run, provider, calendar, ct);
        }

        await PushAsync(run, provider, calendar, ct);
    }

    // --- De lá para cá ------------------------------------------------------

    private async Task PullAsync(SyncRun run, ICalendarProvider provider, ExternalCalendar calendar, CancellationToken ct)
    {
        var changes = await provider.ListChangesAsync(calendar, run.Connection.SyncCursor, ct);
        var now = clock.GetUtcNow();

        // As séries primeiro: um dia alterado precisa da sua série.
        foreach (var change in changes.Events.OrderBy(c => c.IsOccurrence))
        {
            if (change.IsOccurrence)
            {
                ApplyOccurrence(run, change, now);
            }
            else
            {
                ApplyEvent(run, change, now);
            }
        }

        if (changes.IsFullListing)
        {
            // Numa lista completa, o que lá não está foi apagado.
            var seen = changes.Events.Select(c => c.Id).ToHashSet();
            foreach (var link in run.Links.Where(l => !l.IsOccurrence && !seen.Contains(l.ExternalId)).ToList())
            {
                ApplyRemoval(run, link);
            }
        }

        run.Connection.SyncCursor = changes.Cursor;
        await db.SaveChangesAsync(ct);
    }

    private void ApplyEvent(SyncRun run, ExternalEvent change, DateTimeOffset now)
    {
        var link = run.LinkByExternal(change.Id);
        if (change.IsDeleted)
        {
            if (link is not null)
            {
                ApplyRemoval(run, link);
            }

            return;
        }

        var existing = link is null ? null : run.Events.GetValueOrDefault(link.EventId);
        if (link is not null && (link.ExternalVersion == change.Version || existing is null || KubicoWins(existing, link, change)))
        {
            // A cópia está como a deixámos, o evento foi apagado no Kubico, ou o Kubico tem a alteração mais recente:
            // em todos os casos é o envio que trata.
            return;
        }

        var data = change.Data!;
        if (Problem(data) is { } problem)
        {
            run.Warn($"«{Short(data.Title)}» {problem}");
            link?.ExternalVersion = change.Version;
            return;
        }

        if (existing is null)
        {
            var created = new CalendarEvent
            {
                HouseholdId = run.Household.Id,
                Title = "",
                CreatedByUserId = run.Connection.UserId,
                CreatedAt = now,
            };
            Write(created, data, now);
            created.ExcludedDates = [.. data.Cancelled.Distinct().Order()];
            db.CalendarEvents.Add(created);
            run.Events[created.Id] = created;
            run.Upserted.Add(created.Id);
            AddLink(run, created, change.Id, change.Version, isOccurrence: false);
            return;
        }

        if (Update(run, existing, data, now))
        {
            run.Upserted.Add(existing.Id);
        }

        link!.ExternalVersion = change.Version;
        link.SyncedUpdatedAt = existing.UpdatedAt;
    }

    /// <summary>Um dia de uma série alterado ou apagado lá (só o Google os indica).</summary>
    private void ApplyOccurrence(SyncRun run, ExternalEvent change, DateTimeOffset now)
    {
        var link = run.LinkByExternal(change.Id);
        if (link is not null)
        {
            var known = run.Events.GetValueOrDefault(link.EventId);
            if (change.IsDeleted)
            {
                ApplyRemoval(run, link);
            }
            else if (known is not null && link.ExternalVersion != change.Version && !KubicoWins(known, link, change))
            {
                if (Problem(change.Data!) is { } problem)
                {
                    run.Warn($"«{Short(change.Data!.Title)}» {problem}");
                }
                else if (Update(run, known, change.Data!, now))
                {
                    run.Upserted.Add(known.Id);
                }

                link.ExternalVersion = change.Version;
                link.SyncedUpdatedAt = known.UpdatedAt;
            }

            return;
        }

        var seriesLink = run.LinkByExternal(change.SeriesId!);
        var series = seriesLink is null ? null : run.Events.GetValueOrDefault(seriesLink.EventId);
        if (series is null || change.OriginalDate is not { } date || !series.HasOccurrenceOn(date, run.Zone))
        {
            return; // Uma série que o Kubico não conhece (ou um dia que não é dela).
        }

        if (change.IsDeleted)
        {
            if (!series.ExcludedDates.Contains(date))
            {
                series.Exclude(date);
                Touch(run, series, seriesLink!, now);
            }

            return;
        }

        if (Problem(change.Data!) is { } invalid)
        {
            run.Warn($"«{Short(change.Data!.Title)}» {invalid}");
            return;
        }

        // Um dia alterado lá passa a ser uma exceção no Kubico, com as mesmas pessoas da série.
        var exception = run.Events.Values.FirstOrDefault(e => e.SeriesId == series.Id && e.OriginalDate == date);
        if (exception is null)
        {
            exception = new CalendarEvent
            {
                HouseholdId = run.Household.Id,
                Title = "",
                SeriesId = series.Id,
                OriginalDate = date,
                ParticipantUserIds = [.. series.ParticipantUserIds],
                DropOffUserId = series.DropOffUserId,
                PickUpUserId = series.PickUpUserId,
                CreatedByUserId = run.Connection.UserId,
                CreatedAt = now,
            };
            db.CalendarEvents.Add(exception);
            run.Events[exception.Id] = exception;
            series.Exclude(date);
            Touch(run, series, seriesLink!, now);
        }

        Write(exception, change.Data! with { Recurrence = null }, now);
        run.Upserted.Add(exception.Id);
        AddLink(run, exception, change.Id, change.Version, isOccurrence: true);
    }

    /// <summary>A cópia foi apagada lá: apaga-se no Kubico, a menos que o Kubico a tenha mudado entretanto.</summary>
    private void ApplyRemoval(SyncRun run, CalendarEventLink link)
    {
        if (run.Events.GetValueOrDefault(link.EventId) is { } calendarEvent && calendarEvent.UpdatedAt <= link.SyncedUpdatedAt)
        {
            RemoveEvent(run, calendarEvent);
        }

        // Se o evento ficou (mudou no Kubico), o envio cria uma cópia nova.
        RemoveLink(run, link);
    }

    /// <summary>Aplica os dados de fora a um evento do Kubico. Devolve true se alguma coisa mudou.</summary>
    private bool Update(SyncRun run, CalendarEvent calendarEvent, ExternalEventData data, DateTimeOffset now)
    {
        var before = Fingerprint(calendarEvent);
        var updatedAt = calendarEvent.UpdatedAt;
        var schedule = (calendarEvent.StartsAt, calendarEvent.StartDate, calendarEvent.Recurrence);

        if (calendarEvent.SeriesId is not null)
        {
            Write(calendarEvent, data with { Recurrence = null }, now);
        }
        else
        {
            Write(calendarEvent, data, now);
            if (schedule != (calendarEvent.StartsAt, calendarEvent.StartDate, calendarEvent.Recurrence))
            {
                // Como no Kubico: mudar o início ou a repetição de uma série desfaz as exceções.
                foreach (var exception in run.Events.Values.Where(e => e.SeriesId == calendarEvent.Id).ToList())
                {
                    RemoveEvent(run, exception);
                }

                calendarEvent.ExcludedDates = [.. data.Cancelled.Distinct().Order()];
            }
            else
            {
                calendarEvent.ExcludedDates = [.. calendarEvent.ExcludedDates.Union(data.Cancelled).Distinct().Order()];
            }
        }

        if (Fingerprint(calendarEvent) == before)
        {
            calendarEvent.UpdatedAt = updatedAt;
            return false;
        }

        return true;
    }

    private static void Write(CalendarEvent calendarEvent, ExternalEventData data, DateTimeOffset now)
    {
        calendarEvent.Title = Truncate(string.IsNullOrWhiteSpace(data.Title) ? "(sem título)" : data.Title.Trim(), 200);
        calendarEvent.Notes = Notes(data.Description);
        calendarEvent.Location = string.IsNullOrWhiteSpace(data.Location) ? null : Truncate(data.Location.Trim(), 200);
        if (data.IsAllDay)
        {
            calendarEvent.SetAllDay(data.StartDate!.Value, data.EndDate!.Value);
        }
        else
        {
            calendarEvent.SetTimed(data.StartsAt!.Value, data.EndsAt!.Value);
        }

        calendarEvent.Recurrence = data.Recurrence;
        calendarEvent.UpdatedAt = now;
    }

    /// <summary>Uma série mudou por causa de um dia alterado lá; a cópia dessa ligação já está assim.</summary>
    private static void Touch(SyncRun run, CalendarEvent series, CalendarEventLink seriesLink, DateTimeOffset now)
    {
        series.UpdatedAt = now;
        seriesLink.SyncedUpdatedAt = now;
        run.Upserted.Add(series.Id);
    }

    private void RemoveEvent(SyncRun run, CalendarEvent calendarEvent)
    {
        foreach (var exception in run.Events.Values.Where(e => e.SeriesId == calendarEvent.Id).ToList())
        {
            RemoveEvent(run, exception);
        }

        db.CalendarEvents.Remove(calendarEvent);
        run.Events.Remove(calendarEvent.Id);
        run.Upserted.Remove(calendarEvent.Id);
        run.Deleted.Add(calendarEvent.Id);
        if (run.LinkByEvent(calendarEvent.Id) is { } link)
        {
            RemoveLink(run, link);
        }
    }

    /// <summary>
    /// Os dois lados mudaram desde a última sincronização: fica o Kubico se a alteração dele for a mais recente
    /// (ou se o serviço não disser quando mudou o dele).
    /// </summary>
    private static bool KubicoWins(CalendarEvent calendarEvent, CalendarEventLink link, ExternalEvent change) =>
        calendarEvent.UpdatedAt > link.SyncedUpdatedAt && (change.UpdatedAt is not { } external || external <= calendarEvent.UpdatedAt);

    /// <summary>O que impede um evento externo de entrar no Kubico, pronto a mostrar depois do título.</summary>
    private static string? Problem(ExternalEventData data)
    {
        if (data.UnsupportedRecurrence)
        {
            return "repete-se de uma forma que o Kubico ainda não suporta e não foi sincronizado.";
        }

        var (first, last) = data.IsAllDay
            ? (data.StartDate!.Value, data.EndDate!.Value)
            : (DateOnly.FromDateTime(data.StartsAt!.Value.UtcDateTime), DateOnly.FromDateTime(data.EndsAt!.Value.UtcDateTime));
        return first.Year is >= 1900 and <= 2200 && last.Year is >= 1900 and <= 2200
            ? null
            : "tem datas fora do que o Kubico aceita (1900 a 2200) e não foi sincronizado.";
    }

    private static string Fingerprint(CalendarEvent e) => string.Join('\u001f',
        e.Title, e.Notes, e.Location, e.StartsAt?.UtcTicks, e.EndsAt?.UtcTicks, e.StartDate, e.EndDate, e.Recurrence,
        string.Join(',', e.ExcludedDates.Order()));

    // --- De cá para lá ------------------------------------------------------

    private async Task PushAsync(SyncRun run, ICalendarProvider provider, ExternalCalendar calendar, CancellationToken ct)
    {
        // Cópias de eventos que já não existem no Kubico.
        foreach (var link in run.Links.Where(l => !run.Events.ContainsKey(l.EventId)).ToList())
        {
            await provider.DeleteEventAsync(calendar, link.ExternalId, ct);
            RemoveLink(run, link);
            await db.SaveChangesAsync(ct);
        }

        var names = await db.Memberships.AsNoTracking()
            .Where(m => m.HouseholdId == run.Household.Id)
            .Select(m => new { m.UserId, m.User.DisplayName })
            .ToDictionaryAsync(m => m.UserId, m => m.DisplayName, ct);
        var replaced = run.Events.Values
            .Where(e => e.SeriesId is not null)
            .Select(e => (e.SeriesId!.Value, e.OriginalDate!.Value))
            .ToHashSet();

        // As séries antes das exceções: alterar um dia precisa da cópia da série.
        foreach (var calendarEvent in run.Events.Values.OrderBy(e => e.SeriesId is not null).ThenBy(e => e.CreatedAt).ToList())
        {
            var link = run.LinkByEvent(calendarEvent.Id);
            if (link is not null && calendarEvent.UpdatedAt <= link.SyncedUpdatedAt)
            {
                continue;
            }

            var data = ToExternal(calendarEvent, names, replaced, run.Language);
            ExternalRef? copy;
            try
            {
                if (calendarEvent.SeriesId is { } seriesId)
                {
                    if (run.LinkByEvent(seriesId) is not { } seriesLink)
                    {
                        continue; // A série ainda não tem cópia.
                    }

                    copy = link is null ? null : await provider.UpdateEventAsync(calendar, link.ExternalId, data, isOccurrence: true, ct);
                    copy ??= await provider.UpdateOccurrenceAsync(
                        calendar, seriesLink.ExternalId, calendarEvent.OriginalDate!.Value, data, ct);
                    if (copy is null)
                    {
                        run.Warn($"«{Short(calendarEvent.Title)}»: o dia alterado não foi encontrado na cópia da série.");
                        continue;
                    }
                }
                else
                {
                    copy = link is null ? null : await provider.UpdateEventAsync(calendar, link.ExternalId, data, isOccurrence: false, ct);
                    copy ??= await provider.CreateEventAsync(calendar, data, ct);
                }
            }
            catch (CalendarProviderException e)
            {
                logger.LogWarning(e, "Não foi possível copiar o evento {Event}.", calendarEvent.Id);
                run.Warn($"«{Short(calendarEvent.Title)}» não foi copiado; o Kubico volta a tentar.");
                continue;
            }

            link ??= AddLink(run, calendarEvent, copy.Id, copy.Version, calendarEvent.SeriesId is not null);
            run.Links.SetExternalId(link, copy.Id);
            link.ExternalVersion = copy.Version;
            link.SyncedUpdatedAt = calendarEvent.UpdatedAt;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Um evento do Kubico como vai para a cópia. Os dias substituídos por uma exceção não se retiram da série:
    /// a exceção ocupa o lugar deles.
    /// </summary>
    public static ExternalEventData ToExternal(
        CalendarEvent e,
        IReadOnlyDictionary<Guid, string> names,
        IReadOnlySet<(Guid, DateOnly)> replaced,
        string language = Texts.Portuguese) => new(
        e.Title,
        Description(e, names, language),
        e.Location,
        e.StartsAt,
        e.EndsAt,
        e.StartDate,
        e.EndDate,
        e.Recurrence,
        [.. e.ExcludedDates.Where(d => !replaced.Contains((e.Id, d))).Order()]);

    /// <summary>As notas, e depois da marca do Kubico quem participa e as boleias (que lá não têm onde ficar).</summary>
    public static string? Description(CalendarEvent e, IReadOnlyDictionary<Guid, string> names, string language = Texts.Portuguese)
    {
        var lines = new List<string>();
        if (e.ParticipantUserIds.Count > 0)
        {
            lines.Add(Texts.Format(language, "Participantes: {0}", string.Join(", ", e.ParticipantUserIds.Select(id => names.GetValueOrDefault(id, "?")))));
        }

        if (e.DropOffUserId is { } dropOff)
        {
            lines.Add(Texts.Format(language, "Leva: {0}", names.GetValueOrDefault(dropOff, "?")));
        }

        if (e.PickUpUserId is { } pickUp)
        {
            lines.Add(Texts.Format(language, "Vai buscar: {0}", names.GetValueOrDefault(pickUp, "?")));
        }

        if (lines.Count == 0)
        {
            return e.Notes;
        }

        var footer = DescriptionMarker + "\n" + string.Join("\n", lines);
        return e.Notes is null ? footer : $"{e.Notes}\n\n{footer}";
    }

    /// <summary>As notas de uma descrição externa, sem as informações que o Kubico lá pôs.</summary>
    public static string? Notes(string? description)
    {
        if (description is null)
        {
            return null;
        }

        var text = description.Replace("\r\n", "\n");
        var marker = text.IndexOf(DescriptionMarker, StringComparison.Ordinal);
        text = (marker >= 0 ? text[..marker] : text).Trim();
        return text.Length == 0 ? null : Truncate(text, 1000);
    }

    // --- Comum ----------------------------------------------------------------

    private CalendarEventLink AddLink(SyncRun run, CalendarEvent calendarEvent, string externalId, string? version, bool isOccurrence)
    {
        var link = run.LinkByEvent(calendarEvent.Id);
        if (link is null)
        {
            link = new CalendarEventLink { ConnectionId = run.Connection.Id, EventId = calendarEvent.Id, ExternalId = externalId };
            db.CalendarEventLinks.Add(link);
            run.Links.Add(link);
        }

        run.Links.SetExternalId(link, externalId);
        link.ExternalVersion = version;
        link.SyncedUpdatedAt = calendarEvent.UpdatedAt;
        link.IsOccurrence = isOccurrence;
        return link;
    }

    private void RemoveLink(SyncRun run, CalendarEventLink link)
    {
        db.CalendarEventLinks.Remove(link);
        run.Links.Remove(link);
    }

    /// <summary>Avisa as apps: os eventos que mudaram (na casa) e o estado da ligação (só ao dono).</summary>
    private async Task PublishAsync(SyncRun run, CancellationToken ct)
    {
        foreach (var id in run.Upserted)
        {
            if (run.Events.TryGetValue(id, out var calendarEvent))
            {
                await notifier.CalendarEventUpserted(run.Household.Id, CalendarEndpoints.ToDto(calendarEvent), ct);
            }
        }

        await notifier.CalendarEventsDeleted(run.Household.Id, [.. run.Deleted], ct);

        await notifier.CalendarConnectionUpserted(run.Connection.UserId, CalendarSyncEndpoints.ToDto(run.Connection, run.Language), ct);
    }

    private static string Short(string title) => title.Length <= 40 ? title : title[..39] + "…";

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    /// <summary>O estado de uma sincronização: a ligação, os eventos da casa, as ligações aos eventos e o que mudou.</summary>
    private sealed class SyncRun(CalendarConnection connection, Household household, bool canEdit, string language)
    {
        /// <summary>A língua de quem ligou a conta: as informações que o Kubico escreve nos eventos vão nela.</summary>
        public string Language => language;

        public CalendarConnection Connection => connection;

        public Household Household => household;

        public TimeZoneInfo Zone => household.TimeZone;

        public bool CanEdit => canEdit;

        public Dictionary<Guid, CalendarEvent> Events { get; set; } = [];

        /// <summary>As ligações aos eventos, com índices pelo id do evento e pelo id externo (antes, procuras lineares).</summary>
        public LinkIndex Links { get; set; } = new([]);

        public HashSet<Guid> Upserted { get; } = [];

        public HashSet<Guid> Deleted { get; } = [];

        public List<string> Warnings { get; } = [];

        public CalendarEventLink? LinkByExternal(string externalId) => Links.ByExternal(externalId);

        public CalendarEventLink? LinkByEvent(Guid eventId) => Links.ByEvent(eventId);

        public void Warn(string warning)
        {
            if (!Warnings.Contains(warning))
            {
                Warnings.Add(warning);
            }
        }
    }
}

/// <summary>
/// As ligações aos eventos de uma sincronização, com dicionários pelo id do evento e pelo id externo (as procuras
/// eram lineares dentro de ciclos, O(eventos × ligações)). O id externo de uma ligação pode mudar (ver PushAsync):
/// muda-se com <see cref="SetExternalId"/>.
/// </summary>
internal sealed class LinkIndex : IEnumerable<CalendarEventLink>
{
    private readonly List<CalendarEventLink> _links;
    private readonly Dictionary<Guid, CalendarEventLink> _byEvent = [];
    private readonly Dictionary<string, CalendarEventLink> _byExternal = new(StringComparer.Ordinal);

    public LinkIndex(List<CalendarEventLink> links)
    {
        _links = links;
        foreach (var link in links)
        {
            Index(link);
        }
    }

    public int Count => _links.Count;

    public CalendarEventLink? ByEvent(Guid eventId) => _byEvent.GetValueOrDefault(eventId);

    public CalendarEventLink? ByExternal(string externalId) => _byExternal.GetValueOrDefault(externalId);

    public void Add(CalendarEventLink link)
    {
        _links.Add(link);
        Index(link);
    }

    public bool Remove(CalendarEventLink link)
    {
        if (!_links.Remove(link))
        {
            return false;
        }

        Rebuild();
        return true;
    }

    public void Clear()
    {
        _links.Clear();
        _byEvent.Clear();
        _byExternal.Clear();
    }

    public void SetExternalId(CalendarEventLink link, string externalId)
    {
        if (link.ExternalId == externalId)
        {
            return;
        }

        link.ExternalId = externalId;
        Rebuild();
    }

    public IEnumerator<CalendarEventLink> GetEnumerator() => _links.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Com repetidos (não deviam existir), fica o primeiro, como no FirstOrDefault de antes.</summary>
    private void Index(CalendarEventLink link)
    {
        _byEvent.TryAdd(link.EventId, link);
        _byExternal.TryAdd(link.ExternalId, link);
    }

    private void Rebuild()
    {
        _byEvent.Clear();
        _byExternal.Clear();
        foreach (var link in _links)
        {
            Index(link);
        }
    }
}
