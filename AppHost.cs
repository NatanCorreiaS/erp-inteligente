using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Auth;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Knowledge;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Memory;
using erp_inteligente.Core.Operations;
using erp_inteligente.Core.Planner;
using erp_inteligente.Core.Policy;
using erp_inteligente.Core.Recipes;
using erp_inteligente.Core.Security;
using erp_inteligente.Core.Skills;
using erp_inteligente.Services;
using erp_inteligente.Services.Sqlite;
using erp_inteligente.Training;
using erp_inteligente.ViewModels;

namespace erp_inteligente;

/// <summary>
/// Composition root (ROADMAP.md, features 1 to 3): the encrypted database, audit log, policy engine, authentication,
/// session, plan engine and the LLM provider. One instance per running app; disposing it zeroes the database keys.
/// </summary>
public sealed class AppHost : IDisposable
{
    /// <param name="secrets">The OS key store; it also holds the DeepSeek key (references/ARQUITETURA.md, section 12.10).</param>
    /// <param name="llm">The provider; null builds the real DeepSeek client (tests pass a scripted one).</param>
    /// <param name="runScheduledJobs">Runs the daily jobs now (feature 9: backup and memory maintenance); the app does, tests do not.</param>
    /// <param name="embeddings">Embeddings of the RAG; null builds the configured provider (the local server), or none when
    /// <paramref name="llm"/> is scripted (tests never reach a real API).</param>
    /// <param name="embeddingsHandler">HTTP handler of the configured embeddings provider (tests pass a fake one).</param>
    public AppHost(NexusDatabase database, TimeProvider time, Argon2Parameters passwordParameters, AuthOptions authOptions,
        ISecretStore secrets, ILlmProvider? llm = null, bool runScheduledJobs = false, IEmbeddingProvider? embeddings = null,
        System.Net.Http.HttpMessageHandler? embeddingsHandler = null)
    {
        Database = database;
        Time = time;
        AuditLog = new AuditLog(database, time);
        Policy = new PolicyEngine(database, AuditLog);
        ApplicationDb = new ApplicationDb(database, Policy);
        Auth = new AuthService(database, new PasswordHasher(passwordParameters), Policy, AuditLog, time, authOptions);
        Session = new SessionService(Auth, time, authOptions);
        MemoryStore = new MemoryStore(database, Policy, AuditLog, time);
        MemoryMaintenance = new MemoryMaintenance(database, AuditLog, time, Policy);
        UsageLog = new LlmUsageLog(database, time, Policy);
        Backups = new BackupService(database, Policy, AuditLog, time);
        var skills = new SkillCatalog(database);
        RecipeStore = new RecipeStore(database, Policy, AuditLog, time, skills);
        MemoryFeedback = new MemoryFeedback(database, MemoryStore, AuditLog, time);
        Engine = new PlanEngine(database, Policy, AuditLog, time, MemoryStore, RecipeStore, MemoryFeedback);
        LlmSettings = new LlmSettings(secrets, database.Path);
        // Feature 8: one adapter per provider behind a router; each request names its provider (model per role).
        var settings = LlmSettings;
        var handler = ChatCompletionsProvider.CreateDefaultHandler();
        DeepSeekProvider? deepSeek = null;
        OpenRouterProvider? openRouter = null;
        LocalProvider? local = null;
        if (llm is null)
        {
            deepSeek = new DeepSeekProvider(handler, () => settings.ReadKey(LlmProviderKind.DeepSeek));
            openRouter = new OpenRouterProvider(handler, () => settings.ReadKey(LlmProviderKind.OpenRouter));
            local = new LocalProvider(handler, () => settings.LocalPort);
        }
        Llm = llm ?? new LlmRouter(new Dictionary<LlmProviderKind, ILlmProvider>
        {
            [LlmProviderKind.DeepSeek] = deepSeek!,
            [LlmProviderKind.OpenRouter] = openRouter!,
            [LlmProviderKind.Local] = local!,
        });
        // Feature 5: vectors of the procedures for the hybrid search; off until an administrator picks a provider.
        Embeddings = embeddings ?? (llm is null || embeddingsHandler is not null
            ? new ConfiguredEmbeddingProvider(LlmSettings, embeddingsHandler ?? handler)
            : NullEmbeddingProvider.Instance);
        VectorIndex = new KnowledgeVectorIndex(database, Embeddings, time);
        LlmSettingsService = new LlmSettingsService(LlmSettings, Policy, AuditLog, (provider, ct) => provider switch
        {
            LlmProviderKind.DeepSeek => deepSeek?.TestConnectionAsync(ct) ?? Task.CompletedTask,
            LlmProviderKind.OpenRouter => openRouter?.TestConnectionAsync(ct) ?? Task.CompletedTask,
            _ => local?.TestConnectionAsync(LlmSettings.LocalModel, ct) ?? Task.CompletedTask,
        }, VectorIndex);
        SyncKnowledge(database);
        MemoryRetriever = new MemoryRetriever(database, Policy);
        KnowledgeRetriever = new KnowledgeRetriever(database, VectorIndex);
        Planner = new Planner(Llm, LlmSettings, database, Policy, AuditLog, Engine, skills: skills,
            memories: MemoryStore, memoryRetriever: MemoryRetriever, knowledge: KnowledgeRetriever,
            recipeMatcher: new RecipeMatcher(Llm, RecipeStore, AuditLog, LlmSettings, UsageLog), usage: UsageLog,
            feedback: MemoryFeedback);
        SkillStore = new SkillStore(database, Policy, AuditLog, time);
        AuditStatus = VerifyAudit();
        if (runScheduledJobs)
            RunScheduledJobs();
    }

