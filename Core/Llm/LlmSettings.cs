using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using erp_inteligente.Core.Security;

namespace erp_inteligente.Core.Llm;

/// <summary>
/// Provider keys, the model of each role and the local model port, kept in the OS key store (never in SQLite).
/// The members without a provider argument are DeepSeek's, as before feature 8.
/// </summary>
public sealed partial class LlmSettings(ISecretStore store, string databaseId)
{
    public const string KeyEntry = "llm.deepseek.key";
    public const string OpenRouterKeyEntry = "llm.openrouter.key";
    public const string ModelEntry = "llm.deepseek.model";
    public const string StrongModel = "deepseek-v4-pro";
    /// <summary>The API id of DeepSeek's Flash model (served by DeepSeek-V4.1-Flash; the id carries no version).</summary>
    public const string FastModel = "deepseek-flash";

    /// <summary>The planning model when the administrator has not chosen one (decision of 2026-11-14).</summary>
    public const string DefaultPlannerModel = FastModel;
    public const string LocalModel = "nexus-matcher";
    public const int DefaultLocalPort = 8080;
    public const int MinKeyLength = 8;
    public const int MaxKeyLength = 200;

    private const string LocalPortEntry = "llm.local.port";
    private const string EmbeddingEntry = "llm.embedding";

    /// <summary>Alias of the embeddings model served on 127.0.0.1 by tools/finetune/serve_local.py.</summary>
    public const string LocalEmbeddingModel = "nexus-embed";

    /// <summary>Providers that can embed text: DeepSeek has no embeddings API, so only the local server does.</summary>
    public static IReadOnlyList<LlmProviderKind> EmbeddingProviders { get; } = [LlmProviderKind.Local];

    public static string EmbeddingModelFor(LlmProviderKind provider) => provider switch
    {
        LlmProviderKind.Local => LocalEmbeddingModel,
        _ => throw new ArgumentException("This provider has no embeddings.", nameof(provider)),
    };

    /// <summary>The id stored with each vector: vectors of another id are never compared.</summary>
    public static string EmbeddingModelId(LlmChoice choice) => Format(choice);

    /// <summary>
    /// Cosine thresholds of the embeddings model. The local model (paraphrase-multilingual-MiniLM-L12-v2) was measured on the
    /// 22 documents of knowledge/.
    /// </summary>
    public static Knowledge.EmbeddingThresholds EmbeddingThresholdsFor(LlmChoice choice) =>
        new(Search: 0.40, Context: 0.55);

    public static IReadOnlyList<string> SupportedModels { get; } = [StrongModel, FastModel];

    /// <summary>
    /// OpenRouter models suggested in Configurações, all with tool calling (checked in GET /api/v1/models on 2026-11-21):
    /// two free ones for demos and tests, then DeepSeek's. Any other id in the <c>author/model</c> format is accepted.
    /// </summary>
    public static IReadOnlyList<string> OpenRouterSuggestedModels { get; } =
    [
        "google/gemma-4-31b-it:free",
        "nvidia/nemotron-3-ultra-550b-a55b:free",
        "deepseek/deepseek-v4.1-flash",
        "deepseek/deepseek-v4-pro",
    ];

    /// <summary>Longest model id: the size of <c>llm_usage.model</c>.</summary>
    public const int MaxModelIdLength = 80;

    /// <summary>The models offered for each provider. The local provider serves only the fine-tuned Matcher.</summary>
    public static IReadOnlyList<string> ModelsFor(LlmProviderKind provider) => provider switch
    {
        LlmProviderKind.DeepSeek => SupportedModels,
        LlmProviderKind.OpenRouter => OpenRouterSuggestedModels,
        _ => [LocalModel],
    };

    /// <summary>Whether the provider takes a model id typed by the administrator, beyond the suggested ones.</summary>
    public static bool AcceptsAnyModel(LlmProviderKind provider) => provider == LlmProviderKind.OpenRouter;

