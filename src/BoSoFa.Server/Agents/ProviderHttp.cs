using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoSoFa.Protocol;

namespace BoSoFa.Server.Agents;

/// <summary>What every provider client shares: our own User-Agent (OpenCode Go asks clients to name themselves), sending,
/// and turning an error answer into a <see cref="ModelProviderException"/> the loop can act on.</summary>
static class ProviderHttp
{
    public static string UserAgent { get; } = $"{Brand.Name}/{Api.Version.Split('+')[0]}";

    /// <summary>Sends the request and returns a successful response with its body unread (it streams).</summary>
    public static async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request, string provider,
        CancellationToken cancellation)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation); }
        catch (HttpRequestException exception)
        {
            throw new ModelProviderException(ProviderFailure.Transient, $"{provider} could not be reached: {exception.Message}");
        }
        catch (TaskCanceledException) when (!cancellation.IsCancellationRequested)
        {
            throw new ModelProviderException(ProviderFailure.Transient, $"{provider} did not answer in time.");
        }
        if (response.IsSuccessStatusCode) return response;
        using (response) throw await ErrorOfAsync(response, provider, cancellation);
    }

    static async Task<ModelProviderException> ErrorOfAsync(HttpResponseMessage response, string provider, CancellationToken cancellation)
    {
        var status = (int)response.StatusCode;
        string body;
        try { body = await response.Content.ReadAsStringAsync(cancellation); }
        catch (HttpRequestException) { body = ""; }
        var (code, message) = ErrorFields(body);
        message ??= body.Length is > 0 and <= 500 ? body : response.ReasonPhrase ?? "no details";
        return new(Classify(status, code, message), $"{provider} answered {status}: {message}", RetryAfterOf(response.Headers), status);
    }

    /// <summary>The code and message of an error body: OpenAI's <c>{"error":{"code","type","message"}}</c> or Anthropic's
    /// <c>{"type":"error","error":{"type","message"}}</c>.</summary>
    public static (string? Code, string? Message) ErrorFields(string body)
    {
        try
        {
            var error = JsonNode.Parse(body)?["error"];
            if (error is JsonObject fields)
                return (Text(fields["code"]) ?? Text(fields["type"]), Text(fields["message"]));
            if (error is JsonValue value && value.TryGetValue<string>(out var text)) return (null, text);
        }
        catch (JsonException) { }
        return (null, null);
    }

    static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>What the loop should do about a failure, from its HTTP status (none inside a stream), code and message.</summary>
    public static ProviderFailure Classify(int? status, string? code, string message)
    {
        var lower = message.ToLowerInvariant();
        if (code is "context_length_exceeded" || lower.Contains("context window") || lower.Contains("context length") ||
            lower.Contains("prompt is too long") || lower.Contains("maximum context"))
            return ProviderFailure.ContextOverflow;
        if (code is "insufficient_quota" or "billing_error" || lower.Contains("credit balance") || lower.Contains("exceeded your current quota"))
            return ProviderFailure.QuotaExceeded;
        if (status is 401 or 403 || code is "invalid_api_key" or "authentication_error" or "permission_error")
            return ProviderFailure.Authentication;
        if (status is 429 || code is "rate_limit_exceeded" or "rate_limit_error")
            return ProviderFailure.RateLimited;
        if (status is 408 or 409 or >= 500 || code is "server_error" or "overloaded_error" or "api_error" or "internal_error")
            return ProviderFailure.Transient;
        return status == null ? ProviderFailure.Transient : ProviderFailure.Invalid;
    }

    /// <summary>How long the provider asks us to wait: <c>retry-after-ms</c>, or <c>Retry-After</c> in seconds or as a date.</summary>
    static TimeSpan? RetryAfterOf(HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("retry-after-ms", out var values) && double.TryParse(values.FirstOrDefault(),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var milliseconds))
            return TimeSpan.FromMilliseconds(milliseconds);
        if (headers.RetryAfter?.Delta is { } delta) return delta;
        if (headers.RetryAfter?.Date is { } date) return date - DateTimeOffset.UtcNow is { Ticks: > 0 } wait ? wait : TimeSpan.Zero;
        return null;
    }

    /// <summary>A JSON object of the conversation: what the provider gave, without the fields a replay must not send.</summary>
    public static JsonObject? ParseObject(string? json)
    {
        if (json == null) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
