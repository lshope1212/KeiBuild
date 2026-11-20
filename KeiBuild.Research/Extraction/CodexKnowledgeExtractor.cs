using System.Diagnostics;
using KeiBuild.Ingestion.Extraction;

namespace KeiBuild.Research.Extraction;

public sealed class CodexKnowledgeExtractor : IKnowledgeExtractor
{
    private readonly CodexCli cli;
    public string Name => "Codex/ChatGPT";

    public CodexKnowledgeExtractor() => cli = new();
    internal CodexKnowledgeExtractor(Func<ProcessStartInfo, string, CancellationToken, Task<CodexCli.CodexExit>> execute) =>
        cli = new(execute);

    public async Task<KnowledgeResponse> ExtractAsync(ExtractionInput input, CancellationToken ct)
    {
        var prompt = KnowledgePrompt.Instructions + "\n" + KnowledgePrompt.SourceMessage(input);
        var turn = await cli.RunAsync(KnowledgePrompt.Schema(input.ChassisCodes).ToArray(), prompt, discovery: false, ct);
        return turn.Reply;
    }

    public Task CheckAsync(CancellationToken ct) => cli.CheckAsync(ct);
}