    /// <summary>An OpenRouter id: <c>author/model</c>, lower case, with an optional variant such as <c>:free</c>.</summary>
    public static bool IsValidOpenRouterModel(string? model) =>
        model is { Length: > 0 and <= MaxModelIdLength } && OpenRouterModelId().IsMatch(model);

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]*/[a-z0-9][a-z0-9._:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex OpenRouterModelId();

    /// <summary>Providers that may serve a role: the local fine-tuned model only recognises recipes, it never plans.</summary>
    public static IReadOnlyList<LlmProviderKind> ProvidersFor(LlmUseRole role) => role == LlmUseRole.Planner
        ? [LlmProviderKind.DeepSeek, LlmProviderKind.OpenRouter]
        : [LlmProviderKind.DeepSeek, LlmProviderKind.OpenRouter, LlmProviderKind.Local];

    /// <summary>Providers that need an API key (the local server needs none).</summary>
    public static IReadOnlyList<LlmProviderKind> KeyedProviders { get; } = [LlmProviderKind.DeepSeek, LlmProviderKind.OpenRouter];

    public static bool IsSupported(LlmChoice choice, LlmUseRole role) =>
        ProvidersFor(role).Contains(choice.Provider)
        && (AcceptsAnyModel(choice.Provider) ? IsValidOpenRouterModel(choice.Model) : ModelsFor(choice.Provider).Contains(choice.Model));

    private readonly ISecretStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly string _databaseId = !string.IsNullOrEmpty(databaseId) ? databaseId : throw new ArgumentException("Database id is required.", nameof(databaseId));

    // ---- keys ----------------------------------------------------------------------------------------------

    public bool IsConfigured => IsConfiguredFor(LlmProviderKind.DeepSeek);

    public bool IsConfiguredFor(LlmProviderKind provider)
    {
        if (provider == LlmProviderKind.Local) return true;
        var key = _store.Get(_databaseId, KeyName(provider));
        if (key is null) return false;
        var ok = key.Length > 0;
        Array.Clear(key);
        return ok;
    }

    /// <summary>Validates and stores the DeepSeek key. Throws <see cref="ArgumentException"/> without echoing the key.</summary>
    public void SetKey(string key) => SetKey(LlmProviderKind.DeepSeek, key);

    /// <summary>Validates and stores a provider key. Throws <see cref="ArgumentException"/> without echoing the key.</summary>
    public void SetKey(LlmProviderKind provider, string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var name = KeyName(provider);
        var trimmed = key.Trim();
        if (trimmed.Length < MinKeyLength || trimmed.Length > MaxKeyLength)
            throw new ArgumentException($"The API key must have {MinKeyLength} to {MaxKeyLength} characters.", nameof(key));
        foreach (var c in trimmed)
            if (c < 0x21 || c > 0x7E)
                throw new ArgumentException("The API key must contain only printable ASCII characters without spaces.", nameof(key));
        var bytes = Encoding.UTF8.GetBytes(trimmed);
        try { _store.Set(_databaseId, name, bytes); }
        finally { Array.Clear(bytes); }
    }

    public void ClearKey() => ClearKey(LlmProviderKind.DeepSeek);

    public void ClearKey(LlmProviderKind provider) => _store.Delete(_databaseId, KeyName(provider));

    /// <summary>Returns the DeepSeek key bytes, or null. The caller must zero the array.</summary>
    public byte[]? ReadKey() => ReadKey(LlmProviderKind.DeepSeek);

    /// <summary>Returns a provider key, or null. The caller must zero the array.</summary>
    public byte[]? ReadKey(LlmProviderKind provider) => _store.Get(_databaseId, KeyName(provider));

    private static string KeyName(LlmProviderKind provider) => provider switch
    {
        LlmProviderKind.DeepSeek => KeyEntry,
        LlmProviderKind.OpenRouter => OpenRouterKeyEntry,
        _ => throw new ArgumentException("The local model has no key.", nameof(provider)),
    };

    // ---- models --------------------------------------------------------------------------------------------

    /// <summary>The DeepSeek planning model; an unknown stored value falls back to <see cref="DefaultPlannerModel"/>.</summary>
    public string Model
    {
        get
        {
            var text = ReadText(ModelEntry);
            return text is not null && SupportedModels.Contains(text) ? text : DefaultPlannerModel;
        }
        set
        {
            if (!SupportedModels.Contains(value ?? string.Empty))
                throw new ArgumentException("Unsupported model.", nameof(value));
            _store.Set(_databaseId, ModelEntry, Encoding.UTF8.GetBytes(value!));
        }
    }

    /// <summary>
    /// The provider and model of a role. Defaults keep the behaviour from before feature 8: the Planner on DeepSeek
    /// (<see cref="Model"/>) and the Matcher on <see cref="FastModel"/>. A stored value that is no longer offered falls back
    /// to the default.
    /// </summary>
    public LlmChoice ChoiceFor(LlmUseRole role)
    {
        if (ReadText(RoleEntry(role)) is { } text && Parse(text) is { } choice && IsSupported(choice, role))
            return choice;
        return role == LlmUseRole.Planner
            ? new LlmChoice(LlmProviderKind.DeepSeek, Model)
            : new LlmChoice(LlmProviderKind.DeepSeek, FastModel);
    }

    /// <exception cref="ArgumentException">The provider or model is not offered for the role.</exception>
    public void SetChoice(LlmUseRole role, LlmChoice choice)
    {
        ArgumentNullException.ThrowIfNull(choice);
        if (!IsSupported(choice, role))
            throw new ArgumentException("Unsupported model for this role.", nameof(choice));
        _store.Set(_databaseId, RoleEntry(role), Encoding.UTF8.GetBytes(Format(choice)));
        // Keeps the legacy entry in step, so a DeepSeek planner model is also what Model reports.
        if (role == LlmUseRole.Planner && choice.Provider == LlmProviderKind.DeepSeek)
            Model = choice.Model;
    }

    /// <summary>Port of the local model server (loopback only).</summary>
    public int LocalPort
    {
        get => ReadText(LocalPortEntry) is { } text && int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1024 and <= 65535 ? port : DefaultLocalPort;
        set
        {
            if (value is < 1024 or > 65535)
                throw new ArgumentException("The port must be between 1024 and 65535.", nameof(value));
            _store.Set(_databaseId, LocalPortEntry, Encoding.UTF8.GetBytes(value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>The embeddings provider of the hybrid RAG search; null = off (BM25 only, the default).</summary>
    /// <exception cref="ArgumentException">The provider cannot embed or the model is not its embeddings model.</exception>
    public LlmChoice? EmbeddingChoice
    {
        get => ReadText(EmbeddingEntry) is { } text && Parse(text) is { } choice && IsEmbeddingChoice(choice) ? choice : null;
        set
        {
            if (value is null)
            {
                _store.Delete(_databaseId, EmbeddingEntry);
                return;
            }
            if (!IsEmbeddingChoice(value))
                throw new ArgumentException("Unsupported embeddings model.", nameof(value));
            _store.Set(_databaseId, EmbeddingEntry, Encoding.UTF8.GetBytes(Format(value)));
        }
    }

    private static bool IsEmbeddingChoice(LlmChoice choice) =>
        EmbeddingProviders.Contains(choice.Provider) && choice.Model == EmbeddingModelFor(choice.Provider);

    public static string ProviderName(LlmProviderKind provider) => provider switch
    {
        LlmProviderKind.DeepSeek => "DeepSeek",
        LlmProviderKind.OpenRouter => "OpenRouter",
        _ => "Modelo local",
    };

    private static string RoleEntry(LlmUseRole role) => role == LlmUseRole.Planner ? "llm.role.planner" : "llm.role.matcher";

    private static string Format(LlmChoice choice) => choice.Provider.ToString().ToLowerInvariant() + ":" + choice.Model;

    private static LlmChoice? Parse(string text)
    {
        var colon = text.IndexOf(':');
        if (colon <= 0) return null;
        return Enum.TryParse<LlmProviderKind>(text[..colon], ignoreCase: true, out var provider) && Enum.IsDefined(provider)
            ? new LlmChoice(provider, text[(colon + 1)..])
            : null;
    }

    private string? ReadText(string entry)
    {
        var raw = _store.Get(_databaseId, entry);
        return raw is null ? null : Encoding.UTF8.GetString(raw);
    }
}
