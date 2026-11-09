using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Daemon.Harnesses.Claude;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Harnesses.Codex;

/// <summary>Asks <c>codex app-server</c> for its sign-in's plan and limits. The fields are not a documented contract, so
/// whatever does not look as expected is left out.</summary>
public static class CodexUsage
{
    static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    public static readonly CodexUsageResult NotSignedIn = new(false, null, [], null);

    public static async Task<CodexUsageResult> ReadAsync(JsonRpcProcess rpc)
    {
        await rpc.RequestAsync("initialize", new JsonObject
        {
            ["clientInfo"] = new JsonObject { ["name"] = Brand.Id, ["title"] = Brand.Name, ["version"] = Cli.Version },
            ["capabilities"] = new JsonObject { ["experimentalApi"] = false }
        }, RequestTimeout);
        await rpc.NotifyAsync("initialized");
        var read = await rpc.RequestAsync("account/read", new JsonObject { ["refreshToken"] = false }, RequestTimeout);
        if (ClaudeJson.Obj(read, "account") is not { ValueKind: JsonValueKind.Object } account) return NotSignedIn;
        var plan = ClaudeJson.Str(account, "planType");
        try { return Of(plan, await rpc.RequestAsync("account/rateLimits/read", null, RequestTimeout)); }
        catch (JsonRpcException) { return new(true, plan, [], null); } // an API key has a plan of sorts but no limits
    }

    public static CodexUsageResult Of(string? plan, JsonElement result)
    {
        if (ClaudeJson.Obj(result, "rateLimits") is not { ValueKind: JsonValueKind.Object } limits) return new(true, plan, [], null);
        CodexWindow?[] windows = [Window(limits, "primary"), Window(limits, "secondary")];
        var credits = ClaudeJson.Obj(limits, "credits") is { } balance ? ClaudeJson.Str(balance, "balance") : null;
        return new(true, plan ?? ClaudeJson.Str(limits, "planType"), windows.OfType<CodexWindow>().ToArray(), credits);
    }

    static CodexWindow? Window(JsonElement limits, string name)
    {
        if (ClaudeJson.Obj(limits, name) is not { ValueKind: JsonValueKind.Object } window ||
            !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number)
            return null;
        var minutes = ClaudeJson.Long(window, "windowDurationMins");
        if (minutes is <= 0 or > int.MaxValue) return null;
        DateTime? resets = ClaudeJson.Long(window, "resetsAt") is > 0 and < 253_402_300_800 and var seconds
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : null;
        return new((int)minutes, new(used.GetDouble() / 100, resets, null));
    }
}
