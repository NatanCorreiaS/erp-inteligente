using System.Collections.Generic;
using System.Linq;
using erp_inteligente.Models;
using erp_inteligente.ViewModels;

namespace erp_inteligente.Training;

/// <summary>
/// The lessons of the guided training (references/UI.md, section 10). Texts are operational and in Portuguese; every
/// identifier they cite exists in <c>db/seed.sql</c>, which is the data of the sandbox.
/// </summary>
public static class TutorialCatalog
{
    public const string Operator = "op.joao";
    public const string Supervisor = "sup.maria";
    public const string Administrator = "adm.carlos";
    public const string Auditor = "aud.lucia";

    /// <summary>Requests that passed the evaluation set of references/IA.md, section 5.</summary>
    public const string MoveRequest = "mova 10 cx do ABC-123 do A-01-01 para o A-02-03";
    public const string ProcedureQuestion = "Quanto tempo um produto refrigerado pode ficar na doca?";
    public const string OutOfScopeRequest = "Qual a previsão do tempo para amanhã?";
    public const string CriticalRequest = "cancele o pedido de saída PED-FICT-5002 a pedido do cliente";

    private const string AiCost = " Este passo chama a IA de verdade e consome tokens da chave configurada.";

    /// <summary>The seed user a role trains as; an unknown role trains as the operator.</summary>
    public static string PersonaFor(string roleName) => roleName switch
    {
        "supervisor" => Supervisor,
        "administrador" => Administrator,
        "auditor" => Auditor,
        _ => Operator,
    };

    /// <summary>The lessons offered to a persona, in order.</summary>
    public static IReadOnlyList<TutorialLesson> For(UserSession persona) => All.Where(l => l.IsOfferedTo(persona)).ToList();

