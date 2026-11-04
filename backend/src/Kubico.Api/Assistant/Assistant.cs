using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kubico.Api.Assistant;

/// <summary>
/// O assistente (F4-24): transforma uma frase, a fotografia de um talão ou o PDF do calendário da escola numa lista
/// de ações propostas. Não faz nada sozinho: a app mostra as propostas, a pessoa escolhe as que quer e cada uma segue
/// pelos endpoints normais, com as permissões de quem pediu.
/// </summary>
public interface IAssistant
{
    /// <summary>Falso quando não há chave da API configurada (<c>Assistant:ApiKey</c>): o assistente não aparece.</summary>
    bool IsAvailable { get; }

    Task<AssistantDraft> ProposeAsync(AssistantPrompt prompt, CancellationToken ct);
}

/// <summary>
/// O que vai para o modelo. Leva só o necessário: a data de hoje, os nomes das listas e os artigos por comprar (para
/// marcar os do talão). Nunca leva nomes, idades ou dados das crianças, nem de mais ninguém da casa.
/// </summary>
public record AssistantPrompt(
    string? Text,
    byte[]? File,
    string? MediaType,
    DateOnly Today,
    string Language,
    IReadOnlyList<string> Lists,
    IReadOnlyList<string> UncheckedItems);

/// <summary>A resposta do modelo, ainda por validar (ver <see cref="AssistantEndpoints"/>).</summary>
public record AssistantDraft(string? Reply, IReadOnlyList<DraftAction> Actions);

/// <summary>Uma ação proposta, como o modelo a escreve (datas em texto ISO, horas HH:MM, valor em euros).</summary>
public record DraftAction(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("list")] string? List,
    [property: JsonPropertyName("items")] IReadOnlyList<string>? Items,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("date")] string? Date,
    [property: JsonPropertyName("endDate")] string? EndDate,
    [property: JsonPropertyName("start")] string? Start,
    [property: JsonPropertyName("end")] string? End,
    [property: JsonPropertyName("amountEuros")] decimal? AmountEuros,
    [property: JsonPropertyName("location")] string? Location);

public class AssistantOptions
{
    public const string SectionName = "Assistant";

    /// <summary>A chave da API da Anthropic. Sem ela, o assistente fica desligado.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "claude-opus-5-5";

    /// <summary>Pedidos por casa e por dia.</summary>
    public int DailyLimit { get; set; } = 30;
}

/// <summary>Sem chave da API: o assistente não existe.</summary>
public sealed class DisabledAssistant : IAssistant
{
    public bool IsAvailable => false;

    public Task<AssistantDraft> ProposeAsync(AssistantPrompt prompt, CancellationToken ct) =>
        throw new InvalidOperationException("O assistente não está configurado.");
}

/// <summary>As instruções e o formato da resposta, partilhados por todas as formas de chamar o modelo.</summary>
public static class AssistantInstructions
{
    public static readonly string[] Kinds = ["add_items", "check_items", "create_event", "create_chore", "create_expense"];

