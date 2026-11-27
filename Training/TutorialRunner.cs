using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Models;
using erp_inteligente.ViewModels;

namespace erp_inteligente.Training;

/// <summary>
/// Walks a person through the lessons inside the sandbox: holds the current step, watches the sandbox shell to notice
/// when a step was done and moves on by itself. Próximo always works, so nobody gets stuck when the AI answers
/// differently from what a step expects. It knows nothing about windows or overlays.
/// </summary>
public sealed partial class TutorialRunner : ObservableObject, IDisposable
{
    private readonly IReadOnlyList<TutorialLesson> _lessons;
    private readonly string _defaultPersona;
    private readonly Func<string, Task<ShellViewModel>> _switchPersona;
    private readonly TimeProvider _time;
    private readonly Action<TutorialLesson> _lessonCompleted;

    private ShellViewModel _shell;
    private string _persona;
    private TutorialContext _context;
    private DateTimeOffset _lessonStartedAt;
    private INotifyPropertyChanged? _watchedPage;
    private NexusViewModel? _watchedNexus;
    private int _lessonIndex;
    private int _stepIndex;
    private bool _moving;
    private bool _disposed;

    /// <param name="lessons">The lessons offered to the person, in order; at least one.</param>
    /// <param name="shell">The sandbox shell of <paramref name="defaultPersona"/>, already built.</param>
    /// <param name="switchPersona">Signs another seed user into the sandbox and returns its shell.</param>
    /// <param name="lessonCompleted">Called once when the last step of a lesson is left forwards.</param>
    public TutorialRunner(IReadOnlyList<TutorialLesson> lessons, string defaultPersona, ShellViewModel shell,
        Func<string, Task<ShellViewModel>> switchPersona, TimeProvider time, Action<TutorialLesson> lessonCompleted, int startLesson = 0)
    {
        ArgumentNullException.ThrowIfNull(lessons);
        if (lessons.Count == 0)
            throw new ArgumentException("The training needs at least one lesson.", nameof(lessons));
        _lessons = lessons;
        _defaultPersona = defaultPersona;
        _persona = defaultPersona;
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _switchPersona = switchPersona ?? throw new ArgumentNullException(nameof(switchPersona));
        _time = time;
        _lessonCompleted = lessonCompleted ?? throw new ArgumentNullException(nameof(lessonCompleted));
        _lessonIndex = Math.Clamp(startLesson, 0, lessons.Count - 1);
        _lessonStartedAt = time.GetUtcNow();
        _context = new TutorialContext(shell, _lessonStartedAt);
        Watch(shell);
    }

    /// <summary>The sandbox shell of the persona of the current step; it is replaced when the persona changes.</summary>
    public ShellViewModel Shell => _shell;

    /// <summary>Seed username of the persona of the current step.</summary>
    public string Persona => _persona;

    public TutorialLesson Lesson => _lessons[_lessonIndex];

    public TutorialStep Step => Lesson.Steps[_stepIndex];

    /// <summary>What the overlay highlights; null when the text is centred or the training is over.</summary>
    public string? Anchor => IsFinished ? null : Step.Anchor;

    public string LessonText => IsFinished
        ? "Treinamento"
        : $"Lição {_lessonIndex + 1} de {_lessons.Count} · {Lesson.Title}";

    public string StepCounter => IsFinished ? "" : $"{_stepIndex + 1} de {Lesson.Steps.Count}";

    public string StepTitle => IsFinished ? "Treinamento concluído" : Step.Title;

    public string StepText => IsFinished
        ? "Você passou por todas as lições do seu papel. Pode repetir qualquer uma pela tela Treinamento. Feche esta janela para voltar ao sistema real."
        : Step.Text;

    public string? Suggestion => IsFinished ? null : Step.Suggestion;

    public bool HasSuggestion => Suggestion is not null;

    /// <summary>"Pular" while a step that checks itself was not done yet.</summary>
    public string NextLabel => IsFinished ? "Fechar"
        : Step.IsDone is not null && !IsStepDone ? "Pular"
        : _stepIndex == Lesson.Steps.Count - 1 ? "Concluir lição"
        : "Próximo";

    public bool CanGoBack => !IsFinished && _stepIndex > 0;

    /// <summary>The predicate of the current step is satisfied (always false for steps that are only read).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextLabel))]
    public partial bool IsStepDone { get; private set; }

    /// <summary>Every lesson was walked through; the overlay shows the closing text.</summary>
    [ObservableProperty]
    public partial bool IsFinished { get; private set; }

    /// <summary>Raised after <see cref="Shell"/> was replaced by the shell of another persona.</summary>
    public event EventHandler? ShellChanged;

    /// <summary>Raised when the person presses Fechar on the closing text.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Enters the first step (which may sign another persona in).</summary>
    public Task StartAsync() => EnterStepAsync();

