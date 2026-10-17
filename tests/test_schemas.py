"""Recipes and live protocol messages conform to schemas/; standard library only."""
import json
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
TYPES = {"object": dict, "array": list, "string": str, "boolean": bool, "null": type(None)}


def errors(value, schema, where="$"):
    """The subset of JSON Schema 2020-12 that schemas/ uses; returns a list of violations."""
    found = []
    if "oneOf" in schema:
        matches = sum(not errors(value, option, where) for option in schema["oneOf"])
        return [] if matches == 1 else [f"{where}: matches {matches} oneOf branches"]
    if "const" in schema and value != schema["const"]:
        found.append(f"{where}: expected {schema['const']!r}")
    if "enum" in schema and value not in schema["enum"]:
        found.append(f"{where}: {value!r} not in {schema['enum']}")
    if "type" in schema:
        names = schema["type"] if isinstance(schema["type"], list) else [schema["type"]]
        def is_type(name):
            if name == "integer":
                return type(value) is int
            if name == "number":
                return type(value) in (int, float)
            return isinstance(value, TYPES[name]) and not (name != "boolean" and type(value) is bool)
        if not any(is_type(name) for name in names):
            return found + [f"{where}: expected {names}"]
    if isinstance(value, str):
        if len(value) < schema.get("minLength", 0) or len(value) > schema.get("maxLength", len(value)):
            found.append(f"{where}: length out of range")
        if "pattern" in schema and not re.search(schema["pattern"], value):
            found.append(f"{where}: does not match {schema['pattern']}")
    if type(value) in (int, float) and "minimum" in schema and value < schema["minimum"]:
        found.append(f"{where}: below {schema['minimum']}")
    if isinstance(value, list):
        if len(value) < schema.get("minItems", 0):
            found.append(f"{where}: too few items")
        for i, item in enumerate(value):
            found += errors(item, schema.get("items", {}), f"{where}[{i}]")
    if isinstance(value, dict):
        found += [f"{where}: missing {key}" for key in schema.get("required", []) if key not in value]
        properties = schema.get("properties", {})
        for key, item in value.items():
            if key in properties:
                found += errors(item, properties[key], f"{where}.{key}")
            elif schema.get("additionalProperties") is False:
                found.append(f"{where}: unexpected {key}")
    return found


def load(name):
    return json.loads((ROOT / name).read_text(encoding="utf-8"))


class SchemaTests(unittest.TestCase):
    def test_bundled_recipes_match_the_recipe_schema(self):
        schema = load("schemas/recipe.schema.json")
        recipes = sorted((ROOT / "recipes").glob("*.json"))
        self.assertTrue(recipes)
        for path in recipes:
            with self.subTest(recipe=path.name):
                self.assertEqual(errors(json.loads(path.read_text(encoding="utf-8")), schema), [])

    def test_validator_rejects_a_broken_recipe(self):
        schema = load("schemas/recipe.schema.json")
        recipe = load("recipes/sd15-baseline.json")
        broken = dict(recipe, status="ready", extra=1, source=dict(recipe["source"], revision="main"))
        del broken["minimumVramMiB"]
        self.assertEqual(len(errors(broken, schema)), 4)

    def test_live_worker_messages_match_the_protocol_schema(self):
        schema = load("schemas/protocol.schema.json")
        worker = subprocess.Popen([sys.executable, "-u", str(ROOT / "workers/python/media_worker.py"), "--root", str(ROOT)],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
        try:
            with tempfile.TemporaryDirectory() as folder:
                messages = [json.loads(worker.stdout.readline())]
                requests = [{"protocol": 1, "id": "catalog", "operation": "catalog.list", "payload": {}},
                            {"protocol": 1, "id": "demo", "operation": "demo.run",
                             "payload": {"outputDirectory": folder, "steps": 2, "delayMs": 0}},
                            {"protocol": 1, "id": "bad", "operation": "generate.image", "payload": {}},
                            {"protocol": 1, "id": "missing", "operation": "no.such.operation", "payload": {}}]
                for request in requests:
                    self.assertEqual(errors(request, schema), [], request["id"])
                    worker.stdin.write(json.dumps(request) + "\n")
                    worker.stdin.flush()
                    while True:
                        message = json.loads(worker.stdout.readline())
                        messages.append(message)
                        if message["kind"] in ("result", "error"):
                            break
        finally:
            worker.stdin.close()
            worker.wait(timeout=8)
            worker.stdout.close()
            worker.stderr.close()
        self.assertEqual([m["kind"] for m in messages].count("progress"), 2)
        for message in messages:
            self.assertEqual(errors(message, schema), [], message)


if __name__ == "__main__":
    unittest.main()
