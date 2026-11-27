"""The exported n8n workflow (n8n/avaliacao_rag.json) and its result file carry no secret.

The DeepSeek key lives only in the n8n credential store (encrypted with the instance key). The export may only reference
the credential by id and name. Run from the repository root:  python3 -m unittest discover -s tools/tests -v
"""

import json
import pathlib
import re
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]
WORKFLOW = ROOT / "n8n" / "avaliacao_rag.json"
RESULTS = sorted((ROOT / "n8n").glob("resultado_*.json"))

# A DeepSeek/OpenAI-style key, a bearer token, or a field that would hold a secret with a literal value.
SECRET_PATTERNS = [
    re.compile(r"sk-[A-Za-z0-9]{16,}"),
    re.compile(r"Bearer\s+[A-Za-z0-9._\-]{8,}", re.IGNORECASE),
    re.compile(r'"(api[_-]?key|apikey|password|passwd|secret|token|authorization)"\s*:\s*"[^"{]', re.IGNORECASE),
]


def load_workflow():
    data = json.loads(WORKFLOW.read_text(encoding="utf-8"))
    return data[0] if isinstance(data, list) else data


class N8nExportTests(unittest.TestCase):
    def test_files_have_no_secret(self):
        for path in [WORKFLOW, *RESULTS]:
            text = path.read_text(encoding="utf-8")
            for pattern in SECRET_PATTERNS:
                self.assertIsNone(pattern.search(text), f"{path.name}: {pattern.pattern}")

    def test_http_nodes_use_a_credential_reference_only(self):
        http = [n for n in load_workflow()["nodes"] if n["type"] == "n8n-nodes-base.httpRequest"]
        self.assertTrue(http)
        for node in http:
            self.assertEqual(node["parameters"]["authentication"], "genericCredentialType")
            self.assertEqual(set(node["credentials"]["httpHeaderAuth"]), {"id", "name"})
            self.assertNotIn("headerParameters", node["parameters"])
            self.assertEqual(node["parameters"]["url"], "https://api.deepseek.com/chat/completions")

    def test_no_credential_data_and_no_personal_data(self):
        text = WORKFLOW.read_text(encoding="utf-8")
        self.assertNotIn('"data": {', text.replace("\n", ""))  # credential bodies are never exported with a workflow
        self.assertNotIn("@", text)  # no e-mail of an n8n user

    def test_reads_only_the_mounted_read_only_folders_and_writes_to_out(self):
        for node in load_workflow()["nodes"]:
            if node["type"] == "n8n-nodes-base.readWriteFile":
                path = node["parameters"].get("fileSelector") or node["parameters"].get("fileName")
                self.assertTrue(path.startswith(("/data/eval/", "/data/knowledge/", "/data/out/")), path)

    def test_results_match_the_cases(self):
        self.assertTrue(RESULTS)
        data = json.loads(RESULTS[-1].read_text(encoding="utf-8"))
        summary = data[0] if isinstance(data, list) else data
        self.assertEqual(summary["cases"], len(summary["results"]))
        self.assertEqual(summary["cases"], 40)


if __name__ == "__main__":
    unittest.main()
