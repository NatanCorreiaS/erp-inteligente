# Inteligência Artificial

> Detalha as seções 8, 10 e 12.8 de [`ARQUITETURA.md`](ARQUITETURA.md).

## 1. LLMs escolhidos

| Papel | Modelo | Por quê |
|-------|--------|---------|
| Planner (planeja e trata exceções) | **`deepseek-flash`** | Tool calling com raciocínio (*thinking*) devolvido no laço de ferramentas, a cerca de um quarto do custo do `deepseek-v4-pro`. Decisão de 2026-11-14; o `deepseek-v4-pro` continua como opção em Configurações. |
| Receitas e extração de parâmetros | **`deepseek-flash`**, depois o modelo do fine-tuning | Tarefa curta e repetitiva; é o modelo mais barato do provedor. |
| Embeddings do RAG | **Modelo local `nexus-embed`** (`paraphrase-multilingual-MiniLM-L12-v2`) | A DeepSeek não oferece embeddings. O modelo local roda em `127.0.0.1`, sem custo e sem envio de dados. |

O único provedor externo é a **DeepSeek** (decisão de 2026-11-14). A interface `ILlmProvider` continua separando o app do provedor.

**Estado da implementação (feature 3):** o adaptador implementado é o da **DeepSeek**.
- **Modelos:** `deepseek-flash` no Planner por padrão, ou `deepseek-v4-pro`, escolhidos em Configurações. `deepseek-flash` é o id que a API aceita; em 2026-11-14 a documentação da DeepSeek informa que ele é servido pelo DeepSeek-V4.1-Flash, e não existe um id `deepseek-v4.1-flash`.
- **Chave:** digitada pelo administrador e guardada no cofre do SO.
- **Limites por pedido:** 4 planos inválidos, 12 rodadas, 3 propostas válidas, 8.192 tokens por chamada e 60.000 por pedido. Passou do limite, a tarefa é escalada ao operador, salvo quando já há propostas válidas: essas são entregues. É a defesa contra "custo descontrolado" da seção 5.
- **Direitos:** a IA tem os direitos operacionais do usuário que pede (decisão de 2026-11-08). Ver [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md), seção 3.1.
- **Recipe Matcher (feature 7):** uma chamada sem ferramentas, de até 600 tokens de resposta. Um filtro sem LLM evita a chamada quando o pedido não tem valor nem palavra em comum com uma receita ativa.
- **Modelo por papel (feature 8):** o administrador escolhe o modelo de cada papel em Configurações:
  - Planejamento: `deepseek-v4-pro` ou `deepseek-flash`.
  - Matcher: os mesmos, mais o modelo local ajustado (`nexus-matcher`).
  - O padrão é `deepseek-flash` nos dois papéis.

**Privacidade:** o que sai da máquina é sempre não sensível (views `v_ai_*`, procedimentos fictícios e memórias filtradas). Por isso a escolha do provedor afeta custo e qualidade, mas não expõe CPF, contatos ou custos. Quem quiser zero envio de dados pode usar o modelo do fine-tuning e embeddings locais para receitas e RAG; o Planner ainda depende de um provedor.

## 2. Custo por 1.000 usos

### Preços usados

Consultados em 27/10/2026, por 1 milhão de tokens:

| Modelo | Entrada | Saída | Fonte |
|--------|--------:|------:|-------|
| `deepseek-v4-pro` | US$ 1,32 | US$ 3,96 | api-docs.deepseek.com, preço de pico, cache miss |
| `deepseek-flash` | US$ 0,30 | US$ 1,20 | api-docs.deepseek.com, preço de pico, cache miss |
| `nexus-embed` e `nexus-matcher` (locais) | 0 | 0 | Rodam na máquina do usuário |

A DeepSeek publica os preços em dólar (US$). A leitura do cache de prompt tem preço próprio: US$ 0,006 por milhão no `deepseek-flash` e US$ 0,044 no `deepseek-v4-pro`. Fora do horário de pico (01h–04h e 06h–10h UTC, de segunda a sexta, fora feriados chineses), todos os preços caem pela metade. Consultados em 16/11/2026.

