using System.Text.Json;

namespace BoSoFa.Daemon.Tools;

/// <summary>The model got the arguments wrong; it reads this and tries again.</summary>
sealed class ToolProblem(string message) : Exception(message);

/// <summary>The JSON arguments of one tool call.</summary>
readonly struct ToolArguments(JsonElement root)
{
    public string? Text(string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    public string Required(string name) => Text(name) ?? throw new ToolProblem($"The argument {name} is missing.");

    public bool Flag(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public int? Number(string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
}
