using System.Text.Json;
using Avalonia.Threading;
using erp_inteligente.Models;
using erp_inteligente.Training;
using erp_inteligente.ViewModels;
using Nexus.Tests.Core.Engine;
using Nexus.Tests.Fakes;

namespace Nexus.Tests.Training;

/// <summary>
/// A real app (the "real warehouse") plus a training sandbox whose AI is scripted: no test here reaches the provider.
/// </summary>
public sealed class TrainingWorld : IDisposable
{
    public TrainingWorld()
    {
        Sandbox = TrainingSandbox.Create(Llm, TimeProvider.System);
    }

    public TestApp Real { get; } = new();
    public ScriptedLlmProvider Llm { get; } = new();
    public TrainingSandbox Sandbox { get; }
    public List<string> Completed { get; } = [];

    public async Task<TrainingSessionViewModel> StartAsync(string realUsername, string? lessonId = null)
    {
        var user = await Real.LoginAsync(realUsername);
        return await TrainingSessionViewModel.StartAsync(Sandbox, user, new FakeDialogService(), l => Completed.Add(l.Id), lessonId);
    }

    /// <summary>What the scripted model answers to a request: one proposal, then the closing text.</summary>
    public void ScriptProposal(string step) =>
        Llm.Call("propose_plan", JsonSerializer.Serialize(new
        {
            title = "Plano do treinamento",
            rationale = "Pedido do operador.",
            assumptions = Array.Empty<string>(),
            plan = "{\"dsl_version\":\"1.0\",\"intent\":\"treinamento\",\"steps\":[" + step + "]}",
        })).Reply("Proposta pronta.");

    public static string MoveStep => EngineHarness.Move("ABC-123", 10, "A-01-01", "A-02-03");

    public static string CancelStep =>
        "{\"op\":\"cancel_outbound_order\",\"args\":{\"order_number\":\"PED-FICT-5002\",\"reason\":\"Cliente desistiu\"}}";

    public long RealCount(string sql)
    {
        using var connection = Real.Host.Database.OpenApplication();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)command.ExecuteScalar()!;
    }

    public static async Task WaitForAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition(), "Timed out waiting for: " + what);
    }

    public static NavigationItem Item(ShellViewModel shell, string key) =>
        shell.MenuItems.Concat(shell.FooterItems).Single(i => i.IconKey == key);

    public void Dispose()
    {
        Sandbox.Dispose();
        Real.Dispose();
    }
}
