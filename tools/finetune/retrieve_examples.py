"""RAG of the Recipe Matcher experiment: the K training examples most similar to a request, by BM25.

The "with RAG" variants of the comparison (references/IA.md, section 4) put these examples between the system prompt and
the request, as earlier user/assistant turns, in the answer format the fine-tuned model learned. Retrieval runs only over
the training set, never over the test or holdout sets. Standard library only.
"""

import json
import math
import re
import unicodedata

K1 = 1.2
B = 0.75


def terms(text):
    """Lowercase words without accents; SKU, address and dock codes stay whole (ABC-123 -> abc-123)."""
    folded = "".join(c for c in unicodedata.normalize("NFD", text or "") if unicodedata.category(c) != "Mn").lower()
    return re.findall(r"[a-z]+-[0-9]+(?:-[0-9]+)*|[a-z]+|[0-9]+", folded)


class Retriever:
    """BM25 index over the "input" of the training rows."""

    def __init__(self, rows):
        self.rows = list(rows)
        self.docs = [terms(r["input"]) for r in self.rows]
        self.avg = sum(len(d) for d in self.docs) / len(self.docs) if self.docs else 0.0
        df = {}
        for doc in self.docs:
            for t in set(doc):
                df[t] = df.get(t, 0) + 1
        n = len(self.docs)
        self.idf = {t: math.log(1 + (n - f + 0.5) / (f + 0.5)) for t, f in df.items()}

    def score(self, query_terms, i):
        doc = self.docs[i]
        length = len(doc)
        total = 0.0
        for t in set(query_terms):
            f = doc.count(t)
            if f:
                total += self.idf[t] * f * (K1 + 1) / (f + K1 * (1 - B + B * length / self.avg))
        return total

    def top(self, text, k):
        """The k most similar rows, best first; ties keep the training order (deterministic)."""
        if k <= 0:
            return []
        q = terms(text)
        scored = sorted(((self.score(q, i), -i) for i in range(len(self.rows))), reverse=True)
        return [self.rows[-neg] for score, neg in scored[:k] if score > 0]


def answer_text(row):
    """The assistant answer of a training row, exactly as the model was trained on it."""
    messages = row.get("messages") or []
    if len(messages) >= 3 and messages[2].get("role") == "assistant":
        return messages[2]["content"]
    return json.dumps(row["output"], ensure_ascii=False)


def as_turns(rows):
    """Examples as earlier turns of the conversation."""
    turns = []
    for row in rows:
        turns.append({"role": "user", "content": row["input"]})
        turns.append({"role": "assistant", "content": answer_text(row)})
    return turns
