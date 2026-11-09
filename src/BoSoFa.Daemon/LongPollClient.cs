using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using BoSoFa.Protocol;

namespace BoSoFa.Daemon;

/// <summary>The daemon's end of the long-poll transport (plan §5.3): one request waiting for the server's messages, and
/// one short request for each message going the other way.</summary>
public sealed class LongPollClient : IDaemonChannel, IDisposable
{
    static readonly TimeSpan NotFoundGrace = TimeSpan.FromSeconds(5);

    readonly HttpClient _http;
    readonly Func<CancellationToken, Task<string>> _token;
    readonly string _id;
    readonly Queue<DaemonMessage> _received = new();
    readonly CancellationTokenSource _broken = new();
    readonly DateTime _retryWebSocketAt;
    long _after;

    LongPollClient(HttpClient http, Func<CancellationToken, Task<string>> token, string id, TimeSpan lifetime)
    {
        _http = http;
        _token = token;
        _id = id;
        _retryWebSocketAt = DateTime.UtcNow + lifetime;
    }

    public string? CloseReason { get; private set; }

    /// <param name="lifetime">After this the channel reports itself closed, so the link tries a WebSocket again.</param>
    public static async Task<LongPollClient> OpenAsync(Uri server, HelloMessage hello, Func<CancellationToken, Task<string>> token, TimeSpan lifetime,
        CancellationToken cancellation)
    {
        var http = new HttpClient { BaseAddress = server, Timeout = DaemonPoll.Wait + TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{Brand.Daemon}/{Cli.Version.Split('+')[0]}");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, DaemonPoll.Path) { Content = JsonContent.Create(hello, ProtocolJsonContext.Default.HelloMessage) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token(cancellation));
            using var response = await http.SendAsync(request, cancellation);
            if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("The server no longer accepts this machine.");
            if (!response.IsSuccessStatusCode)
                throw new WebSocketException(WebSocketError.Faulted, await response.Content.ReadAsStringAsync(cancellation));
            var opened = await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.PollOpened, cancellation);
            return new(http, token, opened!.ConnectionId, lifetime);
        }
        catch
        {
            http.Dispose();
            throw;
        }
    }

    public async Task<DaemonMessage?> ReceiveAsync(CancellationToken cancellation)
    {
        using var both = CancellationTokenSource.CreateLinkedTokenSource(cancellation, _broken.Token);
        var notFoundSince = default(DateTime?);
        while (_received.Count == 0)
        {
            if (DateTime.UtcNow >= _retryWebSocketAt) return null;
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{DaemonPoll.Path}/{_id}/receive?after={_after}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(both.Token));
            HttpResponseMessage response;
            try { response = await _http.SendAsync(request, both.Token); }
            catch (OperationCanceledException) when (_broken.IsCancellationRequested) { throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "Sending failed."); }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { continue; } // the client's own timeout
            using (response)
            {
                if (response.StatusCode == HttpStatusCode.Gone)
                {
                    CloseReason = await response.Content.ReadAsStringAsync(cancellation);
                    return null;
                }
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    // A node that did not hear of this channel yet, or a channel that is gone for good.
                    notFoundSince ??= DateTime.UtcNow;
                    if (DateTime.UtcNow - notFoundSince > NotFoundGrace) return null;
                    await Task.Delay(TimeSpan.FromSeconds(1), both.Token);
                    continue;
                }
                notFoundSince = null;
                response.EnsureSuccessStatusCode();
                var batch = await response.Content.ReadFromJsonAsync(ProtocolJsonContext.Default.PollBatch, both.Token);
                _after = batch!.LastSequence;
                foreach (var message in batch.Messages) _received.Enqueue(message);
            }
        }
        return _received.Dequeue();
    }

    public async Task SendAsync(DaemonMessage message, CancellationToken cancellation)
    {
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{DaemonPoll.Path}/{_id}/send")
                {
                    Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes<DaemonMessage[]>([message], ProtocolJsonContext.Default.DaemonMessageArray))
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _token(cancellation));
                using var response = await _http.SendAsync(request, cancellation);
                if (response.StatusCode == HttpStatusCode.NotFound && attempt < 5)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellation);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                return;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellation.IsCancellationRequested)
        {
            // The workers of a connection only send; the receive loop is what notices a broken connection.
            await _broken.CancelAsync();
            throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, exception.Message);
        }
    }

    public Task CloseAsync(WebSocketCloseStatus status, string reason, CancellationToken cancellation) => Task.CompletedTask;

    public void Abort() => _broken.Cancel();

    public void Dispose()
    {
        _http.Dispose();
        _broken.Dispose();
    }
}
