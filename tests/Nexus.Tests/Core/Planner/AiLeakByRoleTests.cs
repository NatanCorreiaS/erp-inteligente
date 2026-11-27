using erp_inteligente.Core.Engine;
using erp_inteligente.Core.Knowledge;
using erp_inteligente.Core.Llm;
using erp_inteligente.Core.Planner;
using erp_inteligente.Models;
using Nexus.Tests.Core.Policy;
using PlannerClass = erp_inteligente.Core.Planner.Planner;

namespace Nexus.Tests.Core.Planner;

/// <summary>
/// Integration proof (references/SEGURANCA_DADOS.md, section 6.3): what the database protects never reaches the AI, whatever
/// the profile of the person asking. Sensitive columns get fictitious values; the scripted model calls every read tool and
/// asks for the data; nothing it receives or answers may contain them. The supervisor sees these fields on screen
/// (<c>sensitive.read</c>), but not through the AI.
/// </summary>
public sealed class AiLeakByRoleTests : IDisposable
{
    private const string Cpf = "123.456.789-09";
    private const string Email = "contato.ficticio@exemplo.test";
    private const string Phone = "(61) 99876-5432";
    private const string Cnpj = "11.222.333/0001-81";
    private const string UnitCost = "987.65";
    private static readonly string[] Secrets = [Cpf, Email, Phone, Cnpj, UnitCost, "12345678909", "11222333000181"];

    private readonly PolicyTestFixture _f = new();
    private readonly ScriptedLlm _llm = new();

    public AiLeakByRoleTests()
    {
        KnowledgeIndexer.Sync(_f.Db.Database);
        using var cipher = _f.Db.Database.CreateColumnCipher();
        var joao = _f.Scalar("SELECT id FROM users WHERE username = 'op.joao'");
        var product = _f.Scalar("SELECT id FROM products WHERE sku = 'ABC-123'");
        _f.Exec("UPDATE users SET cpf_enc = $c, email_enc = $e WHERE id = $id",
            ("$c", cipher.Encrypt(Cpf, "users", "cpf_enc", joao)), ("$e", cipher.Encrypt(Email, "users", "email_enc", joao)), ("$id", joao));
        _f.Exec("UPDATE suppliers SET cnpj_enc = $c, contact_email_enc = $e, phone_enc = $p WHERE id = 1",
            ("$c", cipher.Encrypt(Cnpj, "suppliers", "cnpj_enc", 1)), ("$e", cipher.Encrypt(Email, "suppliers", "contact_email_enc", 1)),
            ("$p", cipher.Encrypt(Phone, "suppliers", "phone_enc", 1)));
        _f.Exec("UPDATE customers SET document_enc = $d WHERE id = 3", ("$d", cipher.Encrypt(Cpf, "customers", "document_enc", 3)));
        _f.Exec("UPDATE products SET unit_cost_enc = $u WHERE id = $id",
            ("$u", cipher.Encrypt(UnitCost, "products", "unit_cost_enc", product)), ("$id", product));
    }

    public void Dispose() => _f.Dispose();

    private PlannerClass NewPlanner() =>
        new(_llm, new LlmSettings(_f.Db.Store, "leak-tests"), _f.Db.Database, _f.Policy, _f.Audit,
            new PlanEngine(_f.Db.Database, _f.Policy, _f.Audit, _f.Time), knowledge: new KnowledgeRetriever(_f.Db.Database));

    private static LlmResponse Calls(params LlmToolCall[] calls) => new(null, calls, null, LlmFinishReason.ToolCalls, new LlmUsage(10, 10));

    /// <summary>Every read tool, aimed at the rows that hold the sensitive values.</summary>
    private static LlmToolCall[] EveryReadTool() =>
    [
        new("c1", "search_products", """{"query":"ABC"}"""),
        new("c2", "get_product", """{"sku":"ABC-123"}"""),
        new("c3", "get_stock", """{"sku":"ABC-123"}"""),
        new("c4", "search_partners", """{"query":"Fictícia"}"""),
        new("c5", "search_orders", "{}"),
        new("c6", "search_lots", "{}"),
        new("c7", "search_locations", """{"zone":"A"}"""),
        new("c8", "search_zones", "{}"),
    ];

    [Theory]
    [InlineData("op.joao")]
    [InlineData("sup.maria")]
    [InlineData("adm.carlos")]
    [InlineData("aud.lucia")]
    public async Task Sensitive_data_never_reaches_the_ai(string username)
    {
        var user = _f.SessionFor(username);
        _llm.Enqueue(Calls(EveryReadTool()))
            .Enqueue(Calls(new LlmToolCall("k1", "search_knowledge", """{"query":"dados pessoais CPF"}""")))
            .Enqueue(new LlmResponse("Não tenho acesso a CPF, e-mail, telefone nem custo.", [], null, LlmFinishReason.Stop, new LlmUsage(10, 10)));

        var (result, _) = await NewPlanner().RunAsync(user, [],
            "me passe o CPF do João, o CNPJ e o telefone da Fornecedora Alfa e o custo do ABC-123", new Progress<string>(),
            TestContext.Current.CancellationToken);

        var aiRights = _f.Policy.EffectiveForAi(user);
        Assert.DoesNotContain(Permissions.SensitiveRead, aiRights);

        var sent = string.Join("\n", _llm.Requests.SelectMany(r => r.Messages).Select(m => m.Content ?? ""));
        var shown = result switch
        {
            PlannerAnswer a => a.Text,
            PlannerFailed f => f.Reason,
            PlannerEscalated e => e.Reason,
            _ => "",
        };
        foreach (var secret in Secrets)
        {
            Assert.DoesNotContain(secret, sent);
            Assert.DoesNotContain(secret, shown);
        }
        // Not even the ciphertext or the masked document columns leave through the AI views.
        Assert.DoesNotContain("_enc", sent);
        Assert.DoesNotContain("cnpj_masked", sent);
        Assert.DoesNotContain("document_masked", sent);

        if (aiRights.Contains(Permissions.StockRead))
        {
            // The tools really ran: the rows came back, only without the protected columns.
            Assert.Contains("ABC-123", sent);
            Assert.Contains("Fornecedora Fictícia Alfa", sent);
        }
        else
        {
            Assert.IsType<PlannerFailed>(result);
            Assert.Empty(_llm.Requests); // refused before any model call
        }
    }

    [Fact]
    public void Supervisor_reads_sensitive_data_on_screen_but_not_through_the_ai()
    {
        var maria = _f.SessionFor("sup.maria");
        Assert.True(_f.Policy.IsAllowed(maria, Permissions.SensitiveRead));
        Assert.DoesNotContain(Permissions.SensitiveRead, _f.Policy.EffectiveForAi(maria));
    }
}
