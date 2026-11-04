using System.Collections.Concurrent;
using Kubico.Api.Assistant;

namespace Kubico.Api.Tests;

/// <summary>O assistente dos testes: responde o que lhe ensinam e guarda o que lhe pediram (não vai à internet).</summary>
public sealed class FakeAssistant : IAssistant
{
    public bool IsAvailable => true;

    public ConcurrentQueue<AssistantPrompt> Prompts { get; } = new();

    /// <summary>A resposta a dar; por omissão, nenhuma ação.</summary>
    public Func<AssistantPrompt, AssistantDraft> Respond { get; set; } = _ => new AssistantDraft("Nada a propor.", []);

    public Task<AssistantDraft> ProposeAsync(AssistantPrompt prompt, CancellationToken ct)
    {
        Prompts.Enqueue(prompt);
        return Task.FromResult(Respond(prompt));
    }

    public static DraftAction Action(
        string kind, string? list = null, string[]? items = null, string? title = null, string? date = null,
        string? endDate = null, string? start = null, string? end = null, decimal? euros = null, string? location = null) =>
        new(kind, list, items, title, date, endDate, start, end, euros, location);
}
