# Fluxo n8n: avaliação das respostas com fonte

O app Nexus **não usa o n8n**: o laço de ferramentas e o motor de planos ficam dentro dele (`references/ARQUITETURA.md`). O n8n entra aqui como ferramenta externa de orquestração visual da avaliação. Ele roda localmente, em Docker, ligado só a `127.0.0.1` (não é SaaS) e repete o caminho RAG + LLM sobre as 40 perguntas de `tools/eval/knowledge.json`.

| Arquivo | Conteúdo |
|---------|----------|
| `avaliacao_rag.json` | Fluxo exportado pelo próprio n8n (`n8n export:workflow`) |
| `resultado_20261126.json` | Resultado da execução de 26/11/2026: resumo e as 40 respostas |

## O fluxo

```
Início → Ler perguntas → Extrair perguntas → Ler procedimentos → Montar contexto (BM25, top 4)
      → Uma pergunta por vez ⟲ [Marcar início → DeepSeek (deepseek-flash) → Pontuar resposta]
      → Resumo → Gerar JSON → Gravar resultado
```

- **Montar contexto:** divide os 22 procedimentos de `knowledge/` por seção `##`, indexa por BM25 e põe os 4 trechos mais parecidos dentro de `<dados>`, com o código do documento.
- **DeepSeek:** nó HTTP Request para `https://api.deepseek.com/chat/completions`. O prompt de sistema exige a citação do código (POP-03...).
- **Pontuar:** confere as palavras-chave esperadas, a citação de um documento esperado, os tokens, o custo (preço de pico do `deepseek-flash`) e o tempo da chamada.

## Resultado (26/11/2026, 40 perguntas)

| Métrica | Valor |
|---------|------:|
| Documento esperado entre os 4 trechos | 90,0% |
| Resposta com as palavras-chave | 80,0% |
| Resposta que cita a fonte | 85,0% |
| Correta e com a fonte | 77,5% |
| Latência média / p95 | 1,32 s / 1,84 s |
| Tokens por pergunta | 537 |
| Custo por 1.000 perguntas | US$ 0,25 |

O BM25 deste fluxo é mais simples que o do app (sem pesos por campo nem sinônimos), por isso acha o documento em 90% dos casos, contra 100% no app.

## Segredos

- A chave da DeepSeek fica só no cofre de credenciais do n8n, cifrado com a `N8N_ENCRYPTION_KEY` da instância. O fluxo exportado traz apenas a referência (`id` e `name` da credencial `httpHeaderAuth`).
- `tools/tests/test_n8n_export.py` confere que o fluxo e o resultado não têm chave (`sk-...`), token `Bearer`, campo de senha com valor nem e-mail, e que o nó HTTP usa só a credencial.

## Como rodar

```bash
# 1. n8n local, só em 127.0.0.1, com as pastas de dados montadas só para leitura
docker run -d --name nexus-n8n -p 127.0.0.1:5678:5678 \
  -e N8N_ENCRYPTION_KEY="$(python3 -c 'import secrets; print(secrets.token_hex(24))')" \
  -e N8N_DIAGNOSTICS_ENABLED=false -e N8N_RESTRICT_FILE_ACCESS_TO=/data \
  -v "$PWD/tools/eval:/data/eval:ro" -v "$PWD/knowledge:/data/knowledge:ro" -v "$PWD/n8n/out:/data/out" \
  docker.n8n.io/n8nio/n8n:1.123.0

# 2. Credencial: em http://127.0.0.1:5678, Credentials → Header Auth, nome "DeepSeek (cabeçalho Authorization)",
#    Name = Authorization, Value = Bearer <chave>. A chave nunca vai para um arquivo do repositório.

# 3. Importar e executar
docker cp n8n/avaliacao_rag.json nexus-n8n:/tmp/wf.json
docker exec nexus-n8n n8n import:workflow --input=/tmp/wf.json
docker exec nexus-n8n n8n execute --id=nexusAvaliacaoRag      # grava n8n/out/n8n_rag_resultados.json

# 4. Exportar de novo depois de mudar o fluxo, e conferir
docker exec nexus-n8n n8n export:workflow --id=nexusAvaliacaoRag --output=/tmp/export.json --pretty
docker cp nexus-n8n:/tmp/export.json n8n/avaliacao_rag.json
python3 -m unittest tools.tests.test_n8n_export -v
```

Se a credencial for criada com outro id, o n8n pede para escolhê-la no nó DeepSeek ao abrir o fluxo.
