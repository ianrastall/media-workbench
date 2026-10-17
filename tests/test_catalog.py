import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "workers/python"))
import hub_catalog


class CatalogTests(unittest.TestCase):
    def test_search_encodes_parameters_and_normalizes_results(self):
        urls = []
        def fake(url):
            urls.append(url)
            return [{"id": "owner/model", "downloads": 12, "likes": 4, "pipeline_tag": "text-to-image", "gated": "auto"}]
        result = hub_catalog.search({"query": "a & b", "task": "text-to-image", "limit": 5}, fake)
        self.assertIn("a+%26+b", urls[0])
        self.assertIn("pipeline_tag=text-to-image", urls[0])
        self.assertEqual(result["models"][0]["access"], "Gated access")
        self.assertEqual(result["models"][0]["downloads"], 12)

    def test_invalid_input_never_reaches_network(self):
        def fail(url):
            raise AssertionError("Network should not be used")
        for payload in [{"query": "x" * 201}, {"limit": 500}, {"task": "not-a-task"}]:
            with self.assertRaises(ValueError):
                hub_catalog.search(payload, fail)

    def test_details_restricts_repo_id(self):
        with self.assertRaises(ValueError):
            hub_catalog.details({"repoId": "../outside?token=x"}, lambda url: {})

    def test_details_handles_missing_optional_fields(self):
        result = hub_catalog.details({"repoId": "owner/model"}, lambda url: {"id": "owner/model", "siblings": [{"rfilename": "config.json"}]})
        self.assertEqual(result["revision"], "Not reported")
        self.assertEqual(result["files"], ["config.json"])


if __name__ == "__main__":
    unittest.main()
