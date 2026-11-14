using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using erp_inteligente;
using erp_inteligente.Models;
using erp_inteligente.Services;
using erp_inteligente.Training;
using erp_inteligente.ViewModels;
using erp_inteligente.Views;
using Nexus.Tests.Core;

namespace Nexus.Tests.Training;

/// <summary>The Treinamento screen, the training window and its overlay over the real XAML.</summary>
public sealed class TrainingUiTests
{
    private sealed class FakeTraining(params TrainingLessonStatus[] lessons) : ITrainingService
    {
        public List<string?> Runs { get; } = [];
        public Exception? Failure { get; set; }
        public IReadOnlyList<TrainingLessonStatus> Statuses { get; set; } = lessons;

        public IReadOnlyList<TrainingLessonStatus> Lessons(UserSession user) => Statuses;

        public Task RunAsync(UserSession user, string? startLessonId = null)
        {
            Runs.Add(startLessonId);
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
    }

    private static readonly DateTimeOffset When = new(2026, 11, 14, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Screen_lists_the_lessons_and_starts_at_the_first_one_not_done()
    {
        using var app = new TestApp();
        var user = await app.LoginAsync("op.joao");
        var training = new FakeTraining(new("a", "A janela", "Menus.", When), new("b", "Consultar", "Filtros.", null), new("c", "Nexus", "IA.", null));
        var vm = new TrainingViewModel(user, training);
        await vm.OnActivatedAsync();

        Assert.Equal("Treinamento", vm.Title);
        Assert.Equal(["1", "2", "3"], vm.Lessons.Select(l => l.Number));
        Assert.StartsWith("Concluída em ", vm.Lessons[0].StateText);
        Assert.Equal("Não feita", vm.Lessons[1].StateText);
        Assert.Equal("1 de 3 lições concluídas", vm.ProgressText);
        Assert.Equal("Continuar da lição 2", vm.StartLabel);
        Assert.False(vm.StartLessonCommand.CanExecute(null));

        await vm.StartCommand.ExecuteAsync(null);
        vm.SelectedLesson = vm.Lessons[2];
        await vm.StartLessonCommand.ExecuteAsync(null);
        Assert.Equal(["b", "c"], training.Runs);
        Assert.False(vm.IsWorking);

        training.Statuses = training.Statuses.Select(s => s with { CompletedAt = When }).ToList();
        training.Failure = new InvalidOperationException("sk-secret");
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("Refazer o treinamento", vm.StartLabel);
        Assert.True(vm.HasError);
        Assert.DoesNotContain("sk-secret", vm.ErrorText);
    }

    [AvaloniaTheory]
    [InlineData("op.joao")]
    [InlineData("sup.maria")]
    [InlineData("adm.carlos")]
    [InlineData("aud.lucia")]
    public async Task Every_role_has_the_menu_item_and_the_screen_renders(string username)
    {
        using var app = new TestApp();
        await app.LoginAsync(username);
        var window = new MainWindow();
        var shell = app.Host.CreateShell(new FakeDialogService(), new TrainingLauncher(app.Host, window, () => { }));
        window.DataContext = shell;
        window.Show();

        Assert.Equal(["Training", "Settings"], shell.FooterItems.Select(i => i.IconKey));
        await shell.NavigateCommand.ExecuteAsync(shell.FooterItems[0]);
        Dispatcher.UIThread.RunJobs();

        var view = Assert.Single(window.GetVisualDescendants().OfType<TrainingView>());
        var page = Assert.IsType<TrainingViewModel>(shell.CurrentPage);
        Assert.NotEmpty(page.Lessons);
        Assert.Equal("Iniciar treinamento", page.StartLabel);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Iniciar treinamento");
        Assert.DoesNotContain(app.Host.CreateShell(new FakeDialogService()).FooterItems, i => i.IconKey == "Training");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Overlay_highlights_the_control_of_every_step_of_every_lesson()
    {
        using var world = new TrainingWorld();
        using var session = await world.StartAsync("sup.maria");
        var window = new TrainingWindow { DataContext = session };
        window.Show();
        var runner = session.Runner;
        var steps = 0;

        while (!runner.IsFinished)
        {
            // Skipped steps leave the screen where it was: open the one the step is about.
            if (runner.Anchor == TutorialAnchors.NexusRequest)
                await runner.Shell.NavigateCommand.ExecuteAsync(TrainingWorld.Item(runner.Shell, "Nexus"));
            Dispatcher.UIThread.RunJobs();
            window.UpdateOverlay();

            var where = $"{runner.Lesson.Id} / {runner.Step.Title}";
            if (runner.Anchor is null)
                Assert.True(window.HighlightedArea is null, where);
            else
                Assert.True(window.HighlightedArea is { Width: > 8, Height: > 8 }, $"{where}: nothing highlighted for {runner.Anchor}");
            var callout = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "Callout");
            var bounds = new Rect(new Point(Canvas.GetLeft(callout), Canvas.GetTop(callout)), callout.Bounds.Size);
            Assert.True(new Rect(window.ClientSize).Contains(bounds), $"{where}: the text panel is outside the window");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == runner.StepTitle);
            Assert.Same(runner.Shell, window.GetVisualDescendants().OfType<ShellView>().Single().DataContext);

            steps++;
            await runner.NextCommand.ExecuteAsync(null);
        }

        Assert.Equal(TutorialCatalog.For(world.Sandbox.Host.Session.Current).Sum(l => l.Steps.Count), steps);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == TrainingSessionViewModel.Banner);
        Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Treinamento concluído");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Only_the_highlighted_control_takes_the_pointer()
    {
        using var world = new TrainingWorld();
        using var session = await world.StartAsync("op.joao", "consultar");
        var window = new TrainingWindow { DataContext = session };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateOverlay();
        var runner = session.Runner;
        Assert.Equal(TutorialAnchors.Nav("Stock"), runner.Anchor);
        Assert.Equal("Nexus", runner.Shell.CurrentPage!.Title);

        var scrim = window.GetVisualDescendants().OfType<TutorialScrim>().Single();
        var hole = window.HighlightedArea!.Value;
        Assert.False(scrim.HitTest(hole.Center));
        Assert.True(scrim.HitTest(new Point(hole.Center.X, hole.Bottom + 30)));

        // Another menu button, outside the hole: the click dies on the dim layer.
        var outside = new Point(hole.Center.X, hole.Bottom + 30);
        window.MouseDown(outside, MouseButton.Left);
        window.MouseUp(outside, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Nexus", runner.Shell.CurrentPage!.Title);

        window.MouseDown(hole.Center, MouseButton.Left);
        window.MouseUp(hole.Center, MouseButton.Left);
        await TrainingWorld.WaitForAsync(() => runner.Shell.CurrentPage!.Title == "Estoque", "the highlighted button to navigate");
        await TrainingWorld.WaitForAsync(() => runner.Step.Title == "Filtro", "the step to notice the navigation");

        session.LeaveCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);
    }

