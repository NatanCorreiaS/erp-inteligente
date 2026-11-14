using System;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using erp_inteligente.Models;
using erp_inteligente.Services;
using erp_inteligente.Training;

namespace erp_inteligente.ViewModels;

/// <summary>
/// The training window (references/UI.md, section 10): the sandbox shell of the current persona and the lesson being
/// walked through. Created by <see cref="StartAsync"/>; disposing it ends the lessons, not the sandbox.
/// </summary>
public sealed partial class TrainingSessionViewModel : ViewModelBase, IDisposable
{
    private TrainingSessionViewModel(TutorialRunner runner)
    {
        Runner = runner;
        Shell = runner.Shell;
    }

    public const string Banner = "TREINAMENTO · dados fictícios · nada aqui altera o armazém real";

    public TutorialRunner Runner { get; }

    /// <summary>The sandbox shell shown in the window; replaced when a lesson changes the persona.</summary>
    [ObservableProperty]
    public partial ShellViewModel Shell { get; private set; }

    /// <summary>Raised when the person leaves the training (Sair, Fechar or the sign-out of the sandbox shell).</summary>
    public event EventHandler? CloseRequested;

    /// <summary>
    /// Signs the persona of <paramref name="realUser"/>'s role into the sandbox and starts the lessons of that role.
    /// </summary>
    /// <param name="lessonCompleted">Records a completed lesson in the real system.</param>
    public static async Task<TrainingSessionViewModel> StartAsync(TrainingSandbox sandbox, UserSession realUser, IDialogService dialogs,
        Action<TutorialLesson> lessonCompleted, string? startLessonId = null)
    {
        ArgumentNullException.ThrowIfNull(sandbox);
        ArgumentNullException.ThrowIfNull(realUser);
        var personaName = TutorialCatalog.PersonaFor(realUser.RoleName);
        var persona = await sandbox.SignInAsync(personaName);
        var lessons = TutorialCatalog.For(persona);
        var start = Math.Max(0, lessons.Select((lesson, index) => (lesson, index)).FirstOrDefault(x => x.lesson.Id == startLessonId).index);

        TrainingSessionViewModel? session = null;
        async Task<ShellViewModel> Switch(string username)
        {
            await sandbox.SignInAsync(username);
            var shell = CreateShell(sandbox, dialogs);
            session?.Attach(shell);
            return shell;
        }

        var runner = new TutorialRunner(lessons, personaName, CreateShell(sandbox, dialogs), Switch, sandbox.Host.Time, lessonCompleted, start);
        session = new TrainingSessionViewModel(runner);
        session.Attach(runner.Shell);
        runner.CloseRequested += (_, _) => session.Leave();
        await runner.StartAsync();
        return session;
    }

    private static ShellViewModel CreateShell(TrainingSandbox sandbox, IDialogService dialogs) =>
        sandbox.Host.CreateShell(dialogs, training: null, sandbox: true);

    private void Attach(ShellViewModel shell)
    {
        // Sair of the sandbox shell leaves the training; it never signs the real user out.
        shell.SignOutRequested += (_, _) => Leave();
        Shell = shell;
    }

    /// <summary>Leaves the training; the window closes and the sandbox is thrown away.</summary>
    [RelayCommand]
    private void Leave() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Runner.Dispose();
}
