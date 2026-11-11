using System.Globalization;
using System.Text;
using System.Text.Json;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Harnesses;

/// <summary>Reads the models each harness reports, with the reasoning levels of each, into one shape.</summary>
public static class HarnessModels
{
    /// <summary>Claude Code's answer to <c>initialize</c>: its <c>models</c>, whose "default" entry stands for no model named.</summary>
    public static HarnessModelDto[] FromClaudeInitialize(JsonElement initialized)
    {
        if (ClaudeJson.Obj(initialized, "models") is not { ValueKind: JsonValueKind.Array } models) return [];
        var result = new List<HarnessModelDto>();
        foreach (var model in models.EnumerateArray())
        {
            if (ClaudeJson.Str(model, "value") is not { Length: > 0 } value) continue;
            var efforts = ClaudeJson.Bool(model, "supportsEffort") ? Strings(model, "supportedEffortLevels") : [];
            result.Add(new(value == "default" ? "" : value, ClaudeJson.Str(model, "displayName") ?? value, ClaudeJson.Str(model, "description"), efforts,
                IsDefault: value == "default"));
        }
        return [..result];
    }

    /// <summary>Codex's <c>model/list</c>: the model slug is what a thread or turn takes.</summary>
    public static HarnessModelDto[] FromCodexList(JsonElement listed)
    {
        if (ClaudeJson.Obj(listed, "data") is not { ValueKind: JsonValueKind.Array } data) return [];
        var result = new List<HarnessModelDto>();
        foreach (var model in data.EnumerateArray())
        {
            if (ClaudeJson.Bool(model, "hidden") || (ClaudeJson.Str(model, "model") ?? ClaudeJson.Str(model, "id")) is not { Length: > 0 } id) continue;
            var efforts = ClaudeJson.Obj(model, "supportedReasoningEfforts") is { ValueKind: JsonValueKind.Array } options
                ? options.EnumerateArray().Select(option => ClaudeJson.Str(option, "reasoningEffort")).OfType<string>().ToArray()
                : [];
            result.Add(new(id, ClaudeJson.Str(model, "displayName") ?? id, ClaudeJson.Str(model, "description"), efforts,
                ClaudeJson.Str(model, "defaultReasoningEffort"), ClaudeJson.Bool(model, "isDefault")));
        }
        return [..result];
    }

    /// <summary><c>opencode models --verbose</c> prints each model's id on a line of its own, then its JSON; the reasoning levels are the
    /// names of its variants.</summary>
    public static HarnessModelDto[] FromOpenCodeVerbose(string output)
    {
        var result = new List<HarnessModelDto>();
        string? id = null;
        var json = new StringBuilder();
        foreach (var line in output.Split('\n'))
        {
            if (id == null)
            {
                if (line.Trim() is { Length: > 0 } candidate && !candidate.StartsWith('{') && candidate.Contains('/')) id = candidate;
                continue;
            }
            json.Append(line).Append('\n');
            if (line.TrimEnd() != "}") continue; // the object's own closing brace is the only one at the start of a line
            try
            {
                using var document = JsonDocument.Parse(json.ToString());
                var model = document.RootElement;
                if (ClaudeJson.Str(model, "status") is not ("deprecated"))
                {
                    var efforts = ClaudeJson.Obj(model, "variants") is { ValueKind: JsonValueKind.Object } variants
                        ? variants.EnumerateObject().Select(variant => variant.Name).ToArray()
                        : [];
                    result.Add(new(id, ClaudeJson.Str(model, "name") ?? id, Describe(id, model), efforts));
                }
            }
            catch (JsonException) { /* skipped; the next id starts afresh */ }
            id = null;
            json.Clear();
        }
        return [..result];
    }

    static string? Describe(string id, JsonElement model)
    {
        var parts = new List<string> { id[..id.IndexOf('/')] };
        if (ClaudeJson.Obj(model, "limit") is { } limit && ClaudeJson.Long(limit, "context") is > 0 and var context)
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{(context >= 1_000_000 ? $"{context / 1_000_000.0:0.#}M" : $"{context / 1000}K")} context"));
        if (ClaudeJson.Obj(model, "cost") is { } cost && cost.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Number &&
            cost.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Number)
            parts.Add(input.GetDouble() == 0 && output.GetDouble() == 0
                ? "free"
                : string.Create(CultureInfo.InvariantCulture, $"${input.GetDouble():0.##} / ${output.GetDouble():0.##} per M"));
        return string.Join(" · ", parts);
    }

    static string[] Strings(JsonElement element, string name) =>
        ClaudeJson.Obj(element, name) is { ValueKind: JsonValueKind.Array } array
            ? array.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];
}