    public const string System = """
        És o assistente do Kubico, uma app para gerir a casa em família (em Portugal). Recebes um pedido (uma frase, a
        fotografia de um talão de compras ou um documento, como o calendário escolar) e propões ações. Não executas
        nada: a pessoa vê as propostas e escolhe as que quer.

        Ações possíveis (campo "kind"):
        - add_items: pôr artigos numa lista de compras. "items" com os nomes curtos ("Leite", "Pão"), "list" com o nome
          de uma das listas da casa (ou null para a primeira).
        - check_items: marcar como comprados artigos que já estão nas listas. Usa-a com um talão: em "items", os nomes
          EXATAMENTE como aparecem em "Artigos por comprar", só os que o talão mostra.
        - create_event: um evento no calendário. "title", "date" (AAAA-MM-DD), e "start"/"end" (HH:MM) se tiver hora;
          sem hora é de dia inteiro, com "endDate" se durar vários dias (férias, interrupções letivas). "location" se
          houver.
        - create_chore: uma tarefa da casa. "title" e "date" (o prazo) se houver.
        - create_expense: uma despesa partilhada. "title" (o que foi, por exemplo o nome da loja), "amountEuros" (o
          total pago) e "date".

        Regras:
        - Datas relativas ("amanhã", "sexta") contam a partir de hoje, que vem no pedido. Nunca inventes datas nem
          valores que não estejam no pedido ou no documento.
        - Com um talão: check_items para o que já estava na lista e uma create_expense com o total.
        - Com o calendário escolar: um create_event por período sem aulas, feriado escolar ou data importante.
        - Os campos que não se aplicam vão a null; "items" vai a null se não houver artigos.
        - Em "reply", uma frase curta para a pessoa, na língua indicada no pedido (pt = português de Portugal,
          en = inglês), a dizer o que propões ou porque não propões nada. Os títulos e nomes dos artigos ficam na língua
          em que vieram.
        - Se o pedido não tiver nada a ver com a casa, não proponhas ações e diz isso em "reply".
        """;

    /// <summary>O esquema JSON da resposta (saída estruturada: o modelo tem de o cumprir).</summary>
    public static Dictionary<string, JsonElement> Schema()
    {
        static object Nullable(string type) => new { type = new[] { type, "null" } };
        var action = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "kind", "list", "items", "title", "date", "endDate", "start", "end", "amountEuros", "location" },
            properties = new Dictionary<string, object>
            {
                ["kind"] = new { type = "string", @enum = Kinds },
                ["list"] = Nullable("string"),
                ["items"] = new { type = new[] { "array", "null" }, items = new { type = "string" } },
                ["title"] = Nullable("string"),
                ["date"] = Nullable("string"),
                ["endDate"] = Nullable("string"),
                ["start"] = Nullable("string"),
                ["end"] = Nullable("string"),
                ["amountEuros"] = Nullable("number"),
                ["location"] = Nullable("string"),
            },
        };
        return new Dictionary<string, JsonElement>
        {
            ["type"] = JsonSerializer.SerializeToElement("object"),
            ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
            ["required"] = JsonSerializer.SerializeToElement(new[] { "reply", "actions" }),
            ["properties"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["reply"] = new { type = "string" },
                ["actions"] = new { type = "array", items = action },
            }),
        };
    }

    /// <summary>O texto do pedido: hoje, a língua, as listas e os artigos por comprar, e o que a pessoa escreveu.</summary>
    public static string Context(AssistantPrompt prompt)
    {
        var lines = new List<string>
        {
            $"Hoje: {prompt.Today:yyyy-MM-dd} ({prompt.Today.DayOfWeek}).",
            $"Língua da resposta: {prompt.Language}.",
            $"Listas de compras: {(prompt.Lists.Count == 0 ? "(nenhuma)" : string.Join(" | ", prompt.Lists))}.",
        };
        if (prompt.File is not null)
        {
            lines.Add($"Artigos por comprar: {(prompt.UncheckedItems.Count == 0 ? "(nenhum)" : string.Join(" | ", prompt.UncheckedItems))}.");
        }

        lines.Add(string.IsNullOrWhiteSpace(prompt.Text)
            ? prompt.MediaType == "application/pdf"
                ? "Pedido: propõe os eventos deste documento."
                : "Pedido: este é um talão de compras."
            : $"Pedido: {prompt.Text.Trim()}");
        return string.Join('\n', lines);
    }

    public static AssistantDraft Parse(string json)
    {
        var parsed = JsonSerializer.Deserialize<ParsedDraft>(json, JsonOptions)
            ?? throw new JsonException("Resposta vazia do assistente.");
        return new AssistantDraft(parsed.Reply, parsed.Actions ?? []);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private sealed record ParsedDraft(
        [property: JsonPropertyName("reply")] string? Reply,
        [property: JsonPropertyName("actions")] List<DraftAction>? Actions);
}
