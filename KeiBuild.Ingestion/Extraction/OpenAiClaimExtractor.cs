using System.ClientModel;
using OpenAI.Chat;

namespace KeiBuild.Ingestion.Extraction;

public sealed class OpenAiClaimExtractor(string model, ChatClient client) : IClaimExtractor
{
    public OpenAiClaimExtractor(string model, string apiKey) : this(model, new ChatClient(model, apiKey)) { }

    public string Name => $"OpenAI/{model}";

    public async Task<string> ExtractAsync(ExtractionInput input, CancellationToken ct)
    {
        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = 9_000,
            ResponseFormat = ChatResponseFormat.CreateJsonSchemaFormat(
                "keibuild_fitment_claims", ExtractionPrompt.JsonSchema(input.ChassisCodes), jsonSchemaIsStrict: true)
        };
        ChatMessage[] messages =
        [
            new SystemChatMessage(ExtractionPrompt.Instructions),
            new UserChatMessage(ExtractionPrompt.SourceMessage(input))
        ];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));

        ChatCompletion response;
        try
        {
            response = await client.CompleteChatAsync(messages, options, deadline.Token);
        }
        catch (ClientResultException error)
        {
            // The provider's full error might quote document text. Keep the console message to the status and next actions.
            throw new IngestionException($"OpenAI extraction failed (HTTP {error.Status}). Check the network, API key, quota, and model support for Structured Outputs.", error);
        }
        catch (OperationCanceledException error) when (!ct.IsCancellationRequested)
        {
            throw new IngestionException("OpenAI extraction timed out. Retry or use a smaller permitted excerpt.", error);
        }

        if (response.FinishReason != ChatFinishReason.Stop || !string.IsNullOrEmpty(response.Refusal) ||
            response.Content.Count == 0)
            throw new IngestionException("OpenAI did not return a complete structured extraction. Try a smaller permitted excerpt or check the provider settings.");

        return string.Concat(response.Content.Select(part => part.Text));
    }
}
