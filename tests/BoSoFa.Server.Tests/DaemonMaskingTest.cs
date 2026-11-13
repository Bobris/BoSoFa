using System.Collections.Concurrent;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

/// <summary>The daemon's first line (docs/secrets.md §5.2): what a session reports is masked before the outbox writes it to
/// disk, its live text as it streams, and the daemon's log.</summary>
public sealed class DaemonMaskingTest : IAsyncLifetime
{
    const string Secret = "daemon-secret-value-0042";
    readonly string _root = Directory.CreateTempSubdirectory("bosofa-masking-").FullName;
    readonly StringWriter _log = new();
    Outbox _outbox = null!;
    SessionHost _host = null!;

    public Task InitializeAsync()
    {
        _outbox = new Outbox(Path.Combine(_root, "outbox"));
        _host = new SessionHost(_root, _outbox, new(), _log);
        _host.Masker.Retain(Secret);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _host.DisposeAsync();
        _outbox.Dispose();
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    [Fact]
    public void ReportsAreMaskedBeforeTheOutbox()
    {
        _host.Report(5, new ItemReport(SessionItemKind.ToolResult, DateTime.UtcNow, $"printed {Secret}", Data: $"{{\"command\":\"echo {Secret}\"}}"));
        _host.Report(5, new StatusReport(SessionStatus.Failed, Error: $"failed with {Secret}"));
        _host.Report(5, new ApprovalRequestedReport("k", DateTime.UtcNow, "Bash", null, $"{{\"command\":\"curl -H {Secret}\"}}", $"curl {Secret}"));
        var records = _outbox.After(0, 100, 1 << 20).Select(entry => entry.Record).ToList();
        Assert.Equal(3, records.Count);
        var item = Assert.IsType<ItemReport>(records[0].Report);
        Assert.Equal(("printed ***", "{\"command\":\"echo ***\"}"), (item.Text, item.Data));
        Assert.Equal("failed with ***", Assert.IsType<StatusReport>(records[1].Report).Error);
        var approval = Assert.IsType<ApprovalRequestedReport>(records[2].Report);
        Assert.Equal(("{\"command\":\"curl -H ***\"}", "curl ***"), (approval.Input, approval.Description));
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            Assert.DoesNotContain(Secret, File.ReadAllText(file));
    }

    [Fact]
    public void LiveTextIsMaskedAsItStreams()
    {
        var sent = new ConcurrentQueue<DeltaMessage>();
        _host.DeltaSink = sent.Enqueue;
        foreach (var part in new[] { "token: dae", "mon-secret-", "value-0042 and more" }) _host.Delta(5, "r1", part);
        var shown = string.Concat(sent.Select(delta => delta.Text));
        Assert.Equal("token: *** and more", shown);
        Assert.All(sent, delta => Assert.DoesNotContain("daemon-secret", delta.Text));
    }

    [Fact]
    public void TheLogIsMasked()
    {
        var writer = new MaskingWriter(_log, _host.Masker);
        writer.WriteLine($"session 5: harness said {Secret}");
        Assert.Equal("session 5: harness said ***", _log.ToString().TrimEnd());
    }
}
