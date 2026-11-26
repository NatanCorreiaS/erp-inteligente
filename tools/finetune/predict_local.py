"""Predictions of the fine-tuned model served on this machine by a chat-completions server (llama.cpp, vLLM, Ollama).

Sends exactly what the app sends to the Local provider (system prompt + recipe catalog, then the request) to
http://127.0.0.1:PORT/v1/chat/completions and writes {"id", "prediction"} per test example. Loopback only, no key.
Standard library only.

    python tools/finetune/predict_local.py --data tools/finetune/data --out tools/finetune/data/pred_local.jsonl --port 8080

--rag K puts the K most similar training examples (retrieve_examples.py) between the system prompt and the request.
--label names the variant in the output (for example qwen-base or nexus-matcher). Each line records the latency and the
token usage reported by the server.
"""

import argparse
import json
import pathlib
import time
import urllib.request

import retrieve_examples

MODEL = "nexus-matcher"


def complete(port, messages, timeout=120):
    """Text, seconds and usage of one call to the local server."""
    body = json.dumps({"model": MODEL, "messages": messages, "max_tokens": 300, "stream": False}).encode("utf-8")
    request = urllib.request.Request(f"http://127.0.0.1:{port}/v1/chat/completions", data=body,
                                     headers={"Content-Type": "application/json"}, method="POST")
    started = time.perf_counter()
    with urllib.request.urlopen(request, timeout=timeout) as response:
        data = json.loads(response.read().decode("utf-8"))
    seconds = time.perf_counter() - started
    usage = data.get("usage") or {}
    return (data["choices"][0]["message"].get("content") or "", seconds,
            {"prompt_tokens": usage.get("prompt_tokens", 0), "completion_tokens": usage.get("completion_tokens", 0)})


def messages_for(row, retriever, k):
    """System prompt, the retrieved examples (if any), then the request: what the app would send plus the RAG turns."""
    system, user = row["messages"][0], row["messages"][1]
    shots = retrieve_examples.as_turns(retriever.top(row["input"], k)) if retriever else []
    return [system] + shots + [user]


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--data", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--port", type=int, default=8080)
    parser.add_argument("--split", choices=["test", "holdout"], default="test")
    parser.add_argument("--rag", type=int, default=0, metavar="K")
    parser.add_argument("--label", default=MODEL)
    args = parser.parse_args()
    if not 1024 <= args.port <= 65535:
        parser.error("the port must be between 1024 and 65535")

    data = pathlib.Path(args.data)
    retriever = None
    if args.rag:
        train = [json.loads(line) for line in (data / "train.jsonl").read_text(encoding="utf-8").splitlines() if line]
        retriever = retrieve_examples.Retriever(train)
    test = [json.loads(line) for line in (pathlib.Path(args.data) / f"{args.split}.jsonl").read_text(encoding="utf-8").splitlines() if line]
    with pathlib.Path(args.out).open("w", encoding="utf-8", newline="\n") as out:
        for row in test:
            text, seconds, usage = complete(args.port, messages_for(row, retriever, args.rag))
            line = {"id": row["id"], "prediction": text, "seconds": round(seconds, 3), "model": args.label}
            line.update(usage)
            out.write(json.dumps(line, ensure_ascii=False) + "\n")
    print(f"{len(test)} predictions -> {args.out}")


if __name__ == "__main__":
    main()
