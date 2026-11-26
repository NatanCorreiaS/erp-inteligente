using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Knowledge;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Planner;
using erp_inteligente.Models;
using Nexus.Tests.Core.Policy;
using PlannerClass = erp_inteligente.Core.Planner.Planner;

namespace Nexus.Tests.Core.Planner;

/// <summary>
/// Complete AI audit trail (who asked what, what the AI read and what it answered) and the mandatory citation of the
/// procedure an answer is based on.
/// </summary>
public sealed class AiAuditTrailTests : IDisposable
{
    private readonly PolicyTestFixture _f = new();
    private readonly ScriptedLlm _llm = new();
    private readonly UserSession _joao;

    public AiAuditTrailTests()
    {
        KnowledgeIndexer.Sync(_f.Db.Database);
        _joao = _f.SessionFor("op.joao");
    }

    public void Dispose() => _f.Dispose();

    private PlannerClass NewPlanner() =>
        new(_llm, new LlmSettings(_f.Db.Store, "audit-trail-tests"), _f.Db.Database, _f.Policy, _f.Audit,
            new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time), knowledge: new KnowledgeRetriever(_f.Db.Database));

    private static LlmResponse Text(string text) => new(text, [], null, LlmFinishReason.Stop, new LlmUsage(10, 10));

    private static LlmResponse Calls(params LlmToolCall[] calls) => new(null, calls, null, LlmFinishReason.ToolCalls, new LlmUsage(10, 10));

    private Task<(PlannerResult Result, PlannerTurn? Turn)> Run(UserSession user, string request) =>
        NewPlanner().RunAsync(user, [], request, new Progress<string>(), TestContext.Current.CancellationToken);

    private List<(string Action, string Outcome, long? OnBehalfOf, string Details)> AiRows()
    {
        using var connection = _f.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT action, outcome, on_behalf_of, COALESCE(details_json, '') FROM audit_log WHERE action LIKE 'ai.%' ORDER BY id";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string, long?, string)>();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetInt64(2), reader.GetString(3)));
        return rows;
    }

    [Fact]
    public async Task Request_tools_and_answer_are_audited_in_order_for_the_user()
    {
        _llm.Enqueue(Calls(new LlmToolCall("c1", "get_stock", """{"sku":"ABC-123"}""")))
            .Enqueue(Text("Há 40 caixas do ABC-123 na DOCA-01."));
        await Run(_joao, "quanto tem do ABC-123?");

        var rows = AiRows();
        Assert.Equal("ai.query", rows[0].Action);
        Assert.Contains("quanto tem do ABC-123?", rows[0].Details);
        Assert.Contains(rows, r => r.Action == "ai.tool");
        var response = rows[^1];
        Assert.Equal("ai.response", response.Action);
        Assert.Equal("allowed", response.Outcome);
        Assert.Contains("\"result\":\"answer\"", response.Details);
        Assert.Contains("40 caixas do ABC-123", response.Details);
        Assert.All(rows, r => Assert.Equal(_joao.UserId, r.OnBehalfOf));
    }

    [Fact]
    public async Task Personal_data_in_the_request_and_answer_is_masked_in_the_audit()
    {
        _llm.Enqueue(Text("Não guardo CPF. O CPF 123.456.789-09 não é dado do armazém."));
        await Run(_joao, "anote o CPF 123.456.789-09 e o e-mail fulano@exemplo.test");

        var rows = AiRows();
        Assert.All(rows, r =>
        {
            Assert.DoesNotContain("123.456.789-09", r.Details);
            Assert.DoesNotContain("fulano@exemplo.test", r.Details);
        });
        Assert.Contains(rows, r => r.Action == "ai.query");
        Assert.Contains(rows, r => r.Action == "ai.response");
    }

    [Fact]
    public async Task Answer_from_a_procedure_gets_its_source_when_the_model_forgets()
    {
        _llm.Enqueue(Calls(new LlmToolCall("k1", "search_knowledge", """{"query":"armazenagem refrigerados zona"}""")))
            .Enqueue(Text("Refrigerados vão para a zona C."));
        var (result, _) = await Run(_joao, "para onde vão os refrigerados?");

        var answer = Assert.IsType<PlannerAnswer>(result);
        Assert.Matches(@"\nFonte: [A-Z]{3}-\d{2}", answer.Text);
        Assert.Contains("\"sources\":[\"knowledge/", AiRows()[^1].Details);
    }

    [Fact]
    public async Task Answer_that_already_cites_is_not_changed()
    {
        _llm.Enqueue(Calls(new LlmToolCall("k1", "search_knowledge", """{"query":"armazenagem refrigerados zona"}""")))
            .Enqueue(Text("Conforme o POP-03, refrigerados vão para a zona C."));
        var (result, _) = await Run(_joao, "para onde vão os refrigerados?");

        Assert.Equal("Conforme o POP-03, refrigerados vão para a zona C.", Assert.IsType<PlannerAnswer>(result).Text);
    }

    [Fact]
    public void Citation_is_not_added_without_procedures()
    {
        Assert.Equal("Há 40 caixas.", PlannerClass.CiteSources("Há 40 caixas.", []));
        Assert.Equal("ok", PlannerClass.CiteSources("ok", ["knowledge/nao-existe.md"]));
        Assert.Equal("Veja o pop-03.", PlannerClass.CiteSources("Veja o pop-03.", ["knowledge/03-armazenagem.md"]));
        Assert.Equal("Siga a regra.\nFonte: POP-03, POL-05.",
            PlannerClass.CiteSources("Siga a regra.", ["knowledge/03-armazenagem.md", "knowledge/05-fefo-validade.md", "knowledge/06-separacao.md"]));
    }

    [Fact]
    public async Task Every_profile_with_stock_read_is_audited_before_the_model_call()
    {
        var lucia = _f.SessionFor("aud.lucia");
        _llm.Enqueue(Text("Há 40 caixas."));
        await Run(lucia, "quanto tem do ABC-123?");

        var rows = AiRows();
        Assert.Equal(("ai.query", "allowed", (long?)lucia.UserId), (rows[0].Action, rows[0].Outcome, rows[0].OnBehalfOf));
        Assert.Equal("ai.response", rows[^1].Action);
    }
}
