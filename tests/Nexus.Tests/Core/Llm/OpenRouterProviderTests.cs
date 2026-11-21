using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using erp_inteligente.Core.Llm;
using Xunit;

namespace Nexus.Tests.Core.Llm;

/// <summary>The OpenRouter adapter against its documented wire format; a fake handler, never the real API.</summary>
public class OpenRouterProviderTests
{
    // Built at run time: a literal in the real key format would trip secret scanners, and this one is fake anyway.
    private static readonly string Key = "sk-or-v1-" + string.Concat(System.Linq.Enumerable.Repeat("fake", 16));
    private const string Model = "google/gemma-4-31b-it:free";

    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request, Calls.Count);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string body) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (OpenRouterProvider, FakeHandler, List<TimeSpan>) Make(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var delays = new List<TimeSpan>();
        var provider = new OpenRouterProvider(handler, () => Encoding.UTF8.GetBytes(Key),
            (d, _) => { delays.Add(d); return Task.CompletedTask; });
        return (provider, handler, delays);
    }

    private static LlmRequest Req(params LlmMessage[] messages) =>
        new(Model, messages.Length > 0 ? messages : [LlmMessage.User("oi")], [], 500, Provider: LlmProviderKind.OpenRouter);

    private const string Details = "[{\"type\":\"reasoning.text\",\"text\":\"pensando\",\"signature\":\"sig\",\"format\":\"unknown\",\"index\":0}]";

    private static readonly string ToolCallResponse =
        "{\"id\":\"gen-1\",\"object\":\"chat.completion\",\"choices\":[{\"index\":0,\"finish_reason\":\"tool_calls\",\"native_finish_reason\":\"tool_calls\"," +
        "\"message\":{\"role\":\"assistant\",\"content\":null,\"reasoning\":\"preciso do estoque\",\"reasoning_details\":" + Details + "," +
        "\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"get_stock\",\"arguments\":\"{\\\"sku\\\":\\\"ABC-123\\\"}\"}}]}}]," +
        "\"usage\":{\"prompt_tokens\":1200,\"completion_tokens\":30,\"total_tokens\":1230," +
        "\"prompt_tokens_details\":{\"cached_tokens\":1000,\"cache_write_tokens\":0},\"completion_tokens_details\":{\"reasoning_tokens\":12}," +
        "\"cost\":0.000123,\"is_byok\":false}}";

    [Fact]
    public async Task Request_goes_to_the_fixed_host_with_key_title_and_tool_routing()
    {
        var (p, h, _) = Make((_, _) => Json(HttpStatusCode.OK, ToolCallResponse));
        await p.CompleteAsync(Req(), CancellationToken.None);

        var (req, body) = Assert.Single(h.Calls);
        Assert.Equal("https://openrouter.ai/api/v1/chat/completions", req.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal(Key, req.Headers.Authorization!.Parameter);
        Assert.Equal("Nexus", Assert.Single(req.Headers.GetValues("X-OpenRouter-Title")));
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(Model, doc.RootElement.GetProperty("model").GetString());
        Assert.True(doc.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("tool_choice", out _));
    }

    [Fact]
    public async Task Response_reads_tool_calls_reasoning_cache_and_cost()
    {
        var (p, _, _) = Make((_, _) => Json(HttpStatusCode.OK, ToolCallResponse));
        var r = await p.CompleteAsync(Req(), CancellationToken.None);

        Assert.Equal(LlmFinishReason.ToolCalls, r.FinishReason);
        var call = Assert.Single(r.ToolCalls);
        Assert.Equal(("call_1", "get_stock"), (call.Id, call.Name));
        Assert.Equal("preciso do estoque", r.ReasoningContent);
        Assert.NotNull(r.ReasoningDetailsJson);
        Assert.Equal(1200, r.Usage.PromptTokens);
        Assert.Equal(1000, r.Usage.CacheReadTokens);
        Assert.Equal(0.000123m, r.Usage.Cost);
    }

    [Fact]
    public async Task Reasoning_details_go_back_unchanged_on_the_next_call()
    {
        var (p, h, _) = Make((_, n) => Json(HttpStatusCode.OK, n == 1
            ? ToolCallResponse
            : "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"10 cx\"}}]}"));
        var first = await p.CompleteAsync(Req(), CancellationToken.None);
        await p.CompleteAsync(Req(LlmMessage.User("oi"), LlmMessage.Assistant(first), LlmMessage.ToolResult("call_1", "{\"qty\":10}")),
            CancellationToken.None);

        using var doc = JsonDocument.Parse(h.Calls[1].Body);
        var assistant = doc.RootElement.GetProperty("messages")[1];
        Assert.Equal("assistant", assistant.GetProperty("role").GetString());
        using var expected = JsonDocument.Parse(Details);
        Assert.Equal(expected.RootElement.GetRawText(), assistant.GetProperty("reasoning_details").GetRawText());
        // reasoning_content is DeepSeek's field; OpenRouter gets the details instead.
        Assert.False(assistant.TryGetProperty("reasoning_content", out _));
    }

    [Fact]
    public async Task No_tool_calls_keeps_the_tools_and_sets_tool_choice_none()
    {
        var (p, h, _) = Make((_, _) => Json(HttpStatusCode.OK,
            "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"fim\"}}]}"));
        var tool = new LlmTool("get_stock", "Reads stock", "{\"type\":\"object\"}");
        await p.CompleteAsync(new LlmRequest(Model, [LlmMessage.User("oi")], [tool], 100, NoToolCalls: true), CancellationToken.None);

        using var doc = JsonDocument.Parse(Assert.Single(h.Calls).Body);
        Assert.Equal(1, doc.RootElement.GetProperty("tools").GetArrayLength());
        Assert.Equal("none", doc.RootElement.GetProperty("tool_choice").GetString());
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":502,\"message\":\"upstream down\"}}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"error\",\"message\":{\"role\":\"assistant\",\"content\":\"meia resp\"},\"error\":{\"code\":503,\"message\":\"x\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"error\",\"message\":{\"role\":\"assistant\",\"content\":\"meia resp\"}}]}")]
    public async Task An_error_inside_a_200_is_retried_and_never_taken_as_an_answer(string body)
    {
        var (p, h, delays) = Make((_, _) => Json(HttpStatusCode.OK, body));
        var e = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.Unavailable, e.Kind);
        Assert.Equal(3, h.Calls.Count);
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public async Task A_rate_limit_inside_a_200_recovers_on_retry()
    {
        var (p, h, _) = Make((_, n) => Json(HttpStatusCode.OK, n == 1
            ? "{\"error\":{\"code\":429,\"message\":\"slow down\"}}"
            : "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}"));
        var r = await p.CompleteAsync(Req(), CancellationToken.None);
        Assert.Equal("ok", r.Content);
        Assert.Equal(2, h.Calls.Count);
    }

    [Theory]
    [InlineData(402, LlmErrorKind.PaymentRequired, 1)]
    [InlineData(401, LlmErrorKind.Unauthorized, 1)]
    [InlineData(408, LlmErrorKind.Timeout, 3)]
    [InlineData(524, LlmErrorKind.Timeout, 3)]
    [InlineData(503, LlmErrorKind.Unavailable, 3)]
    [InlineData(400, LlmErrorKind.InvalidResponse, 1)]
    public async Task Http_errors_map_to_kinds_without_leaking_the_key(int status, LlmErrorKind kind, int calls)
    {
        var (p, h, _) = Make((_, _) => Json((HttpStatusCode)status, "{\"error\":{\"code\":" + status + ",\"message\":\"" + Key + "\"}}"));
        var e = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(kind, e.Kind);
        Assert.Equal(status, e.StatusCode);
        Assert.Equal(calls, h.Calls.Count);
        Assert.DoesNotContain(Key, e.ToString());
    }

    [Fact]
    public async Task A_redirect_is_not_followed()
    {
        var (p, h, _) = Make((_, _) =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            r.Headers.Location = new Uri("https://example.com/steal");
            return r;
        });
        var e = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.InvalidResponse, e.Kind);
        Assert.Single(h.Calls);
    }

    [Fact]
    public async Task Connection_test_reads_the_key_endpoint_without_spending_tokens()
    {
        var (p, h, _) = Make((_, _) => Json(HttpStatusCode.OK, "{\"data\":{\"label\":\"x\",\"limit\":null,\"is_free_tier\":true}}"));
        await p.TestConnectionAsync(CancellationToken.None);

        var (req, body) = Assert.Single(h.Calls);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal("https://openrouter.ai/api/v1/key", req.RequestUri!.ToString());
        Assert.Equal(Key, req.Headers.Authorization!.Parameter);
        Assert.Equal("", body);
    }

    [Fact]
    public async Task Connection_test_reports_a_rejected_key()
    {
        var (p, _, _) = Make((_, _) => Json(HttpStatusCode.Unauthorized, "{\"error\":{\"code\":401,\"message\":\"no\"}}"));
        var e = await Assert.ThrowsAsync<LlmException>(() => p.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(LlmErrorKind.Unauthorized, e.Kind);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, ToolCallResponse));
        var p = new OpenRouterProvider(handler, () => null);
        var e = await Assert.ThrowsAsync<LlmException>(() => p.CompleteAsync(Req(), CancellationToken.None));
        Assert.Equal(LlmErrorKind.NotConfigured, e.Kind);
        Assert.Empty(handler.Calls);
    }
}
