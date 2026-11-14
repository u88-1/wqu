namespace GeminiMenu;

/// <summary>
/// Editing and summaries with a priority list of models of ONE provider (Claude or OpenAI).
/// The provider never changes: when every model of the list fails, the error is reported.
/// </summary>
public sealed class FallbackModelClient : IModelClient
{
    readonly IReadOnlyList<string> _models;
    readonly Func<string, int, IModelClient> _create;
    readonly ModelChain _chain;

    public ModelHealth Health { get; }

    /// <param name="create">Builds the client for (model, HTTP attempts).</param>
    public FallbackModelClient(string provider, IEnumerable<string> models, Func<string, int, IModelClient> create, Action<string>? status = null, ModelHealth? health = null)
    {
        _models = models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
        _create = create;
        Health = health ?? new ModelHealth();
        _chain = new ModelChain(Health, provider, status, Log.Write);
    }

    public Task<GenResult> GenerateAsync(System.Text.Json.Nodes.JsonArray parts, System.Text.Json.Nodes.JsonObject generationConfig, bool disableThinkingWhenPossible, CancellationToken ct) =>
        _chain.RunAsync(_models, null,
            (call, token) => _create(call.Model, call.Attempts).GenerateAsync(parts, generationConfig, disableThinkingWhenPossible, token),
            Math.Max(1, _models.Count), ct);
}

/// <summary>Audio to text with a priority list of transcription models of one provider (OpenAI).</summary>
public sealed class FallbackTranscriber
{
    readonly IReadOnlyList<string> _models;
    readonly Func<string, int, Func<string, CancellationToken, Task<GenResult>>> _create;
    readonly ModelChain _chain;

    public ModelHealth Health { get; }

    public FallbackTranscriber(string provider, IEnumerable<string> models, Func<string, int, Func<string, CancellationToken, Task<GenResult>>> create,
        Action<string>? status = null, ModelHealth? health = null)
    {
        _models = models.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToList();
        _create = create;
        Health = health ?? new ModelHealth();
        _chain = new ModelChain(Health, provider, status, Log.Write);
    }

    public Task<GenResult> TranscribeAsync(string path, CancellationToken ct) =>
        _chain.RunAsync(_models, null, (call, token) => _create(call.Model, call.Attempts)(path, token), Math.Max(1, _models.Count), ct);
}
