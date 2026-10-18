"""Optional real public-network smoke check. Never executed by offline unit tests."""
from pathlib import Path
import sys
import json
import threading
sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "workers/python"))
import hub_catalog
import hub_models

models = hub_catalog.search({"query": "stable-diffusion", "task": "text-to-image", "limit": 3})["models"]
assert models, "Hub search returned no models"
model = hub_catalog.details({"repoId": models[0]["id"]})
assert model["id"] and model["files"], "Hub model detail did not include files"
print(f"PASS: live public search returned {len(models)} models; inspected {model['id']}")
root = Path(__file__).resolve().parents[1]
manifest = json.loads((root / "profiles/sd15-files.json").read_text(encoding="utf-8"))
metadata = hub_catalog.metadata(manifest["repoId"], manifest["revision"])
detail = hub_catalog.describe(metadata, manifest["repoId"])
plan = hub_models.native_plan(root, metadata, detail)
assert not plan["blocker"] and plan["files"], "Pinned public source could not form a native download plan"
own_vae = hub_models.identify_remote(detail["id"], detail["revision"], plan["files"][0], threading.Event())
print(f"PASS: bounded remote SD 1.5 header check at {detail['revision']} (own VAE: {own_vae}); no full weights downloaded")
