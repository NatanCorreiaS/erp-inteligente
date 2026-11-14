using System;
using System.Collections.Generic;
using System.Linq;
using erp_inteligente.Models;
using erp_inteligente.ViewModels;

namespace erp_inteligente.Training;

/// <summary>
/// Names of the controls a step can highlight (references/UI.md, section 10). The views mark them with the attached
/// property <c>Tutorial.Anchor</c>; a step without an anchor shows its text in the middle of the window.
/// </summary>
public static class TutorialAnchors
{
    /// <summary>Screen title at the top of the content container.</summary>
    public const string Title = "title";

    /// <summary>The whole content container below the title.</summary>
    public const string Content = "content";

    /// <summary>Request field and Enviar button of the Nexus.</summary>
    public const string NexusRequest = "nexus:request";

    /// <summary>Name and role of the signed-in user, in the footer of the menu column.</summary>
    public const string User = "footer:user";

    /// <summary>A button of the menu column, by <see cref="NavigationItem.IconKey"/>.</summary>
    public static string Nav(string iconKey) => "nav:" + iconKey;
}

/// <summary>
/// One step of a lesson: what the overlay highlights and says, and how the step is recognised as done.
/// </summary>
/// <param name="Anchor">A <see cref="TutorialAnchors"/> name; null centres the text.</param>
/// <param name="IsDone">Checked whenever the sandbox changes; null means the person just reads and moves on.</param>
/// <param name="Persona">Seed user that must be signed in for this step; null is the person's own persona.</param>
/// <param name="Suggestion">A request the person can copy into the Nexus field with one click.</param>
public sealed record TutorialStep(
    string Title,
    string Text,
    string? Anchor = null,
    Func<TutorialContext, bool>? IsDone = null,
    string? Persona = null,
    string? Suggestion = null);

/// <summary>A lesson of the training. It is offered when the persona has one of <see cref="RequiredAnyOf"/> (empty = always).</summary>
public sealed record TutorialLesson(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<string> RequiredAnyOf,
    IReadOnlyList<TutorialStep> Steps)
{
    public bool IsOfferedTo(UserSession persona) => RequiredAnyOf.Count == 0 || RequiredAnyOf.Any(persona.Has);
}

/// <summary>
/// What a step predicate may look at: the sandbox shell of the current persona and what existed when the step began.
/// </summary>
public sealed class TutorialContext(ShellViewModel shell, DateTimeOffset lessonStartedAt)
{
    public ShellViewModel Shell { get; } = shell;

    /// <summary>
    /// When the current lesson began, cut to the whole second like the timestamps of the plans; plans older than this
    /// belong to the seed or to earlier lessons.
    /// </summary>
    public DateTimeOffset LessonStartedAt { get; } = lessonStartedAt.AddTicks(-(lessonStartedAt.Ticks % TimeSpan.TicksPerSecond));

    /// <summary>Transcript entries the Nexus already had when the step began.</summary>
    public int NexusEntriesAtStepStart { get; internal set; }

    /// <summary><see cref="NavigationItem.IconKey"/> of the screen being shown.</summary>
    public string? ActiveKey => Shell.MenuItems.Concat(Shell.FooterItems).FirstOrDefault(i => i.IsActive)?.IconKey;

    public PageViewModel? Page => Shell.CurrentPage;

    /// <summary>The Nexus chat of this persona; null when the persona has no Nexus (auditor, administrator).</summary>
    public NexusViewModel? Nexus => Shell.MenuItems.FirstOrDefault(i => i.IconKey == "Nexus")?.Page as NexusViewModel;

    public IReadOnlyList<Plan> Plans => Shell.Plans.Plans;

    /// <summary>The Nexus finished answering a request sent during this step.</summary>
    public bool NexusReplied => Nexus is { IsBusy: false } nexus
        && nexus.Entries.Skip(NexusEntriesAtStepStart).Any(e => e.Author == NexusViewModel.NexusAuthor);

    /// <summary>A proposal of the Nexus is waiting for Confirmar or Descartar.</summary>
    public bool NexusProposed => Nexus?.PendingProposal is not null;

    /// <summary>A plan created in this lesson matches <paramref name="match"/>.</summary>
    public bool PlanInLesson(Func<Plan, bool> match) => Plans.Any(p => p.CreatedAt >= LessonStartedAt && match(p));
}
