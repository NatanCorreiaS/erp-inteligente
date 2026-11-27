using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using erp_inteligente.Models;
using erp_inteligente.Services;
using erp_inteligente.Training;
using erp_inteligente.ViewModels;

namespace erp_inteligente.Views;

/// <summary>
/// Opens the guided training over the main window: builds the sandbox, shows the training window as a native dialog
/// (the real window stays inert meanwhile) and, when it closes, records what was done and throws the sandbox away.
/// </summary>
/// <param name="touch">Called on input in the training window, so the real session does not expire while training.</param>
public sealed class TrainingLauncher(AppHost host, Window owner, Action touch) : ITrainingService
{
    private TrainingWindow? _open;

    public IReadOnlyList<TrainingLessonStatus> Lessons(UserSession user)
    {
        var completed = host.TrainingLog.CompletedLessons(user);
        // The persona has the permissions of the seed role; the real user's are the same unless the role was changed.
        return TutorialCatalog.All.Where(l => l.IsOfferedTo(user))
            .Select(l => new TrainingLessonStatus(l.Id, l.Title, l.Summary, completed.TryGetValue(l.Id, out var at) ? at : null))
            .ToList();
    }

    public async Task RunAsync(UserSession user, string? startLessonId = null)
    {
        if (_open is not null)
            return;

        var log = host.TrainingLog;
        // Creating the database and hashing the seed password take a moment: off the UI thread.
        using var sandbox = await Task.Run(() => TrainingSandbox.Create(host.Llm, host.Time));
        var window = new TrainingWindow();
        using var session = await TrainingSessionViewModel.StartAsync(sandbox, user, new DialogService(window),
            lesson => log.LessonCompleted(user, lesson), startLessonId);
        log.Started(user, session.Runner.Persona);
        try
        {
            window.DataContext = session;
            void Touch(object? sender, RoutedEventArgs e) => touch();
            window.AddHandler(InputElement.KeyDownEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(InputElement.PointerPressedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.AddHandler(InputElement.PointerWheelChangedEvent, Touch, RoutingStrategies.Tunnel, handledEventsToo: true);

            _open = window;
            await window.ShowDialog(owner);
        }
        finally
        {
            _open = null;
            log.Ended(user, sandbox.ReadUsage());
        }
    }

    /// <summary>Closes the training when the real session ends (sign-out or idle timeout).</summary>
    public void CloseOpenWindow() => _open?.Close();
}
