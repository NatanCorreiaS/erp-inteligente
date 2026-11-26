# Comparação do fine-tuning: o que falta rodar (passagem de tarefa)

**Para quem:** o integrante com a GPU NVIDIA onde o Matcher foi treinado (GTX 1660, 6 GB) e onde está o modelo `nexus-matcher/merged`.
**Prazo:** antes de gerar o PDF final da entrega (27/11/2026, 23h59).
**Por quê:** a entrega pede o acerto, o custo e a latência de quatro variantes: modelo base, base com RAG, ajustado e ajustado com RAG. As variantes do `deepseek-flash` já foram medidas em 26/11 nesta máquina, que não tem GPU. Faltam as quatro do Qwen.

## O que já está pronto

- `retrieve_examples.py`: o "RAG" do Matcher. Para cada pedido, recupera por BM25 os K exemplos de treino mais parecidos e os coloca antes do pedido, como turnos anteriores, no mesmo formato de resposta do treino. A busca usa só o conjunto de treino.
- `predict_local.py --rag K --label NOME`: manda o mesmo prompt do app e grava, por linha, a resposta, o tempo e os tokens.
- `compare.py`: monta a tabela final. Uma variante sem arquivo aparece como "pendente".
- Medido em 26/11 nos 40 pedidos escritos à mão (`data/holdout.jsonl`):

| Variante | JSON válido | Acerto exato | F1 | "Nenhuma receita" | Custo por 1.000 | Latência média |
|----------|-----------:|-------------:|---:|------------------:|----------------:|---------------:|
| `deepseek-flash`, só o prompt (como no app) | 80,0% | 47,5% | 0,724 | 90,0% | US$ 0,48 | 2,41 s |
| `deepseek-flash`, 8 exemplos fixos | 90,0% | 75,0% | 0,885 | 90,0% | US$ 0,39 | 1,98 s |
| `deepseek-flash` + RAG (8 recuperados) | 92,5% | 90,0% | 0,922 | 100,0% | US$ 0,47 | 1,75 s |

## Passo a passo (na máquina com GPU)

Rode tudo da raiz do repositório, com o venv de `requirements-train.txt` ativo.

```bash
git pull
python3 tools/finetune/generate_dataset.py                 # recria data/ (train, test, holdout), igual ao desta máquina
mkdir -p tools/finetune/out

# 1. Modelo BASE (sem ajuste), servido na porta 8080
python3 tools/finetune/serve_local.py --matcher Qwen/Qwen2.5-1.5B-Instruct --no-embeddings --port 8080 &
python3 tools/finetune/predict_local.py --data tools/finetune/data --split holdout --label qwen-base \
    --out tools/finetune/out/pred_qwen_base_holdout.jsonl
python3 tools/finetune/predict_local.py --data tools/finetune/data --split holdout --label qwen-base --rag 8 \
    --out tools/finetune/out/pred_qwen_base_rag_holdout.jsonl
kill %1

# 2. Modelo AJUSTADO (LoRA mesclado); troque DIR pelo caminho onde ele está
python3 tools/finetune/serve_local.py --matcher DIR/nexus-matcher/merged --no-embeddings --port 8080 &
python3 tools/finetune/predict_local.py --data tools/finetune/data --split holdout --label nexus-matcher \
    --out tools/finetune/out/pred_qwen_ft_holdout.jsonl
python3 tools/finetune/predict_local.py --data tools/finetune/data --split holdout --label nexus-matcher --rag 8 \
    --out tools/finetune/out/pred_qwen_ft_rag_holdout.jsonl
kill %1
```

Cada `predict_local.py` faz 40 chamadas, de 5 a 10 s cada nessa GPU. Opcional: repita com `--split test` (100 pedidos gerados).

## Montar a tabela

As predições do `deepseek-flash` de 26/11 já estão no repositório, em `tools/finetune/results/pred_flash_*.jsonl` (holdout e teste). Os comandos abaixo as leem de lá. Depois de gerar as do Qwen, rode:

```bash
cd tools/finetune
python3 compare.py --gold data/holdout.jsonl --guard --json out/comparacao_holdout.json \
  "Qwen2.5-1.5B base=out/pred_qwen_base_holdout.jsonl" \
  "Qwen2.5-1.5B base + RAG=out/pred_qwen_base_rag_holdout.jsonl" \
  "Qwen2.5-1.5B ajustado (LoRA)=out/pred_qwen_ft_holdout.jsonl" \
  "Qwen2.5-1.5B ajustado + RAG=out/pred_qwen_ft_rag_holdout.jsonl" \
  "deepseek-flash (como no app)=results/pred_flash_zero_holdout.jsonl" \
  "deepseek-flash + RAG=results/pred_flash_rag_holdout.jsonl"
```

O custo dos modelos locais sai como zero por chamada, já que não há cobrança por token. Na análise, cite o custo fixo: a GPU, a energia e as 3 h de treino.

## Onde colar os números

1. `references/IA.md`, seção 4: a tabela "Comparação das variantes", trocando as linhas "pendente".
2. `README.md`, Parte de IA: a linha do fine-tuning.
3. `relatorio/entrega_final.md`, seção 3.2: a mesma tabela e duas ou três frases de análise. Diga se o ajuste compensou contra o `deepseek-flash` + RAG, olhando o acerto exato com a checagem, a latência e o custo.
4. Gere o PDF de novo: `python3 tools/readme_to_pdf.py -i relatorio/entrega_final.md -o relatorio/entrega_final.pdf`.

Nada disso grava chave: o servidor local é só `127.0.0.1`, sem chave. `out/` fica fora do git; copie para `results/` as predições do Qwen que entrarem no relatório.
