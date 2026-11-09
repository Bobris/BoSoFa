namespace BoSoFa.Server.Agents;

/// <summary>
/// Makes a call again after a failure that may pass: 1, 2, 4, 8 and 15 seconds apart, or as long as the provider asks
/// (at most a minute). A tool call is never run twice by this: tools run only after a whole response, so a call that broke
/// mid-stream has started nothing. What it streamed is dropped with a <see cref="TurnDeltaKind.Restart"/> delta.
/// </summary>
public sealed class RetryingTurnExecutor(IModelTurnExecutor inner, TimeProvider time) : IModelTurnExecutor
{
    static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(15)];

    static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(1);

    public string Api => inner.Api;
    public string Endpoint => inner.Endpoint;

    public async Task<TurnResponse> ExecuteAsync(TurnRequest request, Func<TurnDelta, ValueTask> onDelta, CancellationToken cancellation)
    {
        for (var attempt = 0; ; attempt++)
        {
            var streamed = false;
            try
            {
                return await inner.ExecuteAsync(request, delta =>
                {
                    streamed = true;
                    return onDelta(delta);
                }, cancellation);
            }
            catch (ModelProviderException exception) when (exception.IsRetryable && attempt < Backoff.Length)
            {
                var wait = exception.RetryAfter is { } asked ? asked < LongestWait ? asked : LongestWait : Backoff[attempt];
                if (streamed) await onDelta(new(TurnDeltaKind.Restart, ""));
                await Task.Delay(wait, time, cancellation);
            }
        }
    }
}
