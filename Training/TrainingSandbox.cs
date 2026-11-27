using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using erp_inteligente.Core.Auth;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Operations;
using erp_inteligente.Core.Security;
using erp_inteligente.Models;

namespace erp_inteligente.Training;

/// <summary>
/// The sandbox of the guided training: a throwaway copy of the app over a new encrypted database with the fictitious
/// seed data. Its keys live only in memory and its folder is deleted on <see cref="Dispose"/>, so nothing done in
/// training reaches the real warehouse. The LLM adapter is the real one, which reads the provider key from the real
/// key store on every call; the key is never copied here.
/// </summary>
public sealed class TrainingSandbox : IDisposable
{
    /// <summary>Password of every seed user (db/seed.sql). It only opens the throwaway database.</summary>
    private const string SeedPassword = "demo";

    private readonly DirectoryInfo _directory;
    private bool _disposed;

    private TrainingSandbox(DirectoryInfo directory, AppHost host)
    {
        _directory = directory;
        Host = host;
    }

    /// <summary>The whole core over the sandbox database.</summary>
    public AppHost Host { get; }

    /// <summary>Folder of the sandbox database, removed on dispose.</summary>
    public string DirectoryPath => _directory.FullName;

    /// <summary>
    /// Creates the database (schema and seed) and the core. It takes a moment: call it off the UI thread.
    /// </summary>
    /// <param name="llm">The provider of the real app (or a scripted one in tests).</param>
    public static TrainingSandbox Create(ILlmProvider llm, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(llm);
        var directory = Directory.CreateTempSubdirectory("nexus-treino-");
        NexusDatabase? database = null;
        try
        {
            var store = new InMemorySecretStore();
            database = DatabaseBootstrapper.OpenOrCreate(Path.Combine(directory.FullName, "treino.db"), new KeyManager(store));
            var host = new AppHost(database, time, Argon2Parameters.Default, AuthOptions.Default, store, llm);
            return new TrainingSandbox(directory, host);
        }
        catch
        {
            database?.Dispose();
            TryDelete(directory);
            throw;
        }
    }

    /// <summary>Signs a seed user into the sandbox (replacing the previous persona) and returns its session.</summary>
    /// <exception cref="InvalidOperationException">The user is not a seed user that can sign in.</exception>
    public async Task<UserSession> SignInAsync(string username)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var result = await Host.Auth.LoginAsync(username, SeedPassword);
        if (result is not LoginResult.Succeeded ok)
            throw new InvalidOperationException("The training persona could not sign in.");
        Host.Session.Start(ok.Session);
        return ok.Session;
    }

    /// <summary>What the AI consumed inside the sandbox, one entry per request and role. Never the text.</summary>
    public IReadOnlyList<LlmUsageEntry> ReadUsage()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var entries = new List<LlmUsageEntry>();
        using var connection = Host.Database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT role, provider, model, calls, prompt_tokens, completion_tokens, result, cache_read_tokens, cache_write_tokens " +
            "FROM llm_usage ORDER BY id";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Enum.TryParse<LlmProviderKind>(reader.GetString(1), ignoreCase: true, out var provider))
                continue;
            var role = reader.GetString(0) == "planner" ? LlmUseRole.Planner : LlmUseRole.Matcher;
            entries.Add(new LlmUsageEntry(role, new LlmChoice(provider, reader.GetString(2)), reader.GetInt32(3),
                reader.GetInt32(4), reader.GetInt32(5), reader.GetString(6))
            {
                CacheReadTokens = reader.GetInt32(7),
                CacheWriteTokens = reader.GetInt32(8),
            });
        }
        return entries;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Host.Dispose();
        TryDelete(_directory);
    }

    /// <summary>A file still held by the OS only leaves an encrypted database without its keys behind.</summary>
    private static void TryDelete(DirectoryInfo directory)
    {
        try
        {
            directory.Delete(recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Training sandbox folder not removed: {0}", ex.GetType().Name);
        }
    }
}
