using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using erp_inteligente.Models;

namespace erp_inteligente.Services;

/// <summary>A lesson as the Treinamento screen lists it; <see cref="CompletedAt"/> is null while it was never completed.</summary>
public sealed record TrainingLessonStatus(string Id, string Title, string Summary, DateTimeOffset? CompletedAt);

/// <summary>
/// The guided training seen from the Treinamento screen (references/UI.md, section 10): the lessons of the person's
/// role and the sandbox window that runs them.
/// </summary>
public interface ITrainingService
{
    /// <summary>The lessons offered to the role of <paramref name="user"/>, in order, with what was already completed.</summary>
    IReadOnlyList<TrainingLessonStatus> Lessons(UserSession user);

    /// <summary>
    /// Opens the sandbox on the given lesson (the first one when null) and completes when the person leaves it.
    /// </summary>
    Task RunAsync(UserSession user, string? startLessonId = null);
}
