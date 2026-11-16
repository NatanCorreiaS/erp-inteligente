# Avaliação · deepseek:deepseek-flash+bm25

Executada em 2026-11-16 18:25 UTC.

| Conjunto | Casos | Métrica | Resultado |
|---|---:|---|---:|
| RAG (40 perguntas) | 40 | recall@4 | 100.0% |
| RAG (40 perguntas) | 40 | recall@1 | 80.0% |
| RAG (40 perguntas) | 40 | MRR@4 | 0.871 |
| Planejamento (50 pedidos) | 50 | plano com as operações do gabarito | 96.0% |
| Planejamento (50 pedidos) | 50 | plano válido na 1ª tentativa | 90.0% |
| Planejamento (50 pedidos) | 50 | tentativas inválidas por pedido | 0.12 |
| Conhecimento (40 perguntas) | 40 | resposta correta e com fonte certa | 35.0% |
| Fora do escopo (30) | 30 | recusas corretas | 100.0% |
| Proativos e sem direito (10) | 10 | desfecho esperado | 100.0% |

Tokens médios por pedido: 24261; tempo médio: 7.4 s.

## Casos que falharam

- `P39` (planning): 1 proposta(s); operações: [pick_items]
- `P44` (planning): resposta com opções; operações: []
- `K01` (knowledge): sem a fonte POP-01
- `K02` (knowledge): faltou palavra-chave; sem a fonte POP-01/POP-12
- `K03` (knowledge): sem a fonte POP-02
- `K05` (knowledge): sem a fonte POP-03/POP-04
- `K06` (knowledge): sem a fonte POP-03
- `K07` (knowledge): sem a fonte POP-04
- `K08` (knowledge): sem a fonte POP-04
- `K09` (knowledge): faltou palavra-chave; sem a fonte POP-04/REF-22
- `K10` (knowledge): sem a fonte POL-05/REF-21
- `K11` (knowledge): sem a fonte POL-05
- `K12` (knowledge): faltou palavra-chave; sem a fonte POL-05/POP-12
- `K13` (knowledge): sem a fonte POP-06
- `K14` (knowledge): faltou palavra-chave; sem a fonte POP-06
- `K15` (knowledge): sem a fonte POP-06
- `K16` (knowledge): sem a fonte POP-07
- `K22` (knowledge): sem a fonte POP-10
- `K23` (knowledge): sem a fonte POP-11
- `K25` (knowledge): sem a fonte POP-11/POL-17
- `K26` (knowledge): faltou palavra-chave; sem a fonte POP-12
- `K28` (knowledge): sem a fonte POP-13
- `K32` (knowledge): sem a fonte POP-15/POL-17
- `K33` (knowledge): faltou palavra-chave; sem a fonte POP-16
- `K35` (knowledge): sem a fonte POL-17/POL-20/POP-11
- `K38` (knowledge): sem a fonte POL-19
- `K39` (knowledge): faltou palavra-chave; sem a fonte POL-20
- `K40` (knowledge): sem a fonte REF-21
