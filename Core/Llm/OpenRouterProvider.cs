using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace erp_inteligente.Core.Llm;

/// <summary>
/// OpenRouter chat-completions client (non-streaming): one key for many models, free ones included, used for demos and
/// tests. The host is fixed so the key can never be sent elsewhere. Every request asks to be routed only to providers that
/// support all its parameters (tools), and <c>reasoning_details</c> goes back unchanged on the following calls, as the
/// API requires to keep reasoning across tool calls.
/// </summary>
public sealed class OpenRouterProvider : ChatCompletionsProvider
{
    private static readonly Uri Url = new("https://openrouter.ai/api/v1/chat/completions");
    private static readonly Uri KeyUrl = new("https://openrouter.ai/api/v1/key");

    /// <param name="handler">HTTP transport (use <see cref="ChatCompletionsProvider.CreateDefaultHandler"/>); not disposed by the provider.</param>
    /// <param name="keySource">Returns a fresh key buffer per call; the provider zeroes it.</param>
    /// <param name="delay">Backoff delay function (injectable for tests).</param>
    /// <param name="timeout">Per-attempt timeout; 60 s by default.</param>
    public OpenRouterProvider(HttpMessageHandler handler, Func<byte[]?> keySource,
        Func<TimeSpan, CancellationToken, Task>? delay = null, TimeSpan? timeout = null)
        : base(handler, keySource ?? throw new ArgumentNullException(nameof(keySource)), delay, timeout)
    {
    }

    protected override Uri Endpoint => Url;

    protected override bool SendsReasoningDetailsBack => true;

    /// <summary>App name shown in the OpenRouter activity page; no data about the request.</summary>
    protected override void AddHeaders(HttpRequestMessage message) => message.Headers.Add("X-OpenRouter-Title", "Nexus");

    protected override void WriteExtraFields(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("provider");
        writer.WriteBoolean("require_parameters", true);
        writer.WriteEndObject();
    }

    /// <summary>Reads the key's limits (<c>GET /api/v1/key</c>): validates the key without spending tokens.</summary>
    public Task TestConnectionAsync(CancellationToken cancellationToken) => ProbeAsync(KeyUrl, cancellationToken);
}