### Premissas de um "uso" (um pedido do operador)

| Caminho | Chamadas | Tokens de entrada | Tokens de saída |
|---------|---------:|------------------:|----------------:|
| Planejamento completo | ~3 (consultas, `propose_plan`, texto final) | 12.000 (acumulado) | 2.000 (inclui raciocínio) |
| Receita | 1 | 1.500 | 150 |
| Embedding da pergunta (modelo local) | 1 | 40 | — |

No início, 70% dos pedidos passam pelo planejamento e 30% por receitas. Com o uso, a meta é chegar a 40% e 60%.

**Medição real (feature 9):** cada pedido grava seus tokens em `llm_usage`, e a tela Custos de IA mostra o custo estimado com os preços acima (DeepSeek em US$ no pico; um modelo sem preço aqui aparece só com os tokens). É a fonte para refazer esta conta com números medidos.

**Premissas em revisão:** o modo proativo (2026-11-08) pode fazer mais consultas por pedido e até 3 propostas. A medição de 2026-11-14 (seção 5) deu 34.377 tokens por pedido de planejamento, contra os 14.000 das premissas acima. A conta abaixo ainda usa as premissas: refazê-la com a divisão entre entrada, cache e saída da tela Custos de IA antes de citar um custo real.

### Resultado

| Configuração | 70% planejamento | 40% planejamento |
|--------------|-----------------:|-----------------:|
| **`deepseek-flash` nos dois papéis (padrão, pico)** | **≈ US$ 4,39** | **≈ US$ 2,78** |
| `deepseek-v4-pro` no planejamento + `deepseek-flash` nas receitas (pico) | ≈ US$ 16,82 | ≈ US$ 9,88 |
| `deepseek-flash` sem receitas (100% planejamento, pico) | ≈ US$ 6,00 | — |

**Contas do padrão (70/30):**
- 700 × (12.000 × 0,30 + 2.000 × 1,20) / 1.000.000 = US$ 4,20
- 300 × (1.500 × 0,30 + 150 × 1,20) / 1.000.000 = US$ 0,19
- Embeddings: sem custo (modelo local)

**Leituras:**
- As **receitas** são o que mais reduz o custo: sair de 70% para 40% de planejamento corta ~37% do gasto.
- O **cache de prompt** (prompt de sistema + definições de ferramentas, que se repetem em todo pedido) não entrou na conta e reduz ainda mais o custo de entrada. **Estado (2026-11-14):** a DeepSeek faz o cache do prefixo do prompt sozinha e informa quantos tokens leu dele (`prompt_cache_hit_tokens`), então cada rodada do laço de ferramentas relê as anteriores do cache. Esses tokens voltam em `LlmUsage` e ficam em `llm_usage` (migração 010); o painel de custos cobra a leitura do cache pelo preço próprio dela. A DeepSeek não cobra a gravação no cache, então a coluna de tokens gravados fica em zero.
- O `deepseek-v4-pro` no planejamento custa ~4× mais. Fica como opção configurável para quem quiser mais qualidade no Planner, que é onde erros custam mais.

## 2.1 Skills sob demanda (feature 4)

- O prompt de sistema leva só o índice das skills: uma linha por skill (nome, descrição de até 300 caracteres e operações), cerca de 60 tokens cada. As 6 skills embutidas somam ~400 tokens no índice, contra ~4.800 se os corpos fossem todos no prompt.
- O corpo (2,4 a 3,3 mil caracteres, ~800 tokens) entra só quando a IA chama `load_skill`, no máximo 3 por pedido.
- Como os exemplos de plano das skills já passam pelo `DslParser`, a IA tem modelos válidos à mão, o que reduz rodadas de correção.
- A IA também redige skills a pedido do supervisor (`propose_skill`); nada é gravado sem a confirmação dele.

## 2.2 Memórias e contexto automático (feature 5)

