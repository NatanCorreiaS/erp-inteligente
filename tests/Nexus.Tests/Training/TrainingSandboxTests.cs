using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Operations;
using erp_inteligente.Models;
using erp_inteligente.Training;

namespace Nexus.Tests.Training;

/// <summary>The sandbox is a separate, throwaway database; the real system only keeps audit events and token usage.</summary>
public sealed class TrainingSandboxTests : IDisposable
{
    private readonly TrainingWorld _w = new();

    public void Dispose() => _w.Dispose();

    [Fact]
    public async Task Sandbox_is_another_database_with_the_seed_and_its_folder_goes_away()
    {
        var folder = _w.Sandbox.DirectoryPath;
        Assert.NotEqual(_w.Real.Host.Database.Path, _w.Sandbox.Host.Database.Path);
        Assert.StartsWith(folder, _w.Sandbox.Host.Database.Path);

        var joao = await _w.Sandbox.SignInAsync(TutorialCatalog.Operator);
        Assert.Equal("operador", joao.RoleName);
        Assert.Equal(joao, _w.Sandbox.Host.Session.Current);
        var maria = await _w.Sandbox.SignInAsync(TutorialCatalog.Supervisor);
        Assert.True(maria.Has(Permissions.PlanApprove));
        Assert.Equal(maria, _w.Sandbox.Host.Session.Current);

        _w.Sandbox.Dispose();
        Assert.False(Directory.Exists(folder));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => _w.Sandbox.SignInAsync(TutorialCatalog.Operator));
    }

    [Theory]
    [InlineData("svc.ia")]
    [InlineData("ninguem")]
    [InlineData("op.joao' OR '1'='1")]
    public async Task Only_seed_users_that_can_sign_in_become_a_persona(string username) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.Sandbox.SignInAsync(username));

    [Fact]
    public void The_provider_key_is_never_copied_into_the_sandbox()
    {
        _w.Real.Host.LlmSettings.SetKey("sk-test-0123456789abcdef");
        using var sandbox = TrainingSandbox.Create(_w.Real.Host.Llm, TimeProvider.System);

        Assert.True(_w.Real.Host.LlmSettings.IsConfigured);
        Assert.False(sandbox.Host.LlmSettings.IsConfigured);
        Assert.Same(_w.Real.Host.Llm, sandbox.Host.Llm);
    }

    [Theory]
    [InlineData("operador", TutorialCatalog.Operator)]
    [InlineData("supervisor", TutorialCatalog.Supervisor)]
    [InlineData("administrador", TutorialCatalog.Administrator)]
    [InlineData("auditor", TutorialCatalog.Auditor)]
    [InlineData("papel_novo", TutorialCatalog.Operator)]
    public void Each_role_trains_as_the_seed_user_of_that_role(string role, string persona) =>
        Assert.Equal(persona, TutorialCatalog.PersonaFor(role));

    [Fact]
    public async Task Training_is_audited_and_its_tokens_are_charged_to_the_person()
    {
        var real = await _w.Real.LoginAsync("op.joao");
        var persona = await _w.Sandbox.SignInAsync(TutorialCatalog.Operator);
        var log = _w.Real.Host.TrainingLog;
        var lesson = TutorialCatalog.All[0];

        log.Started(real, TutorialCatalog.Operator);
        Assert.Empty(log.CompletedLessons(real));
        log.LessonCompleted(real, lesson);
        log.LessonCompleted(real, lesson);
        _w.Sandbox.Host.UsageLog.Record(persona, new LlmUsageEntry(LlmUseRole.Planner,
            new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.StrongModel), 3, 12_000, 2_000, "proposals") { CacheReadTokens = 4_000 });
        _w.Sandbox.Host.UsageLog.Record(persona, new LlmUsageEntry(LlmUseRole.Matcher,
            new LlmChoice(LlmProviderKind.DeepSeek, LlmSettings.FastModel), 1, 500, 50, "no_match"));
        log.Ended(real, _w.Sandbox.ReadUsage());

        Assert.Equal([lesson.Id], log.CompletedLessons(real).Keys);
        Assert.Empty(log.CompletedLessons(await _w.Real.LoginAsync("sup.maria")));
        Assert.Equal(1, _w.RealCount("SELECT COUNT(*) FROM audit_log WHERE action = 'training.start' AND actor_id = 1"));
        Assert.Equal(1, _w.RealCount(
            "SELECT COUNT(*) FROM audit_log WHERE action = 'training.end' AND json_extract(details_json, '$.llm_tokens') = 14550"));
        Assert.Equal(2, _w.RealCount("SELECT COUNT(*) FROM llm_usage WHERE user_id = 1"));
        Assert.Equal(1, _w.RealCount(
            "SELECT COUNT(*) FROM llm_usage WHERE role = 'planner' AND calls = 3 AND prompt_tokens = 12000 AND cache_read_tokens = 4000"));
        Assert.True(_w.Real.Host.VerifyAudit().IsValid);
    }
}
