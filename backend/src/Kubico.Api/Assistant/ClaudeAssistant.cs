using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;

namespace Kubico.Api.Assistant;

/// <summary>
/// O assistente com o Claude (API da Anthropic, SDK oficial). Uma só chamada por pedido, com saída estruturada (o
/// esquema de <see cref="AssistantInstructions.Schema"/>): o modelo não chama ferramentas nem mexe em nada.
/// </summary>
public sealed class ClaudeAssistant : IAssistant
{
    private readonly AnthropicClient _client;
    private readonly AssistantOptions _options;

    public ClaudeAssistant(IOptions<AssistantOptions> options)
    {
        _options = options.Value;
        _client = new AnthropicClient { ApiKey = _options.ApiKey, Timeout = TimeSpan.FromSeconds(90) };
    }

    public bool IsAvailable => true;

    public async Task<AssistantDraft> ProposeAsync(AssistantPrompt prompt, CancellationToken ct)
    {
        var response = await _client.Beta.Messages.Create(Request(prompt, _options), ct);
        if (response.StopReason == "refusal")
        {
            return new AssistantDraft(null, []);
        }

        var text = string.Concat(response.Content.Select(b => b.TryPickText(out var t) ? t.Text : ""));
        return AssistantInstructions.Parse(text);
    }

    /// <summary>O pedido à API (público para os testes verificarem o que se envia).</summary>
    public static MessageCreateParams Request(AssistantPrompt prompt, AssistantOptions options)
    {
        List<BetaContentBlockParam> content = [];
        if (prompt.File is { } file)
        {
            var data = Convert.ToBase64String(file);
            if (prompt.MediaType == "application/pdf")
            {
                content.Add(new BetaRequestDocumentBlock { Source = new BetaBase64PdfSource { Data = data } });
            }
            else
            {
                content.Add(new BetaImageBlockParam
                {
                    Source = new BetaBase64ImageSource
                    {
                        Data = data,
                        MediaType = prompt.MediaType switch
                        {
                            "image/png" => MediaType.ImagePng,
                            "image/webp" => MediaType.ImageWebP,
                            _ => MediaType.ImageJpeg,
                        },
                    },
                });
            }
        }

        content.Add(new BetaTextBlockParam { Text = AssistantInstructions.Context(prompt) });

        return new MessageCreateParams
        {
            Model = options.Model,
            MaxTokens = 8000,
            System = AssistantInstructions.System,
            // Tarefa simples de extração: esforço baixo (mais rápido e mais barato).
            OutputConfig = new BetaOutputConfig
            {
                Effort = Effort.Low,
                Format = new BetaJsonOutputFormat { Schema = AssistantInstructions.Schema() },
            },
            // Se o modelo recusar por engano, o servidor da Anthropic tenta outro modelo na mesma chamada.
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            Messages = [new BetaUserMessageParam { Content = content }],
        };
    }
}