- Cada pedido recebe até 5 memórias do armazém e até 2 trechos de procedimento, limitados a 2.500 caracteres (~650 tokens no pior caso). Pedidos operacionais ("mova 10 do ABC-123") normalmente não recebem trecho nenhum: os identificadores saem da consulta e são exigidos 2 termos em comum.
- A IA busca mais quando precisa (`search_knowledge`, `search_memories`) e registra regras e preferências ditas pelo operador (`remember`, até 2 por pedido).

## 3. RAG

| Etapa | Definição |
|-------|-----------|
| Estado | Feature 5: BM25 em 2026-11-08; **busca híbrida em 2026-11-11** (BM25 + cosseno, RRF). Os embeddings são opcionais e ficam desligados até o administrador escolher o provedor em Configurações |
| Fontes | `knowledge/*.md` (22 documentos fictícios, embutidos e indexados na abertura). As skills não entram: são carregadas sob demanda por `load_skill` |
| Chunking | Um trecho por seção `##`, prefixado com título do documento e da seção; seções acima de ~500 tokens divididas com ~50 tokens de sobreposição |
| Embeddings | Modelo local `nexus-embed` (`paraphrase-multilingual-MiniLM-L12-v2`, 384 dimensões, em `127.0.0.1`, sem custo e sem envio de dados) |
| Banco vetorial | `knowledge_embeddings` no mesmo arquivo SQLite (float32, um vetor por trecho), com a distância calculada pelo `sqlite-vec` |
| Busca | Híbrida: top-20 FTS5/BM25 + top-20 por cosseno, fundidos por Reciprocal Rank Fusion. Até 2 trechos no contexto automático e até 5 em `search_knowledge`. Similaridade mínima para um trecho achado só pelo significado: 0,40 (busca) e 0,55 (contexto) |
| Uso no prompt | Trechos delimitados como dados de referência, com a origem (`POP-03`, `POL-17`...) para citação |

### Base de documentos inicial

`knowledge/` tem **22 documentos** escritos pelo grupo para um centro de distribuição **fictício** (CD-01): 16 procedimentos operacionais, 4 políticas, glossário e perguntas frequentes. Não há dados de empresas ou pessoas reais. O banco de teste (`db/seed.sql`) também é fictício: nomes de pessoas genéricos, documentos sempre mascarados e colunas sensíveis vazias.

## 4. Fine-tuning

| Item | Definição |
|------|-----------|
| Tarefa | Pedido em linguagem natural → `{intent, recipe, params}`, ou `{"intent":"none"}` |
| Dados | ~500 pedidos fictícios gerados a partir das receitas e do catálogo de teste (variações de escrita, abreviações, erros de digitação, 15% fora do escopo); anotados e revisados pelo grupo; 400 treino / 100 teste |
| Modelo base | Modelo aberto pequeno com LoRA (classe 1–3 B parâmetros, ex.: Qwen2.5-1.5B-Instruct), rodando local |
| Métricas | *Exact match* do JSON; F1 por campo; % de JSON válido; acerto em "nenhuma receita" |
| Linha de base | `deepseek-flash` com *few-shot* no mesmo conjunto de teste |
| Critério de adoção | Substitui o `deepseek-flash` se empatar ou superar no *exact match* e tiver ≥ 99% de JSON válido |
| Ferramentas (feature 8) | `tools/finetune`, ver o `README.md` de lá. O gerador, o conjunto escrito à mão e a avaliação estão testados (`tools/tests/test_finetune.py`) |

Exemplo de par de treino:

```json
{"input": "guarda as 20 cx de ABC-124 da doca 2 no A-01-02",
 "output": {"intent": "putaway", "recipe": "armazenar_da_doca",
            "params": {"sku": "ABC-124", "qty": 20, "dock": "DOCA-02", "target": "A-01-02"}}}
```

**Resultados (2026-11-12):** Qwen2.5-1.5B-Instruct com LoRA (r = 16, 3 épocas, perda só na resposta), treinado numa GTX 1660 de 6 GB em 3 h 03 min, servido em `127.0.0.1` por `serve_local.py`. O conjunto de teste gerado sai dos mesmos modelos de frase do treino; por isso o grupo escreveu à mão um segundo conjunto (`tools/finetune/holdout.json`, 40 pedidos: outras frases, SKUs fora da lista do gerador, números por extenso e quase acertos, como mover entre duas prateleiras ou receber com lote).

