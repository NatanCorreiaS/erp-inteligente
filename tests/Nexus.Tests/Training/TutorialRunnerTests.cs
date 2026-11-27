using Avalonia.Headless.XUnit;
using erp_inteligente.Models;
using erp_inteligente.Training;
using erp_inteligente.ViewModels;

namespace Nexus.Tests.Training;

/// <summary>The lessons over a real sandbox with a scripted AI: steps notice what the person did and move on.</summary>
public sealed class TutorialRunnerTests : IDisposable
{
    private readonly TrainingWorld _w = new();

    public void Dispose() => _w.Dispose();

    private static async Task SendAsync(TutorialRunner runner, string request)
    {
        var nexus = Assert.IsType<NexusViewModel>(runner.Shell.CurrentPage);
        nexus.Draft = request;
        await nexus.SendCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task Asking_the_nexus_and_confirming_walks_the_lesson_and_leaves_the_real_warehouse_alone()
    {
        var movements = _w.RealCount("SELECT COUNT(*) FROM stock_movements");
        using var session = await _w.StartAsync("op.joao", "nexus-pedir");
        var runner = session.Runner;
        Assert.Equal("nexus-pedir", runner.Lesson.Id);
        Assert.Equal(TutorialCatalog.Operator, runner.Persona);

        // The Nexus is already open: the step is shown as done and waits for Próximo.
        Assert.Equal(TutorialAnchors.Nav("Nexus"), runner.Anchor);
        Assert.True(runner.IsStepDone);
        Assert.Equal("Próximo", runner.NextLabel);
        await runner.NextCommand.ExecuteAsync(null);

        Assert.Equal(TutorialAnchors.NexusRequest, runner.Anchor);
        Assert.Equal("Pular", runner.NextLabel);
        runner.UseSuggestionCommand.Execute(null);
        var nexus = Assert.IsType<NexusViewModel>(runner.Shell.CurrentPage);
        Assert.Equal(TutorialCatalog.MoveRequest, nexus.Draft);

        _w.ScriptProposal(TrainingWorld.MoveStep);
        await nexus.SendCommand.ExecuteAsync(null);
        await TrainingWorld.WaitForAsync(() => runner.Step.Title == "Confira a proposta", "the proposal step");
        Assert.NotNull(nexus.PendingProposal);

        await nexus.PendingProposal!.ConfirmCommand.ExecuteAsync(null);
        await TrainingWorld.WaitForAsync(() => runner.Anchor == TutorialAnchors.Nav("Stock"), "the executed step");
        Assert.Contains(runner.Shell.Plans.Plans, p => p.Status == PlanStatus.Executed);

        await runner.Shell.NavigateCommand.ExecuteAsync(TrainingWorld.Item(runner.Shell, "Stock"));
        await TrainingWorld.WaitForAsync(() => runner.Step.Title == "Saldo atualizado", "the last step");
        Assert.Equal("Concluir lição", runner.NextLabel);
        Assert.Empty(_w.Completed);
        await runner.NextCommand.ExecuteAsync(null);

        Assert.Equal(["nexus-pedir"], _w.Completed);
        Assert.Equal("nexus-perguntar", runner.Lesson.Id);
        Assert.Equal(movements, _w.RealCount("SELECT COUNT(*) FROM stock_movements"));
        Assert.Equal(0, _w.RealCount("SELECT COUNT(*) FROM plans WHERE created_at >= '2026-11-07'"));
    }

    [AvaloniaFact]
    public async Task Critical_lesson_switches_from_the_operator_to_the_supervisor_who_approves()
    {
        using var session = await _w.StartAsync("sup.maria", "critica");
        var runner = session.Runner;
        var shells = 0;
        runner.ShellChanged += (_, _) => shells++;

        // The supervisor's own persona is Maria, but the lesson starts as João.
        Assert.Equal(TutorialCatalog.Operator, runner.Persona);
        Assert.Same(runner.Shell, session.Shell);
        Assert.Equal("João (operador)", session.Shell.UserName);
        await runner.NextCommand.ExecuteAsync(null);
        await runner.NextCommand.ExecuteAsync(null);
        Assert.Equal(TutorialCatalog.CriticalRequest, runner.Suggestion);

        _w.ScriptProposal(TrainingWorld.CancelStep);
        await SendAsync(runner, TutorialCatalog.CriticalRequest);
        await TrainingWorld.WaitForAsync(() => runner.Step.Title == "Confirme", "the confirm step");
        var nexus = Assert.IsType<NexusViewModel>(runner.Shell.CurrentPage);
        await nexus.PendingProposal!.ConfirmCommand.ExecuteAsync(null);
        await TrainingWorld.WaitForAsync(() => runner.Step.Title == "Aguardando aprovação", "the waiting step");
        Assert.Contains(runner.Shell.Plans.Plans, p => p.Status == PlanStatus.PendingApproval);

        await runner.NextCommand.ExecuteAsync(null);
        Assert.Equal(TutorialCatalog.Supervisor, runner.Persona);
        Assert.Equal(1, shells);
        Assert.Same(runner.Shell, session.Shell);
        Assert.Equal("Maria (supervisora)", session.Shell.UserName);
        Assert.Equal("sup.maria", _w.Sandbox.Host.Session.Current.Username);

        await runner.Shell.NavigateCommand.ExecuteAsync(TrainingWorld.Item(runner.Shell, "Plans"));
        await TrainingWorld.WaitForAsync(() => runner.Step.Title == "Aceite ou rejeite", "the decision step");
        var plans = Assert.IsType<PlansViewModel>(runner.Shell.CurrentPage);
        await TrainingWorld.WaitForAsync(() => plans.SelectedBoard is not null, "the pending plan");
        await plans.SelectedBoard!.AcceptCommand.ExecuteAsync(null);
        await TrainingWorld.WaitForAsync(() => runner.Step.Title == "Registrado", "the closing step");

        await runner.NextCommand.ExecuteAsync(null);
        Assert.Equal(["critica"], _w.Completed);
        Assert.Equal(TutorialCatalog.Supervisor, runner.Persona);
    }

    [AvaloniaFact]
    public async Task Skipping_going_back_and_finishing()
    {
        using var session = await _w.StartAsync("aud.lucia");
        var runner = session.Runner;
        var lessons = TutorialCatalog.For(_w.Sandbox.Host.Session.Current);
        Assert.Equal(lessons[0].Id, runner.Lesson.Id);
        Assert.False(runner.CanGoBack);
        Assert.False(runner.BackCommand.CanExecute(null));

        await runner.NextCommand.ExecuteAsync(null);
        Assert.True(runner.CanGoBack);
        Assert.Equal($"2 de {lessons[0].Steps.Count}", runner.StepCounter);
        await runner.BackCommand.ExecuteAsync(null);
        Assert.Equal($"1 de {lessons[0].Steps.Count}", runner.StepCounter);
        Assert.StartsWith($"Lição 1 de {lessons.Count}", runner.LessonText);

        var closed = 0;
        runner.CloseRequested += (_, _) => closed++;
        for (var i = 0; i < 200 && !runner.IsFinished; i++)
            await runner.NextCommand.ExecuteAsync(null);

        Assert.True(runner.IsFinished);
        Assert.Equal(lessons.Select(l => l.Id), _w.Completed);
        Assert.Null(runner.Anchor);
        Assert.Equal("Fechar", runner.NextLabel);
        Assert.Equal("Treinamento concluído", runner.StepTitle);
        Assert.Equal(0, closed);
        await runner.NextCommand.ExecuteAsync(null);
        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public async Task Lessons_follow_the_role_and_every_highlighted_menu_button_exists_for_the_persona()
    {
        var offered = new Dictionary<string, IReadOnlyList<TutorialLesson>>();
        var menus = new Dictionary<string, HashSet<string>>();
        foreach (var persona in new[] { TutorialCatalog.Operator, TutorialCatalog.Supervisor, TutorialCatalog.Administrator, TutorialCatalog.Auditor })
        {
            var user = await _w.Sandbox.SignInAsync(persona);
            offered[persona] = TutorialCatalog.For(user);
            var shell = _w.Sandbox.Host.CreateShell(new FakeDialogService(), training: null, sandbox: true);
            menus[persona] = shell.MenuItems.Concat(shell.FooterItems).Select(i => i.IconKey).ToHashSet();
            Assert.Equal("Sair do treinamento", shell.SignOutLabel);
            Assert.DoesNotContain("Training", menus[persona]);
        }

        IEnumerable<string> Ids(string persona) => offered[persona].Select(l => l.Id);
        Assert.Equal(["janela", "consultar", "nexus-pedir", "nexus-perguntar", "novo-plano", "critica", "conhecimento"], Ids(TutorialCatalog.Operator));
        Assert.Contains("auditoria", Ids(TutorialCatalog.Supervisor));
        Assert.DoesNotContain("configuracoes", Ids(TutorialCatalog.Supervisor));
        Assert.Contains("configuracoes", Ids(TutorialCatalog.Administrator));
        Assert.DoesNotContain("nexus-pedir", Ids(TutorialCatalog.Administrator));
        Assert.Contains("auditoria", Ids(TutorialCatalog.Auditor));
        Assert.DoesNotContain("critica", Ids(TutorialCatalog.Auditor));

        foreach (var (persona, lessons) in offered)
            foreach (var lesson in lessons)
                foreach (var step in lesson.Steps.Where(s => s.Anchor?.StartsWith("nav:", StringComparison.Ordinal) == true))
                    Assert.True(menus[step.Persona ?? persona].Contains(step.Anchor![4..]),
                        $"{lesson.Id} / {step.Title}: {step.Anchor} is not in the menu of {step.Persona ?? persona}");

        Assert.Equal(TutorialCatalog.All.Count, TutorialCatalog.All.Select(l => l.Id).Distinct().Count());
        Assert.All(TutorialCatalog.All, l => Assert.Matches("^[a-z][a-z-]*$", l.Id));
        Assert.All(TutorialCatalog.All.SelectMany(l => l.Steps), s => Assert.True(s.Suggestion is null || s.Anchor == TutorialAnchors.NexusRequest));
    }
}
