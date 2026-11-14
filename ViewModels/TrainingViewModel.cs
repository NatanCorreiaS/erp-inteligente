using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Models;
using erp_inteligente.Services;

namespace erp_inteligente.ViewModels;

/// <summary>A lesson in the list of the Treinamento screen.</summary>
public sealed record TrainingLessonRow(string Id, string Number, string Title, string Summary, bool IsCompleted, string StateText);

/// <summary>
/// The Treinamento screen (references/UI.md, section 10): the lessons of the person's role, which ones were completed,
/// and the button that opens the sandbox. The screen itself changes nothing.
/// </summary>
public sealed partial class TrainingViewModel(UserSession user, ITrainingService training) : PageViewModel
{
    public override string Title => "Treinamento";

    public ObservableCollection<TrainingLessonRow> Lessons { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartLessonCommand))]
    public partial TrainingLessonRow? SelectedLesson { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StartLessonCommand))]
    public partial bool IsWorking { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string ErrorText { get; private set; } = "";

    public bool HasError => ErrorText.Length > 0;

    [ObservableProperty]
    public partial string ProgressText { get; private set; } = "";

    /// <summary>"Continuar da lição 3" when some lessons were completed and others were not.</summary>
    [ObservableProperty]
    public partial string StartLabel { get; private set; } = "Iniciar treinamento";

    public override Task OnActivatedAsync()
    {
        Reload();
        return Task.CompletedTask;
    }

    private bool CanStart() => !IsWorking && Lessons.Count > 0;

    /// <summary>Opens the sandbox on the first lesson not completed yet (the first one when all were).</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunAsync(Lessons.FirstOrDefault(l => !l.IsCompleted)?.Id);

    private bool CanStartLesson() => !IsWorking && SelectedLesson is not null;

    /// <summary>Opens the sandbox on the selected lesson.</summary>
    [RelayCommand(CanExecute = nameof(CanStartLesson))]
    private Task StartLessonAsync() => RunAsync(SelectedLesson?.Id);

    private async Task RunAsync(string? lessonId)
    {
        IsWorking = true;
        ErrorText = "";
        try
        {
            await training.RunAsync(user, lessonId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Training failed: {0}", ex.GetType().Name);
            ErrorText = "Não foi possível abrir o ambiente de treinamento. Tente de novo.";
        }
        finally
        {
            IsWorking = false;
            Reload();
        }
    }

    private void Reload()
    {
        var selected = SelectedLesson?.Id;
        Lessons.Clear();
        try
        {
            var number = 0;
            foreach (var lesson in training.Lessons(user))
            {
                number++;
                Lessons.Add(new TrainingLessonRow(lesson.Id, number.ToString(), lesson.Title, lesson.Summary, lesson.CompletedAt is not null,
                    lesson.CompletedAt is { } at ? "Concluída em " + at.ToLocalTime().ToString("dd/MM/yyyy HH:mm") : "Não feita"));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceError("Training lessons unavailable: {0}", ex.GetType().Name);
            ErrorText = "Não foi possível ler as lições.";
        }

        var done = Lessons.Count(l => l.IsCompleted);
        ProgressText = $"{done} de {Lessons.Count} lições concluídas";
        var next = Lessons.FirstOrDefault(l => !l.IsCompleted);
        StartLabel = done == 0 ? "Iniciar treinamento"
            : next is null ? "Refazer o treinamento"
            : "Continuar da lição " + next.Number;
        SelectedLesson = Lessons.FirstOrDefault(l => l.Id == selected);
        StartCommand.NotifyCanExecuteChanged();
    }
}
