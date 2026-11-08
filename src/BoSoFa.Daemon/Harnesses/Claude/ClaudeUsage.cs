using System.Text.Json;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon.Harnesses.Claude;

/// <summary>Reads the limits Claude Code reports with each turn. The shape is not a documented contract, so whatever does
/// not look as expected is left out, and a session never fails because of it.</summary>
public static class ClaudeUsage
{
    /// <summary>The limit windows of a <c>rate_limit_event</c>; null when it holds none.</summary>
    public static AccountUsageReport? LimitsOf(JsonElement line, DateTime now)
    {
        if (ClaudeJson.Obj(line, "rate_limit_info") is not { } info || ClaudeJson.Obj(info, "unifiedWindows") is not { } windows)
            return null;
        var fiveHour = Window(windows, "five_hour");
        var sevenDay = Window(windows, "seven_day");
        return fiveHour == null && sevenDay == null ? null : new(now, fiveHour, sevenDay);
    }

    static UsageWindow? Window(JsonElement windows, string name)
    {
        if (ClaudeJson.Obj(windows, name) is not { ValueKind: JsonValueKind.Object } window ||
            !window.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Number)
            return null;
        var used = utilization.GetDouble();
        DateTime? resets = null;
        if (window.TryGetProperty("resetsAt", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var seconds) &&
            seconds is > 0 and < 253_402_300_800)
            resets = DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
        return new(used, resets, ClaudeJson.Str(window, "status"));
    }
}
