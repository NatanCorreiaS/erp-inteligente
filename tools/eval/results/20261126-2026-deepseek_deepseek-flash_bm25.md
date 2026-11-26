# Avaliação · deepseek:deepseek-flash+bm25

Executada em 2026-11-26 20:26 UTC.

| Conjunto | Casos | Métrica | Resultado |
|---|---:|---|---:|
| RAG (40 perguntas) | 40 | recall@4 | 100.0% |
| RAG (40 perguntas) | 40 | recall@1 | 80.0% |
| RAG (40 perguntas) | 40 | MRR@4 | 0.871 |
| Planejamento (50 pedidos) | 50 | plano com as operações do gabarito | 96.0% |
| Planejamento (50 pedidos) | 50 | plano válido na 1ª tentativa | 88.0% |
| Planejamento (50 pedidos) | 50 | tentativas inválidas por pedido | 0.14 |
| Conhecimento (40 perguntas) | 40 | resposta correta e com fonte certa | 80.0% |
| Fora do escopo (30) | 30 | recusas corretas | 100.0% |
| Proativos e sem direito (10) | 10 | desfecho esperado | 100.0% |

Tokens médios por pedido: 24212; tempo médio: 5.9 s.

## Custo e latência medidos

| Conjunto | Pedidos | Custo médio (US$) | Custo por 1.000 (US$) | Latência média (s) | p95 (s) |
|---|---:|---:|---:|---:|---:|
| planning | 50 | 0.00244 | 2.44 | 9.5 | 21.3 |
| knowledge | 40 | 0.00050 | 0.50 | 2.8 | 7.3 |
| out_of_scope | 30 | 0.00029 | 0.29 | 1.9 | 3.1 |
| proactive | 10 | 0.00297 | 2.97 | 12.1 | 21.1 |
| todos | 130 | 0.00139 | 1.39 | 5.9 | 16.5 |

## Casos que falharam

- `P39` (planning): 1 proposta(s); operações: [pick_items]
- `P44` (planning): resposta com opções; operações: []
- `K02` (knowledge): faltou palavra-chave; 
- `K05` (knowledge): sem a fonte POP-03/POP-04
- `K14` (knowledge): faltou palavra-chave; sem a fonte POP-06
- `K23` (knowledge): faltou palavra-chave; 
- `K26` (knowledge): faltou palavra-chave; 
- `K28` (knowledge): faltou palavra-chave; 
- `K33` (knowledge): faltou palavra-chave; 
- `K39` (knowledge): faltou palavra-chave; sem a fonte POL-20
