namespace Kubico.Domain.Expenses;

/// <summary>Uma despesa da casa (F4-10): quem pagou, quanto, e como se divide (a parte de cada um, em cêntimos).</summary>
public class Expense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public required string Description { get; set; }
    public int AmountCents { get; set; }
    public Guid PaidByUserId { get; set; }
    public DateOnly Date { get; set; }
    public string? Category { get; set; }

    /// <summary>A parte de cada pessoa; soma sempre <see cref="AmountCents"/>.</summary>
    public List<ExpenseShare> Shares { get; set; } = [];

    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public class ExpenseShare
{
    public Guid UserId { get; set; }
    public int AmountCents { get; set; }
}

/// <summary>Um acerto: <see cref="FromUserId"/> pagou <see cref="AmountCents"/> a <see cref="ToUserId"/>.</summary>
public class Settlement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid FromUserId { get; set; }
    public Guid ToUserId { get; set; }
    public int AmountCents { get; set; }
    public DateOnly Date { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record Transfer(Guid FromUserId, Guid ToUserId, int AmountCents);

public static class ExpenseMath
{
    /// <summary>
    /// Divide <paramref name="amountCents"/> em partes iguais por <paramref name="people"/>; os cêntimos que sobram vão
    /// um a um para os primeiros (3,00 € por 3 = 1,00 cada; 1,00 € por 3 = 0,34 + 0,33 + 0,33).
    /// </summary>
    public static List<ExpenseShare> SplitEqually(int amountCents, IReadOnlyList<Guid> people)
    {
        var each = amountCents / people.Count;
        var rest = amountCents - each * people.Count;
        return [.. people.Select((id, i) => new ExpenseShare { UserId = id, AmountCents = each + (i < rest ? 1 : 0) })];
    }

    /// <summary>
    /// O saldo de cada um (positivo: tem a receber; negativo: deve). Soma sempre zero: cada despesa dá a quem pagou o
    /// total e tira a cada um a sua parte; cada acerto passa o valor de quem deve para quem recebe.
    /// </summary>
    public static Dictionary<Guid, int> Balances(IEnumerable<Expense> expenses, IEnumerable<Settlement> settlements)
    {
        var net = new Dictionary<Guid, int>();
        void Add(Guid id, int cents) => net[id] = net.GetValueOrDefault(id) + cents;
        foreach (var e in expenses)
        {
            Add(e.PaidByUserId, e.AmountCents);
            foreach (var share in e.Shares)
            {
                Add(share.UserId, -share.AmountCents);
            }
        }

        foreach (var s in settlements)
        {
            Add(s.FromUserId, s.AmountCents);
            Add(s.ToUserId, -s.AmountCents);
        }

        return net;
    }

    /// <summary>
    /// Quem paga a quem para ficarem todos acertados, com poucas transferências: o que mais deve paga ao que mais tem
    /// a receber, até alguém ficar a zero, e repete-se. No máximo n − 1 transferências.
    /// </summary>
    public static List<Transfer> Settle(IReadOnlyDictionary<Guid, int> balances)
    {
        var owe = balances.Where(b => b.Value < 0).Select(b => (b.Key, Cents: -b.Value)).OrderByDescending(b => b.Cents).ThenBy(b => b.Key).ToList();
        var get = balances.Where(b => b.Value > 0).Select(b => (b.Key, b.Value)).OrderByDescending(b => b.Value).ThenBy(b => b.Key).ToList();
        var transfers = new List<Transfer>();
        int i = 0, j = 0;
        while (i < owe.Count && j < get.Count)
        {
            var amount = Math.Min(owe[i].Cents, get[j].Value);
            transfers.Add(new Transfer(owe[i].Key, get[j].Key, amount));
            owe[i] = (owe[i].Key, owe[i].Cents - amount);
            get[j] = (get[j].Key, get[j].Value - amount);
            if (owe[i].Cents == 0)
            {
                i++;
            }

            if (get[j].Value == 0)
            {
                j++;
            }
        }

        return transfers;
    }
}
