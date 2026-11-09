using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>What a provider account can run, for the new-session form: the provider's own list, where it can say, joined with what the snapshot knows.</summary>
public static class ProviderModels
{
    public static async Task<AgentModelDto[]> ListAsync(HttpClient http, AccountKind kind, string secret, ServerOptions options, CancellationToken cancellation)
    {
        var known = ModelCatalog.ToolModels(kind).ToDictionary(model => model.Id, model => model.Info);
        try
        {
            if (kind == AccountKind.OpenCodeGoApiKey)
                return (await OpenCodeGo.ListModelsAsync(http, options.Agent.OpenCodeGoBaseUrl, cancellation))
                    .Select(model => known.TryGetValue(model.Id, out var info) ? model with { InputPrice = info.InputPrice, OutputPrice = info.OutputPrice } : model).ToArray();
            // Providers also list embeddings, image models and retired ones; only those the snapshot knows can run an agent here.
            var offered = await ProviderIdsAsync(http, kind, secret, options, cancellation);
            var usable = offered.Where(known.ContainsKey).Select(id => known[id].ToDto(id)).ToArray();
            if (usable.Length > 0) return usable;
        }
        catch (Exception problem) when (problem is HttpRequestException or JsonException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            // The list is a convenience: what the snapshot knows will do.
        }
        return known.Select(model => model.Value.ToDto(model.Key)).OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static async Task<List<string>> ProviderIdsAsync(HttpClient http, AccountKind kind, string secret, ServerOptions options, CancellationToken cancellation)
    {
        var anthropic = kind == AccountKind.AnthropicApiKey;
        var endpoint = anthropic
            ? (options.Agent.AnthropicEndpoint is { Length: > 0 } messages ? new Uri(messages) : AnthropicMessagesExecutor.AnthropicEndpoint)
            : options.Agent.OpenAIEndpoint is { Length: > 0 } responses ? new Uri(responses) : OpenAIResponsesExecutor.OpenAIEndpoint;
        var address = endpoint.ToString();
        using var message = new HttpRequestMessage(HttpMethod.Get, address[..address.LastIndexOf('/')] + "/models" + (anthropic ? "?limit=1000" : ""));
        if (anthropic)
        {
            message.Headers.TryAddWithoutValidation("x-api-key", secret);
            message.Headers.TryAddWithoutValidation("anthropic-version", AnthropicMessagesExecutor.ApiVersion);
        }
        else message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        message.Headers.TryAddWithoutValidation("User-Agent", ProviderHttp.UserAgent);
        using var response = await http.SendAsync(message, cancellation);
        response.EnsureSuccessStatusCode();
        var ids = new List<string>();
        foreach (var model in (JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellation))?["data"] as JsonArray ?? []).OfType<JsonObject>())
            if (model["id"] is JsonValue value && value.TryGetValue<string>(out var id)) ids.Add(id);
        return ids;
    }
}
