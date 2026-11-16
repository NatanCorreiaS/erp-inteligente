<style>
@page { size: A4; margin: 12mm 14mm; }
body { font-size: 9pt; line-height: 1.35; }
h1 { font-size: 16pt; margin: 0 0 4px; }
h2 { font-size: 11.5pt; margin: 11px 0 4px; }
h3 { font-size: 10pt; margin: 8px 0 3px; }
p, ul { margin: 3px 0; }
ul { padding-left: 16px; }
table { font-size: 8.5pt; margin: 4px 0; }
th, td { padding: 2px 5px; }
</style>

# Relatório de andamento: Nexus

**Grupo:** ERP INTELIGENTE · **Repositório:** https://github.com/NatanCorreiaS/erp-inteligente · **Data:** 16/11/2026

| Integrante | Matrícula |
|------------|-----------|
| Natan Correia da Silva | 231071692 |
| Clara Gabryellen Paixão Aderno | 231070720 |
| Samuel Santos César | 232070317 |
| Rayan Santos Silva | 231070220 |

Todos os integrantes cursam as duas disciplinas: **Projeto de Inteligência Artificial** e **Segurança e Otimização de BD**.

## 1. O projeto

O Nexus é um ERP de armazém, em app desktop (C# e Avalonia), em que o operador pede em português e uma IA monta o plano. A IA só lê dados e descreve a alteração em um plano JSON. Um motor de código valida, simula e executa o plano numa transação, e quem pediu confirma antes de gravar. Operações críticas pedem a aprovação de outro supervisor. Todos os dados são fictícios.

## 2. O que já funciona

**IA**
- Planner da DeepSeek (`deepseek-flash`) com 8 ferramentas de leitura, skills carregadas sob demanda e memórias por armazém.
- RAG sobre 22 procedimentos fictícios, com busca por texto (BM25) e, opcionalmente, por significado (modelo local).
- Receitas: planos repetidos viram atalhos que rodam sem LLM.
- Matcher com fine-tuning (Qwen2.5-1.5B com LoRA) e defesas contra prompt injection.
- Treinamento guiado em ambiente fictício.

**Banco de dados**
- SQLite com SQLCipher: o arquivo inteiro é cifrado e as chaves ficam no cofre do sistema.
- Colunas sensíveis (CPF, e-mail, custos) cifradas com AES-GCM, e o CNPJ com uma versão mascarada.
- Cinco papéis com permissões `ALLOW` e `DENY` e escopo por armazém; triggers que impedem apagar histórico.
- Auditoria encadeada por hash (SHA-256), verificada ao abrir o app.
- 20 operações no motor de planos e esquema na versão 10, atualizado por migrações.

## 3. Medições

**IA** (130 pedidos fictícios de `tools/eval`, `deepseek-flash`, 16/11)

| Conjunto | Resultado |
|----------|----------:|
| Planejamento com as operações esperadas (50 pedidos) | 96% |
| Plano válido na 1ª tentativa | 90% |
| Recusa de pedidos fora do escopo (30) | 100% |
| Pedidos proativos ou sem direito (10) | 100% |
| Perguntas sobre procedimentos: conteúdo correto / com a fonte citada (40) | 82,5% / 35% |
| RAG: documento certo entre os 4 primeiros / em 1º lugar | 100% / 80% |
| Tokens e tempo por pedido (média) | 24.261 e 7,4 s |

O planejamento leva 11,7 s e 39 mil tokens por pedido; um pedido recusado leva 2,9 s. Contando todos os tokens como entrada sem cache, o teto é de US$ 0,007 por pedido (US$ 0,30 por milhão). Com receitas, a estimativa é de US$ 4,39 por 1.000 usos.

Fine-tuning do Matcher: 500 pedidos sintéticos (400 de treino, 100 de teste), 3 h de treino numa GTX 1660. Teste gerado: 100%. Em 40 pedidos escritos à mão: 80% de acerto exato, e 92,5% com a checagem que recusa valor ausente do pedido.

**Banco de dados** (1.000.000 de movimentos, 300 mil saldos, 100 mil lotes e 5.000 produtos)

| Consulta | Plano antes | Plano depois | Antes | Depois |
|----------|-------------|--------------|------:|-------:|
| Kardex de um produto | `SCAN` de 1 milhão de linhas | `SEARCH` por índice | 72,4 ms | 0,39 ms |
| Lotes a vencer (FEFO) | `SCAN` de 300 mil linhas | `SEARCH` com índice de cobertura | 33,8 ms | 9,4 ms |

Os testes automáticos passam: 115 do banco, 1.446 do app e 35 das ferramentas. Isso inclui a suíte de 25 ataques de prompt injection, com 0% de sucesso e 100% de detecção.

## 4. Integração entre IA e banco

- A IA consulta o banco por uma conexão só de leitura, que enxerga apenas as views `v_ai_*`, sem os dados sensíveis.
- Ela age com a interseção dos direitos dela com os de quem pediu. Um operador sem `stock.adjust` não consegue o ajuste nem pela IA: o motor recusa o passo.
- A IA não aprova, não lê a auditoria e não gere usuários.
- A auditoria registra quem pediu (`ai.query`), o que a IA consultou (`ai.tool`) e cada plano proposto, com data e hora. CPF, CNPJ, e-mail e chaves saem mascarados antes do hash.

## 5. O que mudou na arquitetura desde 27/10

- O motor de planos foi implementado: 20 operações, simulação antes de gravar e aprovação por outro supervisor nas críticas.
- O n8n não é usado. O laço de ferramentas fica dentro do app.
- O RAG ganhou busca híbrida e memórias com confiança ajustada pelos resultados.
- A DeepSeek virou o único provedor externo (14/11), e o `deepseek-flash` virou o padrão.
- Entraram receitas, backup cifrado diário, painéis de auditoria e de custos, e o treinamento guiado.

## 6. O que falta

- O prompt ainda não exige a citação da fonte: só 35% das respostas citam o documento.
- O Matcher local não foi comparado com o `deepseek-flash`; ele roda a cerca de 6 s por pedido.
- O cofre do macOS (Keychain) não é suportado.
- A janela de treinamento ainda não foi percorrida à mão.

## 7. Principais riscos

- **Alucinação e prompt injection:** reduzidos porque a IA não grava e todo plano passa pelo motor, mas a taxa de erro de planejamento (4%) e de conteúdo nas respostas (17,5%) ainda existe.
- **Dependência da DeepSeek:** custo, disponibilidade e troca de nomes de modelo (o `deepseek-v4.1-flash` na verdade se chama `deepseek-flash`).
- **Latência do planejamento:** cerca de 12 s por pedido, longa para o operador.
- **Chave de API:** fica só no cofre do sistema, mas o administrador precisa digitá-la em cada instalação.
