"""Comparison table of the Recipe Matcher variants: accuracy, cost per 1,000 uses and latency (references/IA.md, section 4).

Each variant is LABEL=PATH, a predictions file of predict_deepseek.py or predict_local.py (lines with "id", "prediction",
"seconds" and the token usage). A variant whose file does not exist yet is listed as pending, so the table can be filled in
as the runs arrive. Accuracy comes from evaluate.metrics; with --guard the exact match after the app's checks
(RecipeGrounding) is shown too. Cost uses the prices of references/IA.md (deepseek-flash at peak price; local models
cost nothing per call). Standard library only.

    python tools/finetune/compare.py --gold tools/finetune/data/holdout.jsonl --guard \
        "qwen-base=out/pred_qwen_base.jsonl" "qwen-base+RAG=out/pred_qwen_base_rag.jsonl" ...
"""

import argparse
import json
import pathlib
import statistics
import sys

import evaluate

# US$ per million tokens: input without cache, input read from the cache, output (references/IA.md, section 2).
PRICES = {
    "deepseek-flash": (0.30, 0.006, 1.20),
    "deepseek-v4-pro": (1.32, 0.044, 3.96),
}


def cost_usd(line):
    """Cost of one prediction line; models without a price (the local ones) cost zero per call."""
    price = PRICES.get(line.get("model", ""))
    if price is None:
        return 0.0
    prompt = line.get("prompt_tokens", 0) or 0
    cached = min(line.get("cache_read_tokens", 0) or 0, prompt)
    completion = line.get("completion_tokens", 0) or 0
    return ((prompt - cached) * price[0] + cached * price[1] + completion * price[2]) / 1_000_000


def summarize(gold, lines, guard):
    """Metrics, cost per 1,000 and latency of one variant."""
    predictions = {line["id"]: line.get("prediction") for line in lines}
    row = {"raw": evaluate.metrics(gold, predictions)}
    if guard:
        row["guard"] = evaluate.metrics(gold, predictions, with_guard=True)
    seconds = [line["seconds"] for line in lines if isinstance(line.get("seconds"), (int, float))]
    row["latency_mean"] = statistics.fmean(seconds) if seconds else None
    row["latency_p95"] = sorted(seconds)[max(0, -(-len(seconds) * 95 // 100) - 1)] if seconds else None
    row["cost_per_1000"] = 1000 * statistics.fmean(cost_usd(line) for line in lines) if lines else None
    row["tokens_mean"] = statistics.fmean((line.get("prompt_tokens", 0) or 0) + (line.get("completion_tokens", 0) or 0)
                                          for line in lines) if lines else None
    return row


def pct(value):
    return "—" if value is None else f"{value * 100:.1f}%".replace(".", ",")


def num(value, digits):
    return "—" if value is None else f"{value:.{digits}f}".replace(".", ",")


def markdown(rows, guard):
    head = "| Variante | JSON válido | Acerto exato | F1 por campo | \"Nenhuma receita\" |"
    sep = "|----------|-----------:|-------------:|-------------:|------------------:|"
    if guard:
        head += " Acerto exato com a checagem |"
        sep += "---------------------------:|"
    head += " Custo por 1.000 (US$) | Latência média (s) | p95 (s) |"
    sep += "----------------------:|-------------------:|--------:|"
    out = [head, sep]
    for label, row in rows:
        if row is None:
            cells = [label, "pendente", "", "", ""] + ([""] if guard else []) + ["", "", ""]
        else:
            raw = row["raw"]
            cells = [label, pct(raw["valid_json"]), pct(raw["exact_match"]), num(raw["field_f1"], 3), pct(raw["none_accuracy"])]
            if guard:
                cells.append(pct(row["guard"]["exact_match"]))
            cells += [num(row["cost_per_1000"], 4), num(row["latency_mean"], 2), num(row["latency_p95"], 2)]
        out.append("| " + " | ".join(cells) + " |")
    return "\n".join(out)


def parse_variant(text):
    label, sep, path = text.partition("=")
    if not sep or not label or not path:
        raise argparse.ArgumentTypeError(f"expected LABEL=PATH, got {text!r}")
    return label, pathlib.Path(path)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--gold", required=True)
    parser.add_argument("--guard", action="store_true")
    parser.add_argument("--json", help="Also write the numbers as JSON to this file.")
    parser.add_argument("variants", nargs="+", type=parse_variant)
    args = parser.parse_args(argv)

    gold = evaluate.read_jsonl(args.gold)
    rows = []
    for label, path in args.variants:
        rows.append((label, summarize(gold, evaluate.read_jsonl(path), args.guard) if path.exists() else None))
    print(markdown(rows, args.guard))
    if args.json:
        pathlib.Path(args.json).write_text(json.dumps(dict(rows), indent=2, ensure_ascii=False), encoding="utf-8")
    return rows


if __name__ == "__main__":
    sys.exit(0 if main() else 1)