| Conjunto | JSON válido | *Exact match* | F1 por campo | "Nenhuma receita" |
|----------|------------:|--------------:|-------------:|------------------:|
| Teste gerado (100) | 100% | 100% | 1,000 | 100% |
| Escrito à mão (40), resposta do modelo | 100% | 80,0% | 0,991 | 30% |
| Escrito à mão (40), depois da checagem do app | 100% | 92,5% | 0,986 | 80% |

- Nos pedidos dentro do escopo, o modelo acerta 29 de 30 casos, inclusive frases novas e números por extenso.
- O ponto fraco é o pedido incompleto: o modelo completa o que falta com um valor plausível (um destino, uma quantidade "1", uma doca). Por isso o app ganhou a checagem `RecipeGrounding` depois do Matcher, que vale para qualquer provedor. Cada valor precisa estar no pedido (sem diferenciar maiúsculas e acentos, "doca 1" = `DOCA-01`, número por extenso aceito), e cada trecho do pedido sustenta um valor só. Se a checagem falha, não há receita e o Planner pergunta. A mesma regra, em Python, está em `evaluate.py --guard`; os vetores de teste (`grounding_cases.json`) são os mesmos nas duas linguagens.
- Ainda passam pela checagem 2 de 40 casos:
  - "baixa 3 LIM-200 no A-01-02 por avaria", sem motivo: o modelo usou "Avaria" como motivo;
  - um recebimento com lote, que a receita sem lote ignora.
  - Nos dois casos, o resultado continua sendo uma proposta que quem pediu confirma.
- Latência: cerca de 6 s por pedido nessa GPU (transformers em fp16, sem quantização).

Decisão do grupo: o Matcher fica no `deepseek-flash`. O modelo local é a opção para quem não quer enviar nada para fora.

## 5. Onde a IA pode errar e como medimos

| Risco | Exemplo | Defesa | Métrica (conjunto de avaliação) |
|-------|---------|--------|---------------------------------|
| Alucinação de dados | Inventar um endereço ou SKU que não existe | O validador rejeita referências inexistentes | % de planos válidos na 1ª tentativa; nº médio de tentativas (50 pedidos com gabarito) |
| Valor inventado pelo Matcher | "guarda 15 LIM-201 da doca 2" vira uma receita com um destino que ninguém pediu | `RecipeGrounding`: cada valor precisa estar no pedido, senão não há receita e o Planner pergunta | Acerto em "nenhuma receita" nos 40 pedidos escritos à mão (seção 4) |
| Alucinação de procedimento | Afirmar uma regra que não está nos documentos | RAG com citação obrigatória da origem | % de respostas corretas e com fonte certa (40 perguntas sobre `knowledge/`); recall@4 do RAG |
| Fora do escopo | "Calcule a folha de pagamento", "qual a previsão do tempo?" | Prompt de sistema restrito ao armazém; sem ferramentas para outros assuntos | % de recusas corretas (30 pedidos fora do escopo) |
| Recusa ou pergunta desnecessária | "Guarde 10 cx do ABC-123 em Q-01-01" com a zona Q inexistente: o certo é propor a zona, os endereços e a movimentação | Prompt proativo: padrões, suposições e plano que cria o que falta; pergunta só com `offer_options` | % de pedidos com plano válido sem pergunta (casos proativos); % de recusas corretas (sem direito ou fora do escopo) |
| Prompt injection | Ver [`SEGURANCA_DADOS.md`](SEGURANCA_DADOS.md), seção 6.1 | Seção 12.8 da arquitetura | Taxa de sucesso do ataque (meta 0%) e taxa de detecção (25 casos) |
| Custo descontrolado | Laço de correções de plano | Limite de tentativas e de tokens por tarefa | Tokens médios por pedido; % de pedidos resolvidos por receita (medidos no uso real pela tela Custos de IA, feature 9) |

**Menos recusas são esperadas.** O modo proativo reduz as recusas de propósito, então o conjunto de avaliação deve incluir casos proativos: entidade inexistente, dado faltando e pedido sem direito. Sem esses casos, uma queda na recusa pareceria melhoria.

