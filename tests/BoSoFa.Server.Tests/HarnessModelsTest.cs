using System.Text.Json;
using BoSoFa.Daemon.Harnesses;
using BoSoFa.Daemon.Harnesses.Claude;

namespace BoSoFa.Server.Tests;

/// <summary>The models each harness reports, as recorded from Claude Code 2.1, Codex 0.167 and OpenCode 1.19.</summary>
public class HarnessModelsTest
{
    [Fact]
    public void ClaudeCodesDefaultEntryStandsForNoModelNamed()
    {
        var initialized = JsonDocument.Parse("""
            {"models":[
              {"value":"default","displayName":"Default (recommended)","description":"Opus 5.5 · Best for everyday, complex tasks","supportsEffort":true,"supportedEffortLevels":["low","medium","high","xhigh","max"]},
              {"value":"haiku","displayName":"Haiku","description":"Haiku 5.5 · Fastest for quick answers","supportsEffort":true,"supportedEffortLevels":["low","high"]},
              {"value":"old","displayName":"Old","supportsEffort":false,"supportedEffortLevels":["low"]}]}
            """).RootElement;
        var models = HarnessModels.FromClaudeInitialize(initialized);
        Assert.Equal(["", "haiku", "old"], models.Select(model => model.Id));
        Assert.True(models[0].IsDefault);
        Assert.Equal("Haiku", models[1].Name);
        Assert.Equal(["low", "high"], models[1].ReasoningEfforts);
        Assert.Empty(models[2].ReasoningEfforts); // no effort without support for it
    }

    [Fact]
    public void CodexListsVisibleModelsWithTheirLevels()
    {
        var listed = JsonDocument.Parse("""
            {"data":[
              {"id":"gpt-5.5","model":"gpt-5.5","displayName":"GPT-5.5","description":"Frontier","hidden":false,"isDefault":true,"defaultReasoningEffort":"medium",
               "supportedReasoningEfforts":[{"reasoningEffort":"low","description":"Fast"},{"reasoningEffort":"medium","description":"Balanced"},{"reasoningEffort":"high","description":"Deep"}]},
              {"id":"secret","model":"secret","displayName":"Secret","description":"","hidden":true,"isDefault":false,"defaultReasoningEffort":"low","supportedReasoningEfforts":[]}]}
            """).RootElement;
        var model = Assert.Single(HarnessModels.FromCodexList(listed));
        Assert.Equal(("gpt-5.5", "GPT-5.5", "medium", true), (model.Id, model.Name, model.DefaultEffort, model.IsDefault));
        Assert.Equal(["low", "medium", "high"], model.ReasoningEfforts);
    }

    [Fact]
    public void OpenCodesVariantsAreItsLevels()
    {
        const string output = """
            opencode/beam-free
            {
              "id": "beam-free",
              "providerID": "opencode",
              "name": "Beam Free",
              "status": "active",
              "cost": { "input": 0, "output": 0 },
              "limit": { "context": 1048576, "output": 131072 },
              "variants": {
                "low": { "reasoningEffort": "low" },
                "high": { "reasoningEffort": "high" }
              }
            }
            anthropic/claude-haiku-5-5
            {
              "id": "claude-haiku-5-5",
              "name": "Claude Haiku 5.5",
              "status": "active",
              "cost": { "input": 1, "output": 5 },
              "limit": { "context": 200000, "output": 64000 }
            }
            """;
        var models = HarnessModels.FromOpenCodeVerbose(output);
        Assert.Equal(["opencode/beam-free", "anthropic/claude-haiku-5-5"], models.Select(model => model.Id));
        Assert.Equal(["low", "high"], models[0].ReasoningEfforts);
        Assert.Equal("opencode · 1M context · free", models[0].Description);
        Assert.Equal(("Claude Haiku 5.5", "anthropic · 200K context · $1 / $5 per M"), (models[1].Name, models[1].Description));
        Assert.Empty(models[1].ReasoningEfforts);
    }

    [Fact]
    public void ClaudeCodeGetsTheChosenEffort() =>
        Assert.Equal(["--effort", "high"], ClaudeProcess.Arguments(new ClaudeLaunch("/w", "default", "haiku", null, Effort: "high"))
            .SkipWhile(argument => argument != "--effort").Take(2));
}