    [Theory]
    [InlineData(100, 100, 200, 40, 312, 100)]   // room on the right
    [InlineData(900, 100, 290, 40, 508, 100)]   // no room on the right: left
    [InlineData(300, 16, 884, 40, 300, 68)]     // too wide: below
    [InlineData(300, 600, 884, 184, 300, 388)]  // too wide and low: above
    [InlineData(250, 60, 934, 724, 788, 76)]    // fills the window: top right corner inside
    public void Text_panel_goes_beside_the_highlight_and_stays_inside(double x, double y, double w, double h, double left, double top)
    {
        var at = TrainingWindow.Place(new Size(380, 200), new Rect(x, y, w, h), new Rect(0, 0, 1200, 800));
        Assert.Equal(new Point(left, top), at);
        Assert.Equal(new Point(410, 300), TrainingWindow.Place(new Size(380, 200), null, new Rect(0, 0, 1200, 800)));
    }

    [AvaloniaFact]
    public async Task Training_window_closes_when_the_real_session_expires()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
        using var app = new TestApp(time);
        var desktop = new FakeDesktop();
        new AppController(desktop, app.Host, TimeSpan.FromMilliseconds(10)).Start();
        var login = Assert.IsType<LoginViewModel>(Assert.IsType<LoginWindow>(desktop.MainWindow).DataContext);
        login.Username = "op.joao";
        login.Password = TestApp.DemoPassword;
        await login.LoginCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        var main = Assert.IsType<MainWindow>(desktop.MainWindow);
        var shell = Assert.IsType<ShellViewModel>(main.DataContext);

        await shell.NavigateCommand.ExecuteAsync(shell.FooterItems.Single(i => i.IconKey == "Training"));
        var page = Assert.IsType<TrainingViewModel>(shell.CurrentPage);
        var run = page.StartCommand.ExecuteAsync(null);
        await TrainingWorld.WaitForAsync(() => main.OwnedWindows.OfType<TrainingWindow>().Any(), "the training window");
        var training = main.OwnedWindows.OfType<TrainingWindow>().Single();
        Assert.Equal(1, Count(app, "SELECT COUNT(*) FROM audit_log WHERE action = 'training.start'"));

        time.Advance(TimeSpan.FromMinutes(16));
        await TrainingWorld.WaitForAsync(() => desktop.MainWindow is LoginWindow, "the login after the idle timeout");
        await run;

        Assert.False(training.IsVisible);
        Assert.False(app.Host.Session.IsActive);
        Assert.Equal(1, Count(app, "SELECT COUNT(*) FROM audit_log WHERE action = 'training.end'"));
        Assert.True(app.Host.VerifyAudit().IsValid);
    }

    private static long Count(TestApp app, string sql)
    {
        using var connection = app.Host.Database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    private sealed class FakeDesktop : IWindowHost
    {
        public Window? MainWindow { get; set; }
        public void Shutdown() { }
    }
}