    /// <summary>Moves to the next step, lesson or the closing text; on the closing text it asks to close.</summary>
    [RelayCommand]
    private async Task NextAsync()
    {
        if (_moving || _disposed)
            return;
        if (IsFinished)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_stepIndex < Lesson.Steps.Count - 1)
        {
            _stepIndex++;
        }
        else
        {
            _lessonCompleted(Lesson);
            if (_lessonIndex == _lessons.Count - 1)
            {
                IsFinished = true;
                await RestorePersonaAsync(_defaultPersona);
                NotifyStep();
                return;
            }
            _lessonIndex++;
            _stepIndex = 0;
            _lessonStartedAt = _time.GetUtcNow();
        }
        await EnterStepAsync();
    }

    private bool CanBack() => CanGoBack;

    /// <summary>Goes back one step inside the lesson.</summary>
    [RelayCommand(CanExecute = nameof(CanBack))]
    private async Task BackAsync()
    {
        if (_moving || _disposed || !CanGoBack)
            return;
        _stepIndex--;
        await EnterStepAsync();
    }

    /// <summary>Copies the suggested request into the Nexus field; the person still sends it.</summary>
    [RelayCommand]
    private void UseSuggestion()
    {
        if (Suggestion is { } text && _context.Nexus is { IsInputEnabled: true } nexus)
            nexus.Draft = text;
    }

    private async Task EnterStepAsync()
    {
        _moving = true;
        try
        {
            await RestorePersonaAsync(Step.Persona ?? _defaultPersona);
            _context = new TutorialContext(_shell, _lessonStartedAt) { NexusEntriesAtStepStart = CountNexusEntries() };
            IsStepDone = false;
            NotifyStep();
        }
        finally
        {
            _moving = false;
        }
        // A step that is already satisfied (the screen was open, the plan existed) is shown as done, not skipped:
        // the person still reads it.
        IsStepDone = Step.IsDone is { } done && SafeCheck(done);
    }

    private async Task RestorePersonaAsync(string persona)
    {
        if (persona == _persona)
            return;
        var shell = await _switchPersona(persona);
        Unwatch();
        _shell = shell;
        _persona = persona;
        Watch(shell);
        OnPropertyChanged(nameof(Shell));
        OnPropertyChanged(nameof(Persona));
        ShellChanged?.Invoke(this, EventArgs.Empty);
    }

    private int CountNexusEntries() =>
        // Only a Nexus that already exists counts: asking for the page would create it.
        _watchedNexus?.Entries.Count ?? 0;

    private void NotifyStep()
    {
        OnPropertyChanged(nameof(Lesson));
        OnPropertyChanged(nameof(Step));
        OnPropertyChanged(nameof(Anchor));
        OnPropertyChanged(nameof(LessonText));
        OnPropertyChanged(nameof(StepCounter));
        OnPropertyChanged(nameof(StepTitle));
        OnPropertyChanged(nameof(StepText));
        OnPropertyChanged(nameof(Suggestion));
        OnPropertyChanged(nameof(HasSuggestion));
        OnPropertyChanged(nameof(NextLabel));
        OnPropertyChanged(nameof(CanGoBack));
        BackCommand.NotifyCanExecuteChanged();
    }

    // ---- watching the sandbox ------------------------------------------------------------------------------

    private void Watch(ShellViewModel shell)
    {
        shell.PropertyChanged += OnShellChanged;
        shell.Plans.PlanChanged += OnPlanChanged;
        WatchPage(shell.CurrentPage);
        WatchNexus(shell);
    }

    private void Unwatch()
    {
        _shell.PropertyChanged -= OnShellChanged;
        _shell.Plans.PlanChanged -= OnPlanChanged;
        WatchPage(null);
        if (_watchedNexus is not null)
        {
            _watchedNexus.Entries.CollectionChanged -= OnNexusEntriesChanged;
            _watchedNexus.PropertyChanged -= OnWatchedPropertyChanged;
            _watchedNexus = null;
        }
    }

    private void WatchPage(PageViewModel? page)
    {
        if (_watchedPage is not null)
            _watchedPage.PropertyChanged -= OnWatchedPropertyChanged;
        _watchedPage = page;
        if (_watchedPage is not null)
            _watchedPage.PropertyChanged += OnWatchedPropertyChanged;
    }

    /// <summary>The Nexus page is kept for the session, so it is watched from the first time it is shown.</summary>
    private void WatchNexus(ShellViewModel shell)
    {
        if (_watchedNexus is not null || shell.CurrentPage is not NexusViewModel nexus)
            return;
        _watchedNexus = nexus;
        nexus.Entries.CollectionChanged += OnNexusEntriesChanged;
        nexus.PropertyChanged += OnWatchedPropertyChanged;
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ShellViewModel.CurrentPage))
            return;
        WatchPage(_shell.CurrentPage);
        WatchNexus(_shell);
        Evaluate();
    }

    private void OnPlanChanged(object? sender, Plan plan) => Evaluate();

    private void OnNexusEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => Evaluate();

    private void OnWatchedPropertyChanged(object? sender, PropertyChangedEventArgs e) => Evaluate();

    /// <summary>Checks the current step again; a step that has just been done moves on by itself.</summary>
    public void Evaluate()
    {
        if (_moving || _disposed || IsFinished || IsStepDone || Step.IsDone is not { } done)
            return;
        if (!SafeCheck(done))
            return;
        IsStepDone = true;
        _ = AdvanceAsync();
    }

    private async Task AdvanceAsync()
    {
        try
        {
            await NextAsync();
        }
        catch (Exception ex)
        {
            // The person can still press Próximo; a failed persona switch is reported by the window.
            Trace.TraceWarning("Training step change failed: {0}", ex.GetType().Name);
        }
    }

    /// <summary>A predicate reads view models that may be mid-update; a failure only means "not yet".</summary>
    private bool SafeCheck(Func<TutorialContext, bool> done)
    {
        try
        {
            return done(_context);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException or ArgumentException)
        {
            Trace.TraceWarning("Training step check failed: {0}", ex.GetType().Name);
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Unwatch();
    }
}
