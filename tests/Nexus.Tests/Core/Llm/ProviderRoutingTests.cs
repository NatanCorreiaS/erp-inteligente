using System.Net;
using System.Text;
using System.Text.Json;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Security;

namespace Nexus.Tests.Core.Llm;

/// <summary>Feature 8: the DeepSeek and local model adapters, the router and the model per role.</summary>
public sealed class ProviderRoutingTests
{
    private const string Key = "sk-test-MULTI-1234567890";

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

    private static readonly LlmTool Tool = new("get_stock", "Saldo por SKU.",
        """{"type":"object","additionalProperties":false,"required":["sku"],"properties":{"sku":{"type":"string","pattern":"^[A-Z]{3}-[0-9]{3}$"}}}""");

    // ---- DeepSeek cache and the local model -----------------------------------------------------------------

    private const string ChatOk = """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":3,"completion_tokens":1}}""";

    [Fact]
    public async Task DeepSeek_reports_the_part_of_the_prompt_read_from_its_cache()
    {
        const string cached = """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2340,"completion_tokens":5,"prompt_cache_hit_tokens":2000,"prompt_cache_miss_tokens":340}}""";
        const string broken = """{"choices":[{"index":0,"message":{"role":"assistant","content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":5,"prompt_cache_hit_tokens":999}}""";
        var handler = new FakeHandler((_, n) => Json(HttpStatusCode.OK, n == 1 ? cached : n == 2 ? broken : ChatOk));
        var deepSeek = new DeepSeekProvider(handler, () => Encoding.UTF8.GetBytes(Key));
        var request = new LlmRequest(LlmSettings.StrongModel, [LlmMessage.System("Você é o Nexus."), LlmMessage.User("oi")], [Tool], 1024);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal(new LlmUsage(2340, 5) { CacheReadTokens = 2000 }, (await deepSeek.CompleteAsync(request, ct)).Usage);
        // A cached part larger than the prompt is clamped to it; without the field there is no cached part.
        Assert.Equal(new LlmUsage(10, 5) { CacheReadTokens = 10 }, (await deepSeek.CompleteAsync(request, ct)).Usage);
        Assert.Equal(new LlmUsage(3, 1), (await deepSeek.CompleteAsync(request, ct)).Usage);
        Assert.Equal("https://api.deepseek.com/chat/completions", handler.Calls[0].Request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Local_model_talks_only_to_loopback_without_a_key()
    {
        var handler = new FakeHandler((_, _) => Json(HttpStatusCode.OK, ChatOk));
        var local = new LocalProvider(handler, () => 8091);
        await local.CompleteAsync(new LlmRequest(LlmSettings.LocalModel, [LlmMessage.User("oi")], [], 50), TestContext.Current.CancellationToken);

        var request = handler.Calls[0].Request;
        Assert.Equal("http://127.0.0.1:8091/v1/chat/completions", request.RequestUri!.ToString());
        Assert.Null(request.Headers.Authorization);

        var badPort = new LocalProvider(handler, () => 80);
        var e = await Assert.ThrowsAsync<LlmException>(() => badPort.CompleteAsync(
            new LlmRequest(LlmSettings.LocalModel, [LlmMessage.User("oi")], [], 5), TestContext.Current.CancellationToken));
        Assert.Equal(LlmErrorKind.NotConfigured, e.Kind);
    }

    // ---- router and settings -------------------------------------------------------------------------------

    private sealed class Recorder(string name) : ILlmProvider
    {
        public List<LlmRequest> Seen { get; } = [];

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(new LlmResponse(name, [], null, LlmFinishReason.Stop, new LlmUsage(0, 0)));
        }
    }

    [Fact]
    public async Task Router_sends_each_request_to_its_provider_and_defaults_to_deepseek()
    {
        var deepSeek = new Recorder("deepseek");
        var local = new Recorder("local");
        var router = new LlmRouter(new Dictionary<LlmProviderKind, ILlmProvider>
        {
            [LlmProviderKind.DeepSeek] = deepSeek,
            [LlmProviderKind.Local] = local,
        });
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("local", (await router.CompleteAsync(new LlmRequest("m", [], [], 1, Provider: LlmProviderKind.Local), ct)).Content);
        Assert.Equal("deepseek", (await router.CompleteAsync(new LlmRequest("m", [], [], 1), ct)).Content);
        var onlyDeepSeek = new LlmRouter(new Dictionary<LlmProviderKind, ILlmProvider> { [LlmProviderKind.DeepSeek] = deepSeek });
        var e = await Assert.ThrowsAsync<LlmException>(() => onlyDeepSeek.CompleteAsync(new LlmRequest("m", [], [], 1, Provider: LlmProviderKind.Local), ct));
        Assert.Equal(LlmErrorKind.NotConfigured, e.Kind);
    }

    [Fact]
    public void Settings_keep_the_deepseek_key_and_one_model_per_role()
    {
        var store = new InMemorySecretStore();
        var settings = new LlmSettings(store, "db1");

        Assert.Equal(new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.StrongModel), settings.ChoiceFor(LlmUseRole.Planner));
        Assert.Equal(new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.FastModel), settings.ChoiceFor(LlmUseRole.Matcher));

        Assert.False(settings.IsConfigured);
        Assert.True(settings.IsConfiguredFor(LlmProviderKind.Local)); // the local server needs no key
        settings.SetKey(LlmProviderKind.DeepSeek, "sk-test-12345678");
        Assert.True(settings.IsConfiguredFor(LlmProviderKind.DeepSeek));
        Assert.NotNull(store.Get("db1", LlmSettings.KeyEntry));

        settings.SetChoice(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.FastModel));
        settings.SetChoice(LlmUseRole.Matcher, new LlmChoice(LlmProviderKind.Local, LlmSettings.LocalModel));
        Assert.Equal(new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.FastModel), settings.ChoiceFor(LlmUseRole.Planner));
        Assert.Equal(LlmProviderKind.Local, settings.ChoiceFor(LlmUseRole.Matcher).Provider);

        // The local model never plans; a model of another provider is refused; garbage and providers that no longer
        // exist fall back to the default.
        Assert.Throws<ArgumentException>(() => settings.SetChoice(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.Local, LlmSettings.LocalModel)));
        Assert.Throws<ArgumentException>(() => settings.SetChoice(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.LocalModel)));
        Assert.Throws<ArgumentException>(() => settings.SetKey(LlmProviderKind.Local, "whatever-key"));
        foreach (var stored in new[] { "deepseek:modelo-estranho", "outro:modelo", "2:deepseek-flash", "sem-dois-pontos" })
        {
            store.Set("db1", "llm.role.planner", Encoding.UTF8.GetBytes(stored));
            Assert.Equal(LlmSettings.FastModel, settings.ChoiceFor(LlmUseRole.Planner).Model); // the legacy DeepSeek entry
            Assert.Equal(LlmProviderKind.DeepSeek, settings.ChoiceFor(LlmUseRole.Planner).Provider);
        }

        Assert.Equal(LlmSettings.DefaultLocalPort, settings.LocalPort);
        settings.LocalPort = 9000;
        Assert.Equal(9000, settings.LocalPort);
        Assert.Throws<ArgumentException>(() => settings.LocalPort = 22);
    }

    [Fact]
    public void A_deepseek_planner_choice_keeps_the_legacy_model_entry_in_step()
    {
        var settings = new LlmSettings(new InMemorySecretStore(), "db1");
        settings.SetChoice(LlmUseRole.Planner, new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.FastModel));
        Assert.Equal(LlmSettings.FastModel, settings.Model);
    }
}
