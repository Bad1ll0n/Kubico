using System.Text.Json;
using Kubico.Domain.Activity;
using Kubico.Domain.Calendar;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;
using Kubico.Domain.Shopping;
using Kubico.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Kubico.Api.Activity;

/// <summary>
/// Regista o histórico de cada casa e desfaz alterações. Chama-se <see cref="Capture"/> mesmo antes de gravar: o que o
/// EF está prestes a criar, mudar ou apagar (com os valores de antes) fica guardado no registo, na mesma gravação.
/// Desfazer repõe esses valores: o que foi criado é apagado, o que mudou volta ao que era e o que foi apagado volta.
/// </summary>
public sealed class ActivityLog(KubicoDbContext db, TimeProvider clock)
{
    /// <summary>As coisas que se podem desfazer, por ordem de dependência (as de cima primeiro ao repor).</summary>
    private static readonly Type[] Undoable =
        [typeof(ShoppingList), typeof(ShoppingItem), typeof(Chore), typeof(ChoreCompletion), typeof(CalendarEvent), typeof(Reward)];

    private static readonly JsonSerializerOptions Json = new();

    /// <summary>
    /// Regista a alteração que está para ser gravada. Com <paramref name="undoable"/>, guarda o necessário para a
    /// desfazer (só as coisas de <see cref="Undoable"/>); grava-se com o próximo <c>SaveChanges</c>.
    /// </summary>
    public ActivityEntry Capture(Guid householdId, Guid userId, HouseholdModule? module, string summary, bool undoable = true)
    {
        var entry = new ActivityEntry
        {
            HouseholdId = householdId,
            UserId = userId,
            Module = module,
            Summary = summary.Length <= 300 ? summary : summary[..300],
            CreatedAt = clock.GetUtcNow(),
        };

        db.ChangeTracker.DetectChanges();
        var changes = db.ChangeTracker.Entries()
            .Where(e => Undoable.Contains(e.Metadata.ClrType) && e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .ToList();
        // Também se guarda de que coisas dependem as que mudaram (com "^": a lista de um artigo, a tarefa de uma
        // conclusão): pôr um artigo numa lista impede desfazer a criação da lista, mas não o de outros artigos.
        var own = changes.Select(Key).Distinct().ToList();
        entry.EntityKeys = [.. own, .. changes.SelectMany(ParentKeys).Distinct().Except(own).Select(k => ParentMark + k)];
        if (undoable && changes.Count > 0)
        {
            var data = new UndoData(
                [.. changes.Where(e => e.State != EntityState.Added).Select(Before)],
                [.. changes.Where(e => e.State == EntityState.Added).Select(Key)]);
            entry.UndoData = JsonSerializer.Serialize(data, Json);
        }

        db.ActivityEntries.Add(entry);
        return entry;
    }

    /// <summary>
    /// Desfaz: apaga o que foi criado e repõe o que mudou ou foi apagado. Devolve as chaves das coisas tocadas, ou
    /// null se já não se puder (entretanto houve outras alterações às mesmas coisas).
    /// </summary>
    public async Task<IReadOnlyList<string>?> UndoAsync(ActivityEntry entry, Guid userId, CancellationToken ct)
    {
        if (!entry.CanBeUndone || !await IsLatestAsync(entry, ct))
        {
            return null;
        }

        var data = JsonSerializer.Deserialize<UndoData>(entry.UndoData!, Json)!;

        // O que foi criado sai (os dependentes primeiro); se entretanto ganhou outras coisas, não se mexe.
        foreach (var key in data.Created.OrderByDescending(Order))
        {
            var (type, id) = Parse(key);
            if (await db.FindAsync(type, id) is not { } created)
            {
                continue;
            }

            if (await HasOtherDependentsAsync(type, id, data.Created, ct))
            {
                return null;
            }

            db.Remove(created);
        }

        // O que mudou ou foi apagado volta ao que era (os de que outros dependem primeiro).
        foreach (var before in data.Before.OrderBy(b => Order(b.Key)))
        {
            var (type, id) = Parse(before.Key);
            var entity = await db.FindAsync(type, id);
            if (entity is null)
            {
                // Foi apagado: volta com o mesmo id.
                entity = Activator.CreateInstance(type, nonPublic: true)!;
                type.GetProperty("Id")!.SetValue(entity, id);
                db.Add(entity);
            }

            var target = db.Entry(entity);
            foreach (var property in target.Properties.Where(p => !p.Metadata.IsPrimaryKey()))
            {
                if (before.Values.TryGetValue(property.Metadata.Name, out var value))
                {
                    property.CurrentValue = value.Deserialize(property.Metadata.ClrType, Json);
                }
            }
        }

        entry.UndoneAt = clock.GetUtcNow();
        entry.UndoneByUserId = userId;
        return OwnKeys(entry);
    }

    /// <summary>Só se desfaz a última alteração (ainda não desfeita) de cada uma das coisas que tocou.</summary>
    public async Task<bool> IsLatestAsync(ActivityEntry entry, CancellationToken ct)
    {
        var newer = await db.ActivityEntries.AsNoTracking()
            .Where(a => a.HouseholdId == entry.HouseholdId && a.UndoneAt == null && a.UndoOfId == null && a.Id > entry.Id)
            .Select(a => a.EntityKeys)
            .ToListAsync(ct);
        var mine = OwnKeys(entry);
        return !newer.Any(keys => AllKeys(keys).Intersect(mine).Any());
    }

    private const string ParentMark = "^";

    /// <summary>As coisas que a alteração mudou (sem as de que dependem).</summary>
    public static IReadOnlyList<string> OwnKeys(ActivityEntry entry) =>
        [.. entry.EntityKeys.Where(k => !k.StartsWith(ParentMark, StringComparison.Ordinal))];

    /// <summary>As coisas que a alteração mudou e as de que dependem: uma mais antiga que mude alguma destas fica bloqueada.</summary>
    public static IEnumerable<string> AllKeys(IEnumerable<string> keys) =>
        keys.Select(k => k.StartsWith(ParentMark, StringComparison.Ordinal) ? k[ParentMark.Length..] : k);

    /// <summary>Apaga o histórico com mais de 30 dias.</summary>
    public Task PruneAsync(Guid householdId, CancellationToken ct)
    {
        var oldest = clock.GetUtcNow() - ActivityEntry.KeepFor;
        return db.ActivityEntries.Where(a => a.HouseholdId == householdId && a.CreatedAt < oldest).ExecuteDeleteAsync(ct);
    }

    public static (Type Type, Guid Id) Parse(string key)
    {
        var parts = key.Split(':', 2);
        return (Undoable.Single(t => t.Name == parts[0]), Guid.Parse(parts[1]));
    }

    private async Task<bool> HasOtherDependentsAsync(Type type, Guid id, IReadOnlyList<string> created, CancellationToken ct)
    {
        var ids = created.Select(Parse).Select(k => k.Id).ToList();
        return type == typeof(ShoppingList)
            ? await db.ShoppingItems.AnyAsync(i => i.ListId == id && !ids.Contains(i.Id), ct)
            : type == typeof(Chore)
                ? await db.ChoreCompletions.AnyAsync(c => c.ChoreId == id && !ids.Contains(c.Id), ct)
                : type == typeof(CalendarEvent) && await db.CalendarEvents.AnyAsync(e => e.SeriesId == id && !ids.Contains(e.Id), ct);
    }

    /// <summary>As coisas (que se podem desfazer) de que esta depende: a lista de um artigo, a tarefa de uma conclusão…</summary>
    private static IEnumerable<string> ParentKeys(EntityEntry entry) =>
        from foreignKey in entry.Metadata.GetForeignKeys()
        where Undoable.Contains(foreignKey.PrincipalEntityType.ClrType)
        let value = entry.Property(foreignKey.Properties[0].Name).CurrentValue ?? entry.Property(foreignKey.Properties[0].Name).OriginalValue
        where value is Guid
        select $"{foreignKey.PrincipalEntityType.ClrType.Name}:{value}";

    private static int Order(string key) => Array.IndexOf(Undoable, Parse(key).Type);

    private static string Key(EntityEntry entry) => $"{entry.Metadata.ClrType.Name}:{entry.Property("Id").CurrentValue}";

    /// <summary>Os valores de antes: os originais (o que estava gravado), de todas as propriedades.</summary>
    private static Snapshot Before(EntityEntry entry) => new(
        Key(entry),
        entry.Properties.ToDictionary(
            p => p.Metadata.Name,
            p => JsonSerializer.SerializeToElement(p.OriginalValue, p.Metadata.ClrType, Json)));

    private sealed record Snapshot(string Key, Dictionary<string, JsonElement> Values);

    private sealed record UndoData(IReadOnlyList<Snapshot> Before, IReadOnlyList<string> Created);
}
