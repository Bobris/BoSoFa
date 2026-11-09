using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>What is known about one model. Prices are in US dollars per million tokens.</summary>
public sealed record ModelInfo(long? InputLimit, long? OutputLimit, double InputPrice, double OutputPrice, double? CacheReadPrice, double? CacheWritePrice)
{
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

    public static ModelInfo? Find(AccountKind kind, string model)
    {
        if (ProviderOf(kind) is not { } provider || Snapshot.Value[provider]?[model] is not JsonObject entry) return null;
        return new(entry["input_limit"]?.GetValue<long>(), entry["output_limit"]?.GetValue<long>(), Price(entry["input"]) ?? 0, Price(entry["output"]) ?? 0,
            Price(entry["cache_read"]), Price(entry["cache_write"]));
    }

    static double? Price(JsonNode? node) => node?.GetValue<double>();
}