    public static IReadOnlyList<TutorialLesson> All { get; } =
    [
        new("janela", "A janela", "A coluna de menus, o título e o conteúdo de cada tela.", [],
        [
            new("Ambiente de treinamento",
                "Esta janela é uma cópia do Nexus com dados fictícios. Nada do que você fizer aqui altera o armazém real. " +
                "Em cada passo, a área destacada é a que você usa; o resto fica bloqueado. Use Próximo para avançar e Pular quando quiser seguir sem fazer o passo."),
            new("Quem está usando",
                "Aqui ficam o nome e o papel de quem está na sessão. No treinamento você usa um usuário fictício com o mesmo papel que o seu.",
                TutorialAnchors.User),
            new("Coluna de menus",
                "Cada botão abre uma tela. Só aparecem as telas que o seu papel pode usar. Clique em Configurações.",
                TutorialAnchors.Nav("Settings"), c => c.ActiveKey == "Settings"),
            new("Título e conteúdo",
                "O título mostra a tela atual. O conteúdo troca aqui, e a coluna de menus não muda.",
                TutorialAnchors.Title),
            new("Sessão",
                "A sessão real expira depois de 15 minutos sem uso, inclusive durante o treinamento. Aqui em Configurações você vê o usuário, o papel e o armazém.",
                TutorialAnchors.Content),
        ]),

        new("consultar", "Consultar", "Estoque, produtos, endereços e pedidos, com filtro.", [Permissions.StockRead],
        [
            new("Estoque", "As telas de consulta só leem dados. Clique em Estoque.",
                TutorialAnchors.Nav("Stock"), c => c.ActiveKey == "Stock"),
            new("Filtro",
                "Cada linha é um saldo: produto, endereço, lote e quantidades. Digite ABC no filtro para ver só os SKUs que começam assim.",
                TutorialAnchors.Content, c => c.Page is StockViewModel { FilterText.Length: >= 3 }),
            new("Totais",
                "A faixa acima da tabela soma o que está filtrado. Reservado é o que já está separado para um pedido; Disponível é o que sobra.",
                TutorialAnchors.Content),
            new("Endereços", "Clique em Endereços para ver a ocupação de cada posição do armazém.",
                TutorialAnchors.Nav("Locations"), c => c.ActiveKey == "Locations"),
            new("Pedidos", "Clique em Pedidos para ver as entradas esperadas e as saídas em aberto.",
                TutorialAnchors.Nav("Orders"), c => c.ActiveKey == "Orders"),
            new("Alterar dados",
                "Nenhuma tela de consulta altera dados. Toda alteração é um plano, pedido ao Nexus ou montado em Novo plano. É o assunto das próximas lições.",
                TutorialAnchors.Content),
        ]),

        new("nexus-pedir", "Pedir ao Nexus", "Um pedido em português vira um plano que você confere e confirma.", [Permissions.PlanSubmit],
        [
            new("Nexus", "O Nexus é a IA do sistema. Clique em Nexus.",
                TutorialAnchors.Nav("Nexus"), c => c.ActiveKey == "Nexus"),
            new("Escreva o pedido",
                "Peça como falaria com um colega, citando SKU, quantidade e endereços. Use o pedido sugerido ou escreva o seu e pressione Enter." + AiCost,
                TutorialAnchors.NexusRequest, c => c.Nexus is { IsBusy: true } || c.NexusProposed || c.NexusReplied,
                Suggestion: MoveRequest),
            new("A IA está trabalhando",
                "A linha de estado mostra o que a IA consulta. Ela só lê dados; quem grava é o motor de planos, depois da sua confirmação. Aguarde a proposta.",
                TutorialAnchors.Content, c => c.NexusProposed || c.NexusReplied),
            new("Confira a proposta",
                "O quadro mostra as operações, as suposições que a IA fez e o efeito simulado no estoque. Nada foi gravado ainda. " +
                "Se estiver certo, clique em Confirmar. Se a IA respondeu com uma pergunta em vez de um plano, responda ou use Pular.",
                TutorialAnchors.Content, c => c.PlanInLesson(p => p.Status is PlanStatus.Executed or PlanStatus.PendingApproval)),
            new("Plano executado",
                "O plano foi validado, simulado e executado numa transação. O resumo mostra o que mudou. Abra Estoque para conferir o saldo novo.",
                TutorialAnchors.Nav("Stock"), c => c.ActiveKey == "Stock"),
            new("Saldo atualizado",
                "Se você usou o pedido sugerido, 10 caixas do ABC-123 estão agora no A-02-03. O plano e quem o confirmou ficaram registrados em Planos e na auditoria.",
                TutorialAnchors.Content),
        ]),

        new("nexus-perguntar", "Perguntar ao Nexus", "Procedimentos do armazém, pedidos fora do escopo e os limites da IA.", [Permissions.PlanSubmit],
        [
            new("Volte ao Nexus", "A conversa anterior continua lá. Clique em Nexus.",
                TutorialAnchors.Nav("Nexus"), c => c.ActiveKey == "Nexus"),
            new("Pergunte sobre um procedimento",
                "A IA responde com base nos procedimentos do armazém. Envie a pergunta sugerida." + AiCost,
                TutorialAnchors.NexusRequest, c => c.Nexus is { IsBusy: true } || c.NexusReplied,
                Suggestion: ProcedureQuestion),
            new("Aguarde a resposta",
                "A IA procura nos procedimentos do armazém e responde com o que achou.",
                TutorialAnchors.Content, c => c.NexusReplied),
            new("Leia a resposta",
                "A resposta vem em texto simples. Confira sempre com o procedimento quando a decisão for importante.",
                TutorialAnchors.Content),
            new("Fora do escopo",
                "A IA só trata do armazém. Envie o pedido sugerido para ver a recusa." + AiCost,
                TutorialAnchors.NexusRequest, c => c.Nexus is { IsBusy: true } || c.NexusReplied,
                Suggestion: OutOfScopeRequest),
            new("Aguarde a resposta",
                "Um pedido fora do armazém não vira plano.",
                TutorialAnchors.Content, c => c.NexusReplied),
            new("Limites da IA",
                "A IA tem os mesmos direitos operacionais que você, e nunca mais que isso. Ela não aprova planos, não lê dados sensíveis, não gerencia usuários e não lê a auditoria. " +
                "Texto encontrado nos dados nunca é tratado como ordem.",
                TutorialAnchors.Content),
        ]),

        new("novo-plano", "Novo plano", "O mesmo plano, montado por formulário, sem IA.", [Permissions.PlanSubmit],
        [
            new("Novo plano", "Quando você já sabe a operação, pode montar o plano sem a IA. Clique em Novo plano.",
                TutorialAnchors.Nav("NewPlan"), c => c.ActiveKey == "NewPlan"),
            new("Monte e envie",
                "Escolha a operação, preencha os campos e adicione o passo. A simulação mostra o efeito antes de enviar. " +
                "Monte uma movimentação de 5 cx do ABC-123 do A-01-01 para o A-02-02 e envie, ou use Pular.",
                TutorialAnchors.Content, c => c.PlanInLesson(p => p.Status is PlanStatus.Executed or PlanStatus.PendingApproval or PlanStatus.Failed)),
            new("Planos", "Todo plano fica no histórico. Clique em Planos.",
                TutorialAnchors.Nav("Plans"), c => c.ActiveKey == "Plans"),
            new("Histórico",
                "A aba Todos lista os planos com solicitante, risco e status. Selecione um para ver o quadro completo.",
                TutorialAnchors.Content),
        ]),

        new("critica", "Operação crítica", "Um plano crítico espera a aprovação de um supervisor que não o pediu.", [Permissions.PlanSubmit],
        [
            new("Duas pessoas",
                "Ajuste de estoque, expedição e cancelamento de pedido são críticos: quem pede não aprova. " +
                "Nesta lição você começa como o operador João e depois vira a supervisora Maria.",
                Persona: Operator),
            new("Nexus", "Você agora é o João. Clique em Nexus.",
                TutorialAnchors.Nav("Nexus"), c => c.ActiveKey == "Nexus", Persona: Operator),
            new("Peça o cancelamento",
                "Envie o pedido sugerido." + AiCost,
                TutorialAnchors.NexusRequest, c => c.Nexus is { IsBusy: true } || c.NexusProposed || c.NexusReplied,
                Persona: Operator, Suggestion: CriticalRequest),
            new("Confirme",
                "Aguarde a proposta. Ela vem marcada como crítica. Confirmar aqui não executa: só envia o plano para aprovação. Clique em Confirmar.",
                TutorialAnchors.Content, c => c.PlanInLesson(p => p.Status == PlanStatus.PendingApproval),
                Persona: Operator),
            new("Aguardando aprovação",
                "O plano ficou parado. O João não pode aprová-lo. Clique em Próximo para continuar como a Maria.",
                TutorialAnchors.Content, Persona: Operator),
            new("Planos pendentes",
                "Você agora é a Maria, supervisora. O contador ao lado de Planos mostra o que espera a sua decisão. Clique em Planos.",
                TutorialAnchors.Nav("Plans"), c => c.ActiveKey == "Plans", Persona: Supervisor),
            new("Aceite ou rejeite",
                "Confira as operações e o efeito simulado. Aceitar executa o plano; Rejeitar pede um motivo, que fica na auditoria. Decida.",
                TutorialAnchors.Content,
                c => c.Plans.Any(p => p.DecidedAt >= c.LessonStartedAt && p.Status is PlanStatus.Executed or PlanStatus.Rejected or PlanStatus.Failed),
                Persona: Supervisor),
            new("Registrado",
                "A decisão, quem decidiu e quando ficam no plano e na auditoria. No uso real, o solicitante vê o resultado no próprio Nexus.",
                TutorialAnchors.Content, Persona: Supervisor),
        ]),

        new("conhecimento", "Skills, Memórias e Receitas", "O que a IA sabe do seu armazém e como isso é mantido.", [Permissions.StockRead],
        [
            new("Skills", "Skills são os procedimentos que a IA segue ao planejar. Clique em Skills.",
                TutorialAnchors.Nav("Skills"), c => c.ActiveKey == "Skills"),
            new("Leia uma skill",
                "Cada skill tem nome, descrição e as operações que usa. Todos leem; só o supervisor cria e edita, e cada edição vira uma versão.",
                TutorialAnchors.Content),
            new("Memórias", "Memórias são regras e preferências do armazém que a IA leva em conta. Clique em Memórias.",
                TutorialAnchors.Nav("Memory"), c => c.ActiveKey == "Memory"),
            new("Como nascem",
                "A IA registra uma memória quando você dita uma regra no Nexus, e o supervisor pode criar, corrigir e arquivar. Nada é apagado: o que sai é arquivado.",
                TutorialAnchors.Content),
            new("Receitas", "Depois de três planos iguais executados, o sistema sugere uma receita. Clique em Receitas.",
                TutorialAnchors.Nav("Recipes"), c => c.ActiveKey == "Recipes"),
            new("Sem IA",
                "Uma receita ativa executa o mesmo plano só com os valores que você informa, sem chamar a IA. O supervisor é quem promove uma candidata a receita.",
                TutorialAnchors.Content),
        ]),

        new("auditoria", "Auditoria e Custos de IA", "Quem fez o quê, e quanto a IA consumiu.", [Permissions.AuditRead],
        [
            new("Auditoria", "Clique em Auditoria.",
                TutorialAnchors.Nav("Audit"), c => c.ActiveKey == "Audit"),
            new("Eventos e cadeia",
                "Cada evento guarda quem, o quê e o resultado, e é encadeado por hash com o anterior. A tela verifica a cadeia inteira ao abrir. " +
                "Use os filtros por categoria e por problema.",
                TutorialAnchors.Content),
            new("Custos de IA", "Clique em Custos de IA.",
                TutorialAnchors.Nav("Costs"), c => c.ActiveKey == "Costs"),
            new("Consumo",
                "A tela mostra pedidos, a parte resolvida por receita, tokens por pedido e o custo estimado por modelo. " +
                "O que a IA consumir neste treinamento aparece na tela Custos de IA real, no seu nome.",
                TutorialAnchors.Content),
        ]),

        new("configuracoes", "Configurações", "Chave do provedor, modelo por papel e backups.", [Permissions.UserManage],
        [
            new("Configurações", "Clique em Configurações.",
                TutorialAnchors.Nav("Settings"), c => c.ActiveKey == "Settings"),
            new("O que o administrador mantém",
                "No sistema real, esta tela guarda as chaves da DeepSeek e da OpenRouter no cofre do sistema operacional, escolhe o modelo de planejamento e o de receitas, " +
                "e faz e restaura backups criptografados. No treinamento a chave não é mostrada nem editada.",
                TutorialAnchors.Content),
            new("Auditado",
                "Salvar ou remover a chave e trocar de modelo geram eventos de auditoria, sem o valor da chave.",
                TutorialAnchors.Content),
        ]),
    ];
}
