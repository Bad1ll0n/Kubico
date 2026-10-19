namespace Kubico.Domain.Rewards;

public enum AllowancePeriod
{
    Weekly = 0,
    Monthly = 1,
}

/// <summary>
/// A mesada de um membro: um valor fixo por semana (de segunda a domingo) ou por mês e, se quiserem, um extra por
/// cada tarefa feita nesse período. Os valores são em cêntimos de euro.
/// </summary>
public class Allowance
{
    public Guid HouseholdId { get; set; }
    public Guid UserId { get; set; }
    public int AmountCents { get; set; }
    public int BonusPerChoreCents { get; set; }
    public AllowancePeriod Period { get; set; }

    /// <summary>A partir de quando conta (o início do período em que foi criada ou mudou de semanal para mensal).</summary>
    public DateOnly StartDate { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Quanto se ganha num período com estas tarefas feitas.</summary>
    public int AmountFor(int choresDone) => AmountCents + BonusPerChoreCents * choresDone;

    /// <summary>O primeiro dia do período que contém <paramref name="day"/> (segunda-feira, ou dia 1).</summary>
    public static DateOnly PeriodStart(DateOnly day, AllowancePeriod period) => period == AllowancePeriod.Weekly
        ? day.AddDays(-(((int)day.DayOfWeek + 6) % 7))
        : new DateOnly(day.Year, day.Month, 1);

    /// <summary>O último dia (inclusive) do período que começa em <paramref name="start"/>.</summary>
    public static DateOnly PeriodEnd(DateOnly start, AllowancePeriod period) => period == AllowancePeriod.Weekly
        ? start.AddDays(6)
        : start.AddMonths(1).AddDays(-1);

    /// <summary>Os inícios dos períodos desde <see cref="StartDate"/> até ao de hoje (no máximo os últimos <paramref name="max"/>).</summary>
    public IEnumerable<DateOnly> PeriodStarts(DateOnly today, int max)
    {
        var starts = new List<DateOnly>();
        var first = PeriodStart(StartDate, Period);
        for (var start = PeriodStart(today, Period); start >= first && starts.Count < max; start = Previous(start))
        {
            starts.Add(start);
        }

        return starts;
    }

    private DateOnly Previous(DateOnly start) => Period == AllowancePeriod.Weekly ? start.AddDays(-7) : start.AddMonths(-1);

    /// <summary>Euros como se escrevem em Portugal: "6,20 €".</summary>
    public static string Format(int cents) => $"{cents / 100},{cents % 100:00} €";
}

/// <summary>Uma mesada paga: o período, quanto foi (com quantas tarefas) e quem pagou.</summary>
public class AllowancePayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid HouseholdId { get; set; }
    public Guid UserId { get; set; }
    public DateOnly PeriodStart { get; set; }
    public DateOnly PeriodEnd { get; set; }
    public int AmountCents { get; set; }
    public int ChoresDone { get; set; }
    public DateTimeOffset PaidAt { get; set; }
    public Guid PaidByUserId { get; set; }
}
