using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using BoSoFa.Daemon;
using BoSoFa.Daemon.Sessions;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Tests;

public class TerminalTest
{
    static readonly string Multiply = OperatingSystem.IsWindows() ? "set /a 1234*2\r" : "echo $((1234*2))\n";

    static async Task Send(WebSocket socket, LiveClientMessage message) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, ProtocolJsonContext.Default.LiveClientMessage),
            WebSocketMessageType.Text, true, default);

    static string Output(ConcurrentQueue<LiveServerMessage> live, string terminalId) =>
        Encoding.UTF8.GetString(live.ToArray().SelectMany(message => message switch
        {
            LiveTerminalSnapshot snapshot when snapshot.TerminalId == terminalId => Convert.FromBase64String(snapshot.Data),
            LiveTerminalOutput output when output.TerminalId == terminalId => Convert.FromBase64String(output.Data),
            _ => []
        }).ToArray());

    static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task PeopleWatchAndTypeIntoAShellThatLivesOnTheMachine()
    {
        await using var server = await TestServer.StartAsync();
        var cookies = new CookieContainer();
        using var boris = server.Client(cookies);
        (await boris.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("boris@example.com", null))).EnsureSuccessStatusCode();
        var annaCookies = new CookieContainer();
        using var anna = server.Client(annaCookies);
        (await anna.PostAsJsonAsync("/auth/dev-login", new DevLoginRequest("anna@example.com", null))).EnsureSuccessStatusCode();
        var dataDirectory = Path.Combine(server.DataDirectory, "daemon");
        var state = await SessionTest.EnrollAsync(server.BaseAddress, boris, dataDirectory);
        var work = Directory.CreateDirectory(Path.Combine(server.DataDirectory, "work")).FullName;
        using var key = new DaemonStore(dataDirectory).LoadOrCreateKey();
        using var outbox = new Outbox(Path.Combine(dataDirectory, "outbox"));
        await using var host = new SessionHost(dataDirectory, outbox, new(), TextWriter.Null);
        using var stop = new CancellationTokenSource();
        var link = new DaemonLink(state, key, TextWriter.Null, outbox, host);
        var connected = new TaskCompletionSource();
        link.Connected += _ => connected.TrySetResult();
        var running = link.RunAsync(stop.Token);
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var terminals = $"/api/v1/machines/{state.MachineId}/terminals";

        Assert.Equal(HttpStatusCode.Forbidden, (await anna.GetAsync(terminals)).StatusCode);
        await Eventually(async () => (await boris.GetAsync(terminals)).StatusCode == HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.BadRequest, (await boris.PostAsJsonAsync(terminals, new TerminalOpenRequest("/no/such/folder", 80, 24))).StatusCode);
        var opened = await boris.PostAsJsonAsync(terminals, new TerminalOpenRequest(work, 80, 24));
        Assert.True(opened.IsSuccessStatusCode, await opened.Content.ReadAsStringAsync());
        var terminal = (await opened.Content.ReadFromJsonAsync<TerminalOpenResult>())!.Terminal;
        Assert.False(terminal.Exited);
        // Listed across machines too, with when it last printed; only to the person who controls the machine.
        var everywhere = Assert.Single((await boris.GetFromJsonAsync<MachineTerminalDto[]>("/api/v1/terminals"))!);
        Assert.Equal((state.MachineId, terminal.Id), (everywhere.MachineId, everywhere.Terminal.Id));
        Assert.NotNull(everywhere.Terminal.LastActivityAt);
        Assert.Empty((await anna.GetFromJsonAsync<MachineTerminalDto[]>("/api/v1/terminals"))!);

        async Task<(ClientWebSocket Socket, ConcurrentQueue<LiveServerMessage> Live, Task Listening)> WatchAsync(CookieContainer jar)
        {
            var socket = new ClientWebSocket();
            socket.Options.Cookies = jar;
            await socket.ConnectAsync(new UriBuilder(server.BaseAddress) { Scheme = "ws", Path = LiveSocket.Path }.Uri, default);
            var live = new ConcurrentQueue<LiveServerMessage>();
            var listening = SessionTest.ListenAsync(socket, live);
            await Send(socket, new LiveTerminalSubscribe(state.MachineId, terminal.Id));
            return (socket, live, listening);
        }

        // Someone else's connection neither sees nor reaches it.
        var stranger = await WatchAsync(annaCookies);
        await Eventually(() => Task.FromResult(stranger.Live.Any(message => message is LiveTerminalExited { Message: not null })));
        await Send(stranger.Socket, new LiveTerminalInput(terminal.Id, Base64("exit\n")));

        var first = await WatchAsync(cookies);
        await Eventually(() => Task.FromResult(first.Live.Any(message => message is LiveTerminalSnapshot)));
        await Send(first.Socket, new LiveTerminalResize(terminal.Id, 100, 30));
        // A login shell that is still starting may discard what was typed, so type again until it answers.
        await Eventually(async () =>
        {
            await Send(first.Socket, new LiveTerminalInput(terminal.Id, Base64(Multiply)));
            await Task.Delay(1000);
            return Output(first.Live, terminal.Id).Contains("2468");
        });

        // Somebody who attaches later gets the history; a shell's output needs no one watching.
        first.Socket.Abort();
        var second = await WatchAsync(cookies);
        await Eventually(() => Task.FromResult(second.Live.OfType<LiveTerminalSnapshot>().Any(snapshot =>
            Encoding.UTF8.GetString(Convert.FromBase64String(snapshot.Data)).Contains("2468"))));

        await Send(second.Socket, new LiveTerminalInput(terminal.Id, Base64(OperatingSystem.IsWindows() ? "exit 3\r" : "exit 3\n")));
        await Eventually(() => Task.FromResult(second.Live.Any(message => message is LiveTerminalExited { ExitCode: 3 })));
        var listed = await boris.GetFromJsonAsync<TerminalListResult>(terminals);
        Assert.Equal((true, 3), (listed!.Terminals.Single().Exited, listed.Terminals.Single().ExitCode));
        Assert.Equal(HttpStatusCode.OK, (await boris.DeleteAsync($"{terminals}/{terminal.Id}")).StatusCode);
        Assert.Empty((await boris.GetFromJsonAsync<TerminalListResult>(terminals))!.Terminals);

        // Closing a running shell ends it.
        var another = (await (await boris.PostAsJsonAsync(terminals, new TerminalOpenRequest(work, 80, 24)))
            .Content.ReadFromJsonAsync<TerminalOpenResult>())!.Terminal;
        Assert.Equal(HttpStatusCode.OK, (await boris.DeleteAsync($"{terminals}/{another.Id}")).StatusCode);

        second.Socket.Abort();
        stranger.Socket.Abort();
        await stop.CancelAsync();
        try { await running; }
        catch (OperationCanceledException) { }
    }

    static async Task Eventually(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out.");
            await Task.Delay(50);
        }
    }
}
