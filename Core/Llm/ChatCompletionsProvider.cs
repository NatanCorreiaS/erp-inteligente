using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace erp_inteligente.Core.Llm;

/// <summary>
/// Client of the "chat completions" wire format (non-streaming), shared by DeepSeek, OpenRouter and a local server that
/// speaks the same format. The endpoint is fixed by each subclass, so a key can never be sent elsewhere; errors carry only a kind
/// and a status code, never the key or the response body.
/// </summary>
public abstract class ChatCompletionsProvider : ILlmProvider
{
    private const int MaxRetries = 2;

    private readonly HttpClient _http;
    private readonly Func<byte[]?>? _keySource;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _timeout;

    /// <param name="keySource">Returns a fresh key buffer per call (zeroed after use); null for a server that needs no key.</param>
    protected ChatCompletionsProvider(HttpMessageHandler handler, Func<byte[]?>? keySource,
        Func<TimeSpan, CancellationToken, Task>? delay, TimeSpan? timeout)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _keySource = keySource;
        _http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        _delay = delay ?? Task.Delay;
        _timeout = timeout ?? TimeSpan.FromSeconds(60);
    }

    /// <summary>The fixed URL of the chat completions endpoint.</summary>
    protected abstract Uri Endpoint { get; }

    /// <summary>DeepSeek returns <c>reasoning_content</c> and requires it back when tools are in use.</summary>
    protected virtual bool SendsReasoningBack => false;

    /// <summary>Handler without redirects, so the Authorization header cannot follow a redirect elsewhere.</summary>
    public static HttpMessageHandler CreateDefaultHandler() => new SocketsHttpHandler { AllowAutoRedirect = false };

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return SendAsync(BuildBody(request, extra: null), cancellationToken);
    }

    /// <summary>Sends a body built by <see cref="BuildBody"/>; used by the connection tests of the subclasses.</summary>
    protected async Task<LlmResponse> SendAsync(byte[] body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (status, text) = await ExchangeAsync(HttpMethod.Post, Endpoint, body, ct).ConfigureAwait(false);
            if (text is not null)
            {
                // A 200 can still carry an error (OpenRouter: a top-level "error", or finish_reason "error").
                var (response, errorCode) = ParseResponse(text);
                if (response is not null) return response;
                status = errorCode;
            }
            if (await ShouldRetryAsync(status, attempt, ct).ConfigureAwait(false)) continue;
            throw ErrorFor(status);
        }
    }

    /// <summary>
    /// A GET with the key to a fixed URL of the same host, for connection tests that spend no tokens. The body is ignored;
    /// only the status matters.
    /// </summary>
    protected async Task ProbeAsync(Uri url, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var (status, text) = await ExchangeAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            if (text is not null) return;
            if (await ShouldRetryAsync(status, attempt, ct).ConfigureAwait(false)) continue;
            throw ErrorFor(status);
        }
    }

    /// <summary>Extra headers of a provider (never a secret other than the Authorization header set here).</summary>
    protected virtual void AddHeaders(HttpRequestMessage message)
    {
    }

    /// <summary>Provider-specific top-level fields written into every request body.</summary>
    protected virtual void WriteExtraFields(Utf8JsonWriter writer)
    {
    }

    /// <summary>OpenRouter returns <c>reasoning_details</c> and expects it back, unchanged, in the following calls.</summary>
    protected virtual bool SendsReasoningDetailsBack => false;

    /// <summary>One HTTP exchange: the status, and the body when the status is a success.</summary>
    private async Task<(int Status, string? Text)> ExchangeAsync(HttpMethod method, Uri url, byte[]? body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var message = new HttpRequestMessage(method, url);
        if (_keySource is not null)
        {
            var key = _keySource();
            if (key is null || key.Length == 0)
                throw new LlmException(LlmErrorKind.NotConfigured, "The LLM API key is not configured.");
            try { message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Encoding.UTF8.GetString(key)); }
            finally { Array.Clear(key); }
        }
        AddHeaders(message);
        if (body is not null)
        {
            message.Content = new ByteArrayContent(body);
            message.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);
        try
        {
            using var response = await _http.SendAsync(message, timeoutCts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            return response.IsSuccessStatusCode
                ? (status, await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false))
                : (status, null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new LlmException(LlmErrorKind.Timeout, "The LLM request timed out.");
        }
        catch (HttpRequestException)
        {
            throw new LlmException(LlmErrorKind.Unavailable, "The LLM service could not be reached.");
        }
    }

    /// <summary>Rate limits, timeouts and server errors are retried twice, after 1 s and 3 s.</summary>
    private async Task<bool> ShouldRetryAsync(int status, int attempt, CancellationToken ct)
    {
        if (!IsTransient(status) || attempt >= MaxRetries) return false;
        await _delay(TimeSpan.FromSeconds(attempt == 0 ? 1 : 3), ct).ConfigureAwait(false);
        return true;
    }

    private static bool IsTransient(int status) => status is 408 or 429 or >= 500;

    private static LlmException ErrorFor(int status) => status switch
    {
        401 or 403 => new(LlmErrorKind.Unauthorized, "The LLM provider rejected the credentials.", status),
        402 => new(LlmErrorKind.PaymentRequired, "The LLM account has no balance or credits left.", status),
        408 or 524 => new(LlmErrorKind.Timeout, "The LLM request timed out.", status),
        429 => new(LlmErrorKind.RateLimited, "The LLM provider is rate limiting requests.", status),
        >= 500 => new(LlmErrorKind.Unavailable, "The LLM service is unavailable.", status),
        _ => new(LlmErrorKind.InvalidResponse, "The LLM provider rejected the request.", status),
    };

    /// <param name="extra">Writes top-level fields of one call (e.g. DeepSeek's <c>thinking</c> in the connection test).</param>
    protected byte[] BuildBody(LlmRequest r, Action<Utf8JsonWriter>? extra)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("model", r.Model);
            w.WriteStartArray("messages");
            foreach (var m in r.Messages) WriteMessage(w, m);
            w.WriteEndArray();
            if (r.Tools.Count > 0)
            {
                w.WriteStartArray("tools");
                foreach (var t in r.Tools)
                {
                    w.WriteStartObject();
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", t.Name);
                    w.WriteString("description", t.Description);
                    w.WritePropertyName("parameters");
                    using var schema = JsonDocument.Parse(t.ParametersSchema);
                    schema.RootElement.WriteTo(w);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (r.NoToolCalls && r.Tools.Count > 0) w.WriteString("tool_choice", "none");
            w.WriteNumber("max_tokens", r.MaxTokens);
            if (r.Temperature is { } temp) w.WriteNumber("temperature", temp);
            WriteExtraFields(w);
            extra?.Invoke(w);
            w.WriteBoolean("stream", false);
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private void WriteMessage(Utf8JsonWriter w, LlmMessage m)
    {
        w.WriteStartObject();
        w.WriteString("role", m.Role switch
        {
            LlmRole.System => "system",
            LlmRole.User => "user",
            LlmRole.Assistant => "assistant",
            _ => "tool",
        });
        if (m.Content is not null) w.WriteString("content", m.Content);
        else if (m.Role != LlmRole.Assistant || m.ToolCalls.Count == 0) w.WriteString("content", string.Empty);
        else w.WriteNull("content");
        if (m.Role == LlmRole.Tool) w.WriteString("tool_call_id", m.ToolCallId);
        if (m.Role == LlmRole.Assistant)
        {
            if (SendsReasoningBack && m.ReasoningContent is not null) w.WriteString("reasoning_content", m.ReasoningContent);
            if (SendsReasoningDetailsBack && m.ReasoningDetailsJson is not null)
            {
                w.WritePropertyName("reasoning_details");
                using var details = JsonDocument.Parse(m.ReasoningDetailsJson);
                details.RootElement.WriteTo(w);
            }
            if (m.ToolCalls.Count > 0)
            {
                w.WriteStartArray("tool_calls");
                foreach (var c in m.ToolCalls)
                {
                    w.WriteStartObject();
                    w.WriteString("id", c.Id);
                    w.WriteString("type", "function");
                    w.WriteStartObject("function");
                    w.WriteString("name", c.Name);
                    w.WriteString("arguments", c.ArgumentsJson);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
        }
        w.WriteEndObject();
    }

    private static LlmException Invalid() => new(LlmErrorKind.InvalidResponse, "The LLM response was not in the expected format.");

    /// <summary>
    /// The response, or the error code it carries instead: a top-level <c>error</c> object or a choice that finished with
    /// <c>"error"</c> (OpenRouter reports provider failures this way, with HTTP 200). A missing code counts as 502.
    /// </summary>
    private static (LlmResponse? Response, int ErrorCode) ParseResponse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid();
            if (root.TryGetProperty("error", out var topError) && topError.ValueKind == JsonValueKind.Object)
                return (null, ErrorCode(topError));
            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                throw Invalid();
            var choice = choices[0];
            if (Str(choice, "finish_reason") == "error")
                return (null, choice.TryGetProperty("error", out var choiceError) && choiceError.ValueKind == JsonValueKind.Object
                    ? ErrorCode(choiceError)
                    : 502);
            if (!choice.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) throw Invalid();

            var calls = new List<LlmToolCall>();
            if (msg.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in tc.EnumerateArray())
                {
                    if (!c.TryGetProperty("function", out var fn)) throw Invalid();
                    var id = Str(c, "id");
                    var name = Str(fn, "name");
                    var args = Str(fn, "arguments");
                    if (id is null || name is null) throw Invalid();
                    calls.Add(new LlmToolCall(id, name, args ?? "{}"));
                }
            }

            var finish = Str(choice, "finish_reason") switch
            {
                "stop" => LlmFinishReason.Stop,
                "tool_calls" => LlmFinishReason.ToolCalls,
                "length" => LlmFinishReason.Length,
                "content_filter" => LlmFinishReason.ContentFilter,
                _ => LlmFinishReason.Other,
            };

            var usage = new LlmUsage(0, 0);
            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                // DeepSeek reports the cached part of the prompt as prompt_cache_hit_tokens; OpenRouter (the standard
                // format) as prompt_tokens_details.cached_tokens, with cache writes and the cost in US$ beside it.
                var prompt = Int(u, "prompt_tokens");
                var details = u.TryGetProperty("prompt_tokens_details", out var d) && d.ValueKind == JsonValueKind.Object ? d : default;
                var cacheRead = u.TryGetProperty("prompt_cache_hit_tokens", out _)
                    ? Int(u, "prompt_cache_hit_tokens")
                    : details.ValueKind == JsonValueKind.Object ? Int(details, "cached_tokens") : 0;
                var cacheWrite = details.ValueKind == JsonValueKind.Object ? Int(details, "cache_write_tokens") : 0;
                cacheRead = Math.Clamp(cacheRead, 0, prompt);
                usage = new LlmUsage(prompt, Int(u, "completion_tokens"))
                {
                    CacheReadTokens = cacheRead,
                    CacheWriteTokens = Math.Clamp(cacheWrite, 0, prompt - cacheRead),
                    Cost = u.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Number
                        && cost.TryGetDecimal(out var value) && value >= 0 ? value : null,
                };
            }

            var reasoningDetails = msg.TryGetProperty("reasoning_details", out var rd)
                && rd.ValueKind == JsonValueKind.Array && rd.GetArrayLength() > 0 ? rd.GetRawText() : null;
            var reasoning = Str(msg, "reasoning_content") ?? Str(msg, "reasoning");
            return (new LlmResponse(Str(msg, "content"), calls, reasoning, finish, usage) { ReasoningDetailsJson = reasoningDetails }, 0);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw Invalid();
        }
    }

    /// <summary>The HTTP-like code of an error object; anything unusable counts as a provider failure (502).</summary>
    private static int ErrorCode(JsonElement error) =>
        error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out var n)
            && n is >= 400 and <= 599 ? n : 502;

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
}
