"""Optional real public-network smoke check. Never executed by offline unit tests."""
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "workers/python"))
import hub_catalog

models = hub_catalog.search({"query": "stable-diffusion", "task": "text-to-image", "limit": 3})["models"]
assert models, "Hub search returned no models"
model = hub_catalog.details({"repoId": models[0]["id"]})
assert model["id"] and model["files"], "Hub model detail did not include files"
print(f"PASS: live public search returned {len(models)} models; inspected {model['id']}")
