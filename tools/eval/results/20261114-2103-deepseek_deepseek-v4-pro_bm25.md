# Avaliação · deepseek:deepseek-v4-pro+bm25

Executada em 2026-11-14 21:03 UTC.

| Conjunto | Casos | Métrica | Resultado |
|---|---:|---|---:|
| RAG (40 perguntas) | 40 | recall@4 | 100.0% |
| RAG (40 perguntas) | 40 | recall@1 | 80.0% |
| RAG (40 perguntas) | 40 | MRR@4 | 0.871 |
| Planejamento (50 pedidos) | 50 | plano com as operações do gabarito | 92.0% |
| Planejamento (50 pedidos) | 50 | plano válido na 1ª tentativa | 88.0% |
| Planejamento (50 pedidos) | 50 | tentativas inválidas por pedido | 0.06 |
| Conhecimento (40 perguntas) | 40 | resposta correta e com fonte certa | 15.0% |
| Fora do escopo (30) | 30 | recusas corretas | 100.0% |
| Proativos e sem direito (10) | 10 | desfecho esperado | 100.0% |

Tokens médios por pedido: 20088; tempo médio: 8.0 s.

## Casos que falharam

- `P14` (planning): resposta com opções; operações: []
- `P39` (planning): 1 proposta(s); operações: [pick_items]
- `P44` (planning): resposta; operações: []
- `P50` (planning): resposta com opções; operações: []
- `K01` (knowledge): sem a fonte POP-01
- `K03` (knowledge): sem a fonte POP-02
- `K04` (knowledge): sem a fonte POP-02
- `K05` (knowledge): sem a fonte POP-03/POP-04
- `K06` (knowledge): sem a fonte POP-03
- `K07` (knowledge): sem a fonte POP-04
- `K08` (knowledge): sem a fonte POP-04
- `K10` (knowledge): sem a fonte POL-05/REF-21
- `K11` (knowledge): sem a fonte POL-05
- `K13` (knowledge): sem a fonte POP-06
- `K16` (knowledge): sem a fonte POP-07
- `K18` (knowledge): sem a fonte POP-08/POL-17
- `K19` (knowledge): sem a fonte POP-09
- `K20` (knowledge): sem a fonte POP-09
- `K21` (knowledge): sem a fonte POP-10
- `K22` (knowledge): sem a fonte POP-10
- `K23` (knowledge): sem a fonte POP-11
- `K24` (knowledge): sem a fonte POP-11
- `K25` (knowledge): sem a fonte POP-11/POL-17
- `K26` (knowledge): faltou palavra-chave; sem a fonte POP-12
- `K27` (knowledge): sem a fonte POP-12
- `K28` (knowledge): sem a fonte POP-13
- `K29` (knowledge): sem a fonte POP-13
- `K30` (knowledge): sem a fonte POL-14
- `K31` (knowledge): sem a fonte POL-14
- `K32` (knowledge): sem a fonte POP-15/POL-17
- `K33` (knowledge): sem a fonte POP-16
- `K34` (knowledge): sem a fonte POL-17
- `K35` (knowledge): sem a fonte POL-17/POL-20/POP-11
- `K36` (knowledge): sem a fonte POL-18
- `K37` (knowledge): sem a fonte POL-18
- `K38` (knowledge): sem a fonte POL-19
- `K39` (knowledge): sem a fonte POL-20
- `K40` (knowledge): sem a fonte REF-21
