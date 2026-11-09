using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>What is known about one model. Prices are in US dollars per million tokens.</summary>
public sealed record ModelInfo(long? InputLimit, long? OutputLimit, double InputPrice, double OutputPrice, double? CacheReadPrice, double? CacheWritePrice,
    string? Name = null, string[]? Efforts = null, bool ToolCall = true)
{
    public AgentModelDto ToDto(string id) => new(id, Name ?? id, InputLimit, OutputLimit, Efforts ?? [], InputPrice, OutputPrice);

    /// <summary>A list-price estimate: tiered prices and the providers' per-search fees are not counted.</summary>
    public double CostOf(TurnUsage usage)
    {
        var plain = Math.Max(0, usage.InputTokens - usage.CachedInputTokens - usage.CacheWriteTokens);
        return (plain * InputPrice + usage.CachedInputTokens * (CacheReadPrice ?? InputPrice) + usage.CacheWriteTokens * (CacheWritePrice ?? InputPrice) +
                usage.OutputTokens * OutputPrice) / 1_000_000;
    }
}

/// <summary>Limits and prices from a checked-in snapshot of models.dev (docs/server-agent.md §5.5). A model missing from it still runs, with no cost
/// shown and no compaction until the provider reports an overflow.</summary>
public static class ModelCatalog
{
    public const string ResourceName = "agents/models.json";

    static readonly Lazy<JsonObject> Snapshot = new(() =>
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException($"{ResourceName} is missing.");
        return (JsonObject)JsonNode.Parse(stream)!;
    });

    static string? ProviderOf(AccountKind kind) => kind switch
    {
        AccountKind.OpenAIApiKey => "openai",
        AccountKind.AnthropicApiKey => "anthropic",
        AccountKind.OpenCodeGoApiKey => "opencode-go",
        _ => null
    };

    public static ModelInfo? Find(AccountKind kind, string model) =>
        ProviderOf(kind) is { } provider && Snapshot.Value[provider]?[model] is JsonObject entry ? Read(entry) : null;

    /// <summary>Every model of this provider that can use tools, as the snapshot knows them.</summary>
    public static IEnumerable<(string Id, ModelInfo Info)> ToolModels(AccountKind kind)
    {
        if (ProviderOf(kind) is not { } provider || Snapshot.Value[provider] is not JsonObject models) yield break;
        foreach (var (id, entry) in models)
            if (entry is JsonObject found && Read(found) is { ToolCall: true } info) yield return (id, info);
    }

    static ModelInfo Read(JsonObject entry) => new(entry["input_limit"]?.GetValue<long>(), entry["output_limit"]?.GetValue<long>(), Price(entry["input"]) ?? 0, Price(entry["output"]) ?? 0,
        Price(entry["cache_read"]), Price(entry["cache_write"]), entry["name"]?.GetValue<string>(),
        (entry["efforts"] as JsonArray)?.Select(effort => effort!.GetValue<string>()).ToArray(), entry["tools"]?.GetValue<bool>() ?? true);

    static double? Price(JsonNode? node) => node?.GetValue<double>();
}
