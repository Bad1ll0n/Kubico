namespace Kubico.Domain.Shopping;

/// <summary>
/// Um artigo comprado (marcado no carrinho), para as sugestões e para aprender a ordem do supermercado (F4-22). Há
/// no máximo um por artigo da lista (desmarcar apaga-o); guarda-se um ano.
/// </summary>
public class PurchaseRecord
{
    /// <summary>O id do artigo da lista: um registo por artigo.</summary>
    public Guid ItemId { get; set; }

    public Guid HouseholdId { get; set; }
    public Guid ListId { get; set; }
    public required string Name { get; set; }

    /// <summary>O nome normalizado (minúsculas, sem espaços à volta), para juntar "Leite" e "leite ".</summary>
    public required string NameKey { get; set; }

    public string? Category { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset PurchasedAt { get; set; }
}

/// <summary>Um artigo que volta à lista sozinho, a cada <see cref="IntervalDays"/> dias (F4-22a).</summary>
public class RecurringItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid ListId { get; set; }
    public required string Name { get; set; }
    public string? Quantity { get; set; }
    public string? Category { get; set; }

    /// <summary>De quantos em quantos dias (7 = todas as semanas).</summary>
    public int IntervalDays { get; set; }

    /// <summary>O próximo dia (no fuso da casa) em que o artigo vai para a lista.</summary>
    public DateOnly NextDate { get; set; }

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>O dia seguinte a <paramref name="today"/> em que volta, mantendo o ritmo.</summary>
    public DateOnly NextAfter(DateOnly today)
    {
        var next = NextDate;
        while (next <= today)
        {
            next = next.AddDays(IntervalDays);
        }

        return next;
    }
}

/// <summary>Uma sugestão: um artigo que se costuma comprar e que já passou do costume.</summary>
public sealed record PurchaseSuggestion(string Name, string? Category, DateOnly LastBought, int EveryDays);

/// <summary>As contas das sugestões e da ordem do supermercado, a partir do que se comprou.</summary>
public static class ShoppingInsights
{
    public const int MaxSuggestions = 8;

    public static string Key(string name) => name.Trim().ToLowerInvariant();

    /// <summary>
    /// O que se costuma comprar (pelo menos duas vezes, em dias diferentes) e já devia ter voltado: passou ~90 % do
    /// intervalo médio desde a última compra. Fora fica o que já está na lista por comprar. Os mais atrasados primeiro.
    /// </summary>
    public static IReadOnlyList<PurchaseSuggestion> Suggestions(
        IEnumerable<(PurchaseRecord Record, DateOnly Day)> purchases, ISet<string> pendingKeys, DateOnly today)
    {
        var suggestions = new List<(PurchaseSuggestion Suggestion, double Lateness)>();
        foreach (var group in purchases.GroupBy(p => p.Record.NameKey))
        {
            if (pendingKeys.Contains(group.Key))
            {
                continue;
            }

            var days = group.Select(p => p.Day).Distinct().Order().ToList();
            if (days.Count < 2)
            {
                continue;
            }

            var every = (days[^1].DayNumber - days[0].DayNumber) / (double)(days.Count - 1);
            if (every < 1)
            {
                continue;
            }

            var since = today.DayNumber - days[^1].DayNumber;
            var lateness = since / every;
            if (lateness < 0.9)
            {
                continue;
            }

            var latest = group.MaxBy(p => p.Record.PurchasedAt).Record;
            suggestions.Add((new PurchaseSuggestion(latest.Name, latest.Category, days[^1], (int)Math.Round(every)), lateness));
        }

        return [.. suggestions.OrderByDescending(s => s.Lateness).Take(MaxSuggestions).Select(s => s.Suggestion)];
    }

    /// <summary>
    /// A ordem das categorias no supermercado, aprendida com a ordem em que se marcam os artigos: cada ida às compras
    /// (a mesma pessoa, no mesmo dia, pelo menos 3 artigos) dá a cada categoria a sua posição média (0 = primeiro,
    /// 1 = último); as categorias vistas pelo menos duas vezes ordenam-se por essa média.
    /// </summary>
    public static IReadOnlyList<string> CategoryOrder(IEnumerable<(PurchaseRecord Record, DateOnly Day)> purchases)
    {
        var positions = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var trip in purchases.GroupBy(p => (p.Record.UserId, p.Day)))
        {
            var ordered = trip.OrderBy(p => p.Record.PurchasedAt).Select(p => p.Record).ToList();
            if (ordered.Count < 3)
            {
                continue;
            }

            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Category is not { Length: > 0 } category)
                {
                    continue;
                }

                if (!positions.TryGetValue(category, out var list))
                {
                    positions[category] = list = [];
                }

                list.Add(i / (double)(ordered.Count - 1));
            }
        }

        return [.. positions.Where(p => p.Value.Count >= 2).OrderBy(p => p.Value.Average()).Select(p => p.Key)];
    }
}