Os conjuntos de avaliação são fictícios, versionados junto com o código e rodados a cada mudança de prompt, skill ou modelo.

**Estado (2026-11-11):** os conjuntos estão em `tools/eval` (`planning.json`: 50 pedidos com gabarito; `knowledge.json`: 40 perguntas com documento esperado e palavras-chave; `scope.json`: 30 fora do escopo e 10 proativos ou sem direito), com os dados do seed. O executor `tools/Nexus.Eval` cria um banco novo, roda o Planner real e grava um relatório em `tools/eval/results`. A coerência dos conjuntos (cada operação do gabarito é permitida a quem pede, cada documento existe) e as regras de pontuação são testadas em `EvalTests`.

| Medição | recall@4 | recall@1 | MRR@4 |
|---------|---------:|---------:|------:|
| RAG só BM25 | 100% | 80,0% | 0,871 |
| RAG híbrido, modelo local (`paraphrase-multilingual-MiniLM-L12-v2`) | 100% | 87,5% | 0,927 |

As perguntas usam o vocabulário dos documentos, então o BM25 já acha o documento entre os 4 primeiros; o ganho da busca híbrida aparece na ordem (o certo em 1º lugar).

**Conjuntos que chamam o LLM:** o Planner e o Matcher usam o `deepseek-flash`, o padrão desde 2026-11-14, e o RAG usa só BM25. A medição de 2026-11-16 (130 pedidos) está em `tools/eval/results/20261116-1825-deepseek_deepseek-flash_bm25.md`. A de 2026-11-14, com o `deepseek-v4-pro` no Planner, serve de comparação.

| Conjunto | Casos | Métrica | `deepseek-flash` | `deepseek-v4-pro` |
|----------|------:|---------|-----------------:|------------------:|
| Planejamento | 50 | Plano com as operações do gabarito | 96,0% (48) | 92,0% (46) |
| Planejamento | 50 | Plano válido na 1ª tentativa | 90,0% | 88,0% |
| Planejamento | 50 | Tentativas inválidas por pedido | 0,12 | 0,06 |
| Respostas com fonte | 40 | Resposta correta e com a fonte certa | 35,0% (14) | 15,0% (6) |
| Respostas com fonte | 40 | Resposta com as palavras-chave esperadas | 82,5% (33) | 97,5% (39) |
| Fora do escopo | 30 | Recusas corretas | 100% | 100% |
| Proativos e sem direito | 10 | Desfecho esperado | 100% | 100% |

- **Planejamento:** os 2 erros do `deepseek-flash` são `P39` (propôs só `pick_items`, sem o resto do gabarito) e `P44` (ofereceu opções em vez de propor o plano). No `deepseek-v4-pro` eram `P14`, `P44`, `P50` e `P39`.
- **Respostas com fonte:** o `deepseek-flash` cita o código do documento em mais casos (14 de 40, contra 6), mas acerta menos o conteúdo (33 de 40 respostas com as palavras-chave, contra 39). O prompt de sistema (`Core/Planner/Prompts/system.md`) não pede a citação, embora a tabela acima a trate como obrigatória, então a taxa de citação depende do modelo.
- **Tokens e tempo por pedido** (entrada + saída, somando as chamadas), com o `deepseek-flash`: planejamento 39.230 e 11,7 s; proativos 36.784 e 12,6 s; respostas com fonte 13.246 e 4,0 s; fora do escopo 9.824 e 2,9 s. A média dos 130 pedidos é 24.261 tokens e 7,4 s. O planejamento consome cerca de 3 vezes os 12.000 tokens de entrada das premissas da seção 2, então o custo por 1.000 usos de lá está subestimado para o modo proativo. Como o relatório da avaliação não separa entrada, saída e cache, o teto de custo por pedido é o de contar tudo como entrada sem cache: 24.261 × US$ 0,30 por milhão, cerca de US$ 0,007.

```bash
DEEPSEEK_API_KEY=... dotnet run --project tools/Nexus.Eval -- --suites all
```