    /// <summary>
    /// The daily jobs of feature 9, at startup: a backup when the last one is older than a day, and the memory maintenance
    /// when it has not run for a day. A failure is traced and never stops the app.
    /// </summary>
    public void RunScheduledJobs()
    {
        try { Backups.CreateScheduled(); }
        catch (Exception ex) { Trace.TraceWarning("Scheduled backup failed: " + ex.GetType().Name); }
        try { MemoryMaintenance.RunScheduled(); }
        catch (Exception ex) { Trace.TraceWarning("Scheduled memory maintenance failed: " + ex.GetType().Name); }
        // Vectors of new or changed procedures, in the background: the search stays BM25 for chunks not embedded yet.
        if (Embeddings.Model is not null)
            _ = Task.Run(async () =>
            {
                try { await VectorIndex.SyncAsync(System.Threading.CancellationToken.None); }
                catch (Exception ex) { Trace.TraceWarning("Knowledge embedding failed: " + ex.GetType().Name); }
            });
    }

    public NexusDatabase Database { get; }
    public TimeProvider Time { get; }
    public AuditLog AuditLog { get; }
    public PolicyEngine Policy { get; }
    public ApplicationDb ApplicationDb { get; }
    public AuthService Auth { get; }
    public SessionService Session { get; }
    public PlanEngine Engine { get; }
    public LlmSettings LlmSettings { get; }
    public ILlmProvider Llm { get; }
    public LlmSettingsService LlmSettingsService { get; }
    public Planner Planner { get; }
    public SkillStore SkillStore { get; }
    public MemoryStore MemoryStore { get; }
    public RecipeStore RecipeStore { get; }
    public MemoryMaintenance MemoryMaintenance { get; }
    public MemoryFeedback MemoryFeedback { get; }
    public LlmUsageLog UsageLog { get; }
    public BackupService Backups { get; }
    public MemoryRetriever MemoryRetriever { get; }
    public KnowledgeRetriever KnowledgeRetriever { get; }
    public IEmbeddingProvider Embeddings { get; }
    public KnowledgeVectorIndex VectorIndex { get; }

    /// <summary>Last verification of the audit hash chain (at startup and at every login).</summary>
    public AuditVerification AuditStatus { get; private set; }

    /// <summary>Opens (or creates on first run) the database at <paramref name="databasePath"/> with keys from <paramref name="store"/>.</summary>
    public static AppHost Open(string databasePath, ISecretStore store) =>
        new(DatabaseBootstrapper.OpenOrCreate(databasePath, new KeyManager(store)),
            TimeProvider.System, Argon2Parameters.Default, AuthOptions.Default, store, runScheduledJobs: true);

    /// <summary>Indexes the embedded procedures (idempotent). A failure only leaves the search empty: the app still starts.</summary>
    private static void SyncKnowledge(NexusDatabase database)
    {
        try
        {
            KnowledgeIndexer.Sync(database);
        }
        catch (Exception ex)
        {
            Trace.TraceWarning("Knowledge index sync failed: " + ex.GetType().Name);
        }
    }

    /// <summary>Re-checks the whole audit chain and keeps the result for the next shell.</summary>
    public AuditVerification VerifyAudit()
    {
        using var connection = Database.OpenApplication();
        return AuditStatus = AuditVerifier.Verify(connection);
    }

    /// <summary>Audit events and token accounting of the guided training (references/UI.md, section 10).</summary>
    public TrainingLog TrainingLog => new(Database, AuditLog, UsageLog);

    /// <summary>Builds the main window view model for the session that has just started.</summary>
    /// <param name="training">Opens the guided training; null hides the menu item.</param>
    /// <param name="sandbox">
    /// True for the shell of a training sandbox: Configurações does not edit the provider settings (the sandbox calls
    /// the provider with the real configuration) and leaving is labelled as leaving the training.
    /// </param>
    public ShellViewModel CreateShell(IDialogService dialogs, ITrainingService? training = null, bool sandbox = false) =>
        new(Session,
            new SqliteQueryService(ApplicationDb, Session),
            new EnginePlanService(Engine, ApplicationDb, Session),
            new PlannerNexusService(Planner, PlannerOptions.Default.MaxHistoryTurns),
            dialogs,
            Time,
            AuditStatus,
            $"{Database.Path} (criptografado com SQLCipher)",
            sandbox ? null : LlmSettingsService,
            new SqliteSkillService(SkillStore, Session),
            new SqliteMemoryService(MemoryStore, Session, MemoryMaintenance),
            new SqliteRecipeService(RecipeStore, Session),
            new SqliteOperationsService(Database, Policy, UsageLog, Backups, Session, Time),
            training,
            sandbox ? "Sair do treinamento" : "Sair");

    public void Dispose() => Database.Dispose();
}
