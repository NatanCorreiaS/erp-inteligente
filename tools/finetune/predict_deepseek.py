"""Baseline of the experiment: deepseek-flash with few-shot examples on the test set (references/IA.md, section 4).

Same system prompt and recipe catalog as the app, plus K examples from the training set as earlier turns. Writes one
JSONL line per test example: {"id", "prediction"} with the raw model text, for evaluate.py.

Standard library only. The key comes from the DEEPSEEK_API_KEY environment variable and is never written anywhere. The
host is fixed and redirects are refused, so the key cannot be sent elsewhere. Every run calls the API: ~100 short
requests.

    python tools/finetune/predict_deepseek.py --data tools/finetune/data --out tools/finetune/data/pred_deepseek.jsonl

Variants of the comparison: --shots 0 is the app's call (system prompt and catalog only); --rag K replaces the fixed
examples with the K training examples most similar to each request (retrieve_examples.py). Each line also records the
latency and the token usage, which compare.py turns into cost and latency.
"""

import argparse
import json
import os
import pathlib
import random
import time
import urllib.error
import urllib.request

import retrieve_examples

MODEL = "deepseek-flash"
URL = "https://api.deepseek.com/chat/completions"
MAX_TOKENS = 600  # the app's RecipeMatcher.MaxTokens


class NoRedirect(urllib.request.HTTPRedirectHandler):
    """A redirect would carry the Authorization header to another host: refuse it."""

    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


def few_shot(train, k, seed):
    if k <= 0:
        return []
    rng = random.Random(seed)
    in_scope = [r for r in train if r["output"].get("intent") != "none"]
    none = [r for r in train if r["output"].get("intent") == "none"]
    picked = rng.sample(in_scope, k - max(1, k // 4)) + rng.sample(none, max(1, k // 4))
    rng.shuffle(picked)
    turns = []
    for row in picked:
        turns.append({"role": "user", "content": row["input"]})
        turns.append({"role": "assistant", "content": json.dumps(row["output"], ensure_ascii=False)})
    return turns


def build_request(key, system, examples, text):
    """The POST request of one prediction: system prompt, the few-shot turns, then the request."""
    if not key or any(c <= " " or c > "~" for c in key):
        raise ValueError("DEEPSEEK_API_KEY is missing or malformed")
    body = {
        "model": MODEL,
        "messages": [{"role": "system", "content": system}] + examples + [{"role": "user", "content": text}],
        "max_tokens": MAX_TOKENS,
        "stream": False,
    }
    return urllib.request.Request(URL, data=json.dumps(body).encode("utf-8"), method="POST",
                                  headers={"Content-Type": "application/json", "Authorization": "Bearer " + key})


def read_answer(payload):
    """The text and the finish reason of a chat completions response."""
    choice = json.loads(payload)["choices"][0]
    return choice["message"].get("content") or "", choice.get("finish_reason")


def read_usage(payload):
    """Input, cached input and output tokens of a chat completions response (DeepSeek reports the cache hits)."""
    usage = json.loads(payload).get("usage") or {}
    cached = usage.get("prompt_cache_hit_tokens")
    if cached is None:
        cached = (usage.get("prompt_tokens_details") or {}).get("cached_tokens", 0)
    return {"prompt_tokens": usage.get("prompt_tokens", 0), "cache_read_tokens": cached or 0,
            "completion_tokens": usage.get("completion_tokens", 0)}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--data", required=True, help="Directory with train.jsonl and test.jsonl.")
    parser.add_argument("--out", required=True)
    parser.add_argument("--shots", type=int, default=8)
    parser.add_argument("--seed", type=int, default=7)
    parser.add_argument("--split", choices=["test", "holdout"], default="test")
    parser.add_argument("--rag", type=int, default=0, metavar="K",
                        help="Use the K most similar training examples instead of the fixed few-shot ones.")
    args = parser.parse_args()

    key = os.environ.get("DEEPSEEK_API_KEY", "").strip()
    if not key:
        parser.error("set the DEEPSEEK_API_KEY environment variable")

    data = pathlib.Path(args.data)
    train = [json.loads(line) for line in (data / "train.jsonl").read_text(encoding="utf-8").splitlines() if line]
    test = [json.loads(line) for line in (data / f"{args.split}.jsonl").read_text(encoding="utf-8").splitlines() if line]
    examples = few_shot(train, 0 if args.rag else args.shots, args.seed)
    retriever = retrieve_examples.Retriever(train) if args.rag else None

    opener = urllib.request.build_opener(NoRedirect)
    stops = {}
    with pathlib.Path(args.out).open("w", encoding="utf-8", newline="\n") as out:
        for row in test:
            shots = retrieve_examples.as_turns(retriever.top(row["input"], args.rag)) if retriever else examples
            request = build_request(key, row["messages"][0]["content"], shots, row["input"])
            started = time.perf_counter()
            try:
                with opener.open(request, timeout=60) as response:
                    payload = response.read().decode("utf-8")
                seconds = time.perf_counter() - started
                text, stop = read_answer(payload)
            except urllib.error.HTTPError as error:
                # Only the status: the body and the request (with the key) are never printed.
                raise SystemExit(f"DeepSeek answered HTTP {error.code} on {row['id']}") from None
            stops[stop] = stops.get(stop, 0) + 1
            line = {"id": row["id"], "prediction": text, "seconds": round(seconds, 3), "model": MODEL}
            line.update(read_usage(payload))
            out.write(json.dumps(line, ensure_ascii=False) + "\n")
    print(f"{len(test)} predictions -> {args.out}; finish reasons: {stops}")


if __name__ == "__main__":
    main()
