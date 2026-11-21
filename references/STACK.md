# Stack

## Aplicação

- **Nativa, não web.** O ERP é um aplicativo desktop nativo; não deve ser construído como aplicação web nem depender de um navegador para rodar.
- **Linguagem:** C#.
- **Interface:** Avalonia.

## Dados

- **Dados internos:** SQLite, criptografado em repouso com **SQLCipher**.
- **Busca:** FTS5 (texto) e **`sqlite-vec`** (vetores do RAG), no mesmo arquivo SQLite.
- **Instruções para LLMs:** arquivos JSON e Markdown (`.md`).
- **Não usados:** SQL Server, MongoDB e n8n não fazem parte do projeto.

## Provedores de LLM

Os provedores externos de LLM são a **DeepSeek**, o padrão, e a **OpenRouter** (decisão de 2026-11-21), usada para modelos gratuitos em apresentações e testes, só com dados fictícios. O Recipe Matcher também pode usar um modelo local ajustado por fine-tuning, servido em `127.0.0.1`.

Embeddings do RAG: um modelo de embeddings local, servido em `127.0.0.1`. Modelos padrão e custos em [`IA.md`](IA.md).

## Testes dos scripts de banco

Python 3 (biblioteca padrão `sqlite3` + `unittest`) testa os scripts de `db/`.

## Testes do app

xUnit v3 com `Avalonia.Headless.XUnit` (renderização Skia real) em `tests/Nexus.Tests`. Fonte da interface: família Barlow (SIL OFL) embutida em `Assets/Fonts`.

## Convenções

- **Documentação no código em inglês:** comentários, XML doc comments (`///`), nomes de identificadores, mensagens de log e READMEs de código.
- Os documentos de `references/` continuam em português.
