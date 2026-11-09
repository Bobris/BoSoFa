using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;
using BoSoFa.Server.Agents;

namespace BoSoFa.Server.Tests;

/// <summary>The checked-in snapshot of models.dev. Run with BOSOFA_UPDATE_MODELS=1 to fetch a new one.</summary>
public class ModelCatalogTest
{
    static string RepositoryRoot()
    {
        var directory = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(directory, "BoSoFa.slnx")))
            directory = Path.GetDirectoryName(directory) ?? throw new InvalidOperationException("Repository root not found.");
        return directory;
    }

    static readonly string[] Providers = ["anthropic", "openai", "opencode-go"];

    [Fact]
    public async Task SnapshotCanBeRefreshed()
    {
        if (Environment.GetEnvironmentVariable("BOSOFA_UPDATE_MODELS") != "1") return;
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(1) };
        var all = JsonNode.Parse(await http.GetStringAsync("https://models.dev/api.json"))!;
        var text = new System.Text.StringBuilder("{\n");
        foreach (var (provider, providerIndex) in Providers.Select((name, index) => (name, index)))
        {
            var models = all[provider]!["models"]!.AsObject().OrderBy(model => model.Key, StringComparer.Ordinal).ToList();
            text.Append($"  \"{provider}\": {{\n");
            for (var i = 0; i < models.Count; i++)
            {
                var (id, model) = models[i];
                var entry = new JsonObject();
                entry["name"] = model!["name"]?.DeepClone();
                if (model["tool_call"]?.GetValue<bool>() == false) entry["tools"] = false;
                var efforts = (model["reasoning_options"] as JsonArray ?? []).OfType<JsonObject>().Where(option => option["type"]?.GetValue<string>() == "effort")
                    .SelectMany(option => option["values"]!.AsArray().Select(value => value!.GetValue<string>())).Where(Harnesses.ReasoningEfforts.Contains).ToArray();
                if (efforts.Length > 0) entry["efforts"] = new JsonArray(efforts.Select(effort => (JsonNode?)JsonValue.Create(effort)).ToArray());
                if (model["limit"]?["input"] is { } input) entry["input_limit"] = input.DeepClone();
                else if (model["limit"]?["context"] is { } context) entry["input_limit"] = context.DeepClone();
                if (model["limit"]?["output"] is { } output) entry["output_limit"] = output.DeepClone();
                foreach (var price in new[] { "input", "output", "cache_read", "cache_write" })
                    if (model["cost"]?[price] is { } value) entry[price] = value.DeepClone();
                text.Append($"    {JsonSerializer.Serialize(id)}: {entry.ToJsonString()}{(i + 1 < models.Count ? "," : "")}\n");
            }
            text.Append($"  }}{(providerIndex + 1 < Providers.Length ? "," : "")}\n");
        }
        await File.WriteAllTextAsync(Path.Combine(RepositoryRoot(), "src", "BoSoFa.Server", "Agents", "models.json"), text.Append("}\n").ToString());
    }

    [Fact]
    public void AKnownModelHasItsLimitsAndPrices()
    {
        var opus = ModelCatalog.Find(AccountKind.AnthropicApiKey, "claude-opus-5-5")!;
        Assert.Equal(1_000_000, opus.InputLimit);
        Assert.Equal(128_000, opus.OutputLimit);
        Assert.Equal((4, 20), (opus.InputPrice, opus.OutputPrice));
        Assert.Null(ModelCatalog.Find(AccountKind.AnthropicApiKey, "no-such-model"));
        Assert.Null(ModelCatalog.Find(AccountKind.ClaudeOAuthToken, "claude-opus-5-5"));
        Assert.NotNull(ModelCatalog.Find(AccountKind.OpenCodeGoApiKey, "glm-5.3"));
        Assert.NotNull(ModelCatalog.Find(AccountKind.OpenAIApiKey, "gpt-5.5"));
    }

    [Fact]
    public void CostCountsCachedTokensAtTheirOwnPrice()
    {
        var model = new ModelInfo(1000, 100, InputPrice: 4, OutputPrice: 20, CacheReadPrice: 0.5, CacheWritePrice: 5);
        // 1M input of which 200k read from the cache and 100k written, and 100k output
        Assert.Equal(700_000 * 4e-6 + 200_000 * 0.5e-6 + 100_000 * 5e-6 + 100_000 * 20e-6, model.CostOf(new(1_000_000, 200_000, 100_000, 100_000, 0)), 9);
        var noCachePrices = new ModelInfo(1000, 100, 4, 20, null, null);
        Assert.Equal(1_000_000 * 4e-6, noCachePrices.CostOf(new(1_000_000, 200_000, 100_000, 0, 0)), 9);
    }
}
