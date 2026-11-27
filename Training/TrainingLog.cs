using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using erp_inteligente.Core.Audit;
using erp_inteligente.Core.Data;
using erp_inteligente.Core.Operations;
using erp_inteligente.Models;

namespace erp_inteligente.Training;

/// <summary>
/// What the real system keeps about a training: audit events (<c>training.start</c>, <c>training.lesson.complete</c>,
/// <c>training.end</c>) and the tokens the AI consumed in the sandbox, recorded under the person who trained. The
/// lessons a person completed are read back from those events, so there is no table of its own.
/// </summary>
public sealed class TrainingLog(NexusDatabase database, IAuditLog audit, LlmUsageLog usage)
{
    public const string StartAction = "training.start";
    public const string LessonAction = "training.lesson.complete";
    public const string EndAction = "training.end";

    private const string LessonTargetPrefix = "lesson:";

    public void Started(UserSession user, string persona) =>
        Append(user, StartAction, "training", new JsonObject { ["persona"] = persona });

    public void LessonCompleted(UserSession user, TutorialLesson lesson) =>
        Append(user, LessonAction, LessonTargetPrefix + lesson.Id, null);

    /// <summary>Closes the training in the audit and copies the token usage of the sandbox to the real accounting.</summary>
    public void Ended(UserSession user, IReadOnlyList<LlmUsageEntry> sandboxUsage)
    {
        long tokens = 0;
        foreach (var entry in sandboxUsage)
        {
            usage.Record(user, entry);
            tokens += entry.PromptTokens + entry.CompletionTokens;
        }
        Append(user, EndAction, "training", new JsonObject { ["llm_requests"] = sandboxUsage.Count, ["llm_tokens"] = tokens });
    }

    /// <summary>When the person last completed each lesson, by lesson id.</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> CompletedLessons(UserSession user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var completed = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        using var connection = database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT target, MAX(occurred_at) FROM audit_log " +
            "WHERE action = $action AND actor_type = 'user' AND actor_id = $user AND outcome = 'allowed' GROUP BY target";
        command.Parameters.AddWithValue("$action", LessonAction);
        command.Parameters.AddWithValue("$user", user.UserId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var target = reader.GetString(0);
            if (target.StartsWith(LessonTargetPrefix, StringComparison.Ordinal)
                && DateTimeOffset.TryParse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                completed[target[LessonTargetPrefix.Length..]] = at;
        }
        return completed;
    }

    private void Append(UserSession user, string action, string target, JsonObject? details) =>
        audit.Append(new AuditEntry(AuditActorType.User, user.UserId, null, action, target, AuditOutcome.Allowed, details?.ToJsonString()));
}
