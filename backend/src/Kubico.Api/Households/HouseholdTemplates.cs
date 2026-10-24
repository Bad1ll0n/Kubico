using Kubico.Api.Common;
using Kubico.Domain.Chores;
using Kubico.Domain.Households;
using Kubico.Domain.Rewards;
using Kubico.Domain.Shopping;
using Kubico.Infrastructure.Persistence;

namespace Kubico.Api.Households;

/// <summary>Os modelos para começar uma casa já com listas, tarefas e recompensas que façam sentido.</summary>
public enum HouseholdTemplate
{
    Blank = 0,
    FamilyWithKids = 1,
    Couple = 2,
    SharedHouse = 3,
    CaringForParents = 4,
}

/// <summary>Uma tarefa de um modelo (todas começam hoje).</summary>
public record TemplateChore(
    string Title,
    ChoreRecurrence Recurrence,
    int Points = 0,
    ChoreTimeOfDay TimeOfDay = ChoreTimeOfDay.Any,
    IReadOnlyList<string>? Steps = null);

public record TemplateReward(string Title, int Cost);

/// <summary>O que um modelo traz, para a app mostrar antes de escolher.</summary>
public record HouseholdTemplateDto(
    HouseholdTemplate Key,
    string Name,
    string Description,
    IReadOnlyList<string> Lists,
    IReadOnlyList<string> Chores,
    IReadOnlyList<string> Rewards);

/// <summary>
/// Onboarding em menos de dois minutos: ao criar a casa escolhe-se um modelo ("Família com crianças", "Casa
/// partilhada"…) e a casa já nasce com listas, tarefas (incluindo rotinas) e recompensas. Muda-se tudo depois.
/// </summary>
public static class HouseholdTemplates
{
    private sealed record Definition(
        HouseholdTemplate Key,
        string Name,
        string Description,
        string[] Lists,
        TemplateChore[] Chores,
        TemplateReward[] Rewards);

    private static readonly Definition[] All =
    [
        new(HouseholdTemplate.FamilyWithKids, "Família com crianças",
            "Rotinas da manhã e da noite para os mais novos, tarefas da casa com pontos e recompensas para trocar.",
            ["Supermercado", "Farmácia"],
            [
                new("Rotina da manhã", ChoreRecurrence.Daily, 2, ChoreTimeOfDay.Morning, ["Lavar os dentes", "Vestir", "Fazer a cama"]),
                new("Rotina da noite", ChoreRecurrence.Daily, 2, ChoreTimeOfDay.Evening, ["Arrumar os brinquedos", "Vestir o pijama", "Lavar os dentes"]),
                new("Pôr a mesa", ChoreRecurrence.Daily, 1),
                new("Tirar o lixo", ChoreRecurrence.Weekly, 2),
                new("Aspirar a casa", ChoreRecurrence.Weekly, 3),
            ],
            [new("Escolher o filme de sexta", 20), new("Meia hora extra de ecrã", 30), new("Um passeio especial", 100)]),
        new(HouseholdTemplate.Couple, "Casal",
            "As tarefas da casa a dividir por dois e a lista do supermercado.",
            ["Supermercado"],
            [
                new("Tirar o lixo", ChoreRecurrence.Weekly),
                new("Aspirar a casa", ChoreRecurrence.Weekly),
                new("Lavar a roupa", ChoreRecurrence.Weekly),
                new("Limpar a casa de banho", ChoreRecurrence.Weekly),
                new("Regar as plantas", ChoreRecurrence.Weekly),
            ],
            []),
        new(HouseholdTemplate.SharedHouse, "Casa partilhada",
            "Para quem divide casa com amigos ou colegas: as limpezas das zonas comuns e a lista das compras comuns.",
            ["Compras da casa"],
            [
                new("Tirar o lixo e a reciclagem", ChoreRecurrence.Weekly),
                new("Limpar a casa de banho", ChoreRecurrence.Weekly),
                new("Limpar a cozinha", ChoreRecurrence.Weekly),
                new("Aspirar as zonas comuns", ChoreRecurrence.Weekly),
            ],
            []),
        new(HouseholdTemplate.CaringForParents, "Cuidar dos pais ou avós",
            "A medicação, as compras e as visitas organizadas entre a família.",
            ["Farmácia", "Compras para os pais"],
            [
                new("Preparar a caixa dos medicamentos", ChoreRecurrence.Weekly),
                new("Fazer as compras dos pais", ChoreRecurrence.Weekly),
                new("Visita", ChoreRecurrence.Weekly),
                new("Telefonar a saber como estão", ChoreRecurrence.Daily),
            ],
            []),
    ];

    /// <summary>Os modelos (na língua de quem pede), com "Começar do zero" no fim.</summary>
    public static IReadOnlyList<HouseholdTemplateDto> List(string language)
    {
        string T(string text) => Texts.Translate(text, language);
        return
        [
            .. All.Select(d => new HouseholdTemplateDto(
                d.Key,
                T(d.Name),
                T(d.Description),
                [.. d.Lists.Select(T)],
                [.. d.Chores.Select(c => T(c.Title))],
                [.. d.Rewards.Select(r => T(r.Title))])),
            new(HouseholdTemplate.Blank, T("Começar do zero"), T("Uma casa vazia: crias tu as listas e as tarefas."), [], [], []),
        ];
    }

    /// <summary>Junta à casa nova o que o modelo traz, na língua de quem a cria (grava-se com a casa).</summary>
    public static void Apply(
        KubicoDbContext db, Household household, HouseholdTemplate template, Guid userId, DateTimeOffset now, string language)
    {
        string T(string text) => Texts.Translate(text, language);
        if (All.SingleOrDefault(d => d.Key == template) is not { } definition)
        {
            return;
        }

        var today = household.Today(now);
        foreach (var name in definition.Lists)
        {
            db.ShoppingLists.Add(new ShoppingList { HouseholdId = household.Id, Name = T(name), CreatedAt = now });
        }

        foreach (var chore in definition.Chores)
        {
            db.Chores.Add(new Chore
            {
                HouseholdId = household.Id,
                Title = T(chore.Title),
                Points = chore.Points,
                DueDate = today,
                DueDay = today.Day,
                Recurrence = chore.Recurrence,
                TimeOfDay = chore.TimeOfDay,
                Steps = [.. (chore.Steps ?? []).Select(T)],
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        foreach (var reward in definition.Rewards)
        {
            db.Rewards.Add(new Reward
            {
                HouseholdId = household.Id,
                Title = T(reward.Title),
                Cost = reward.Cost,
                CreatedByUserId = userId,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
    }
}
