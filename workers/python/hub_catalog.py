"""Read-only public Hugging Face discovery through documented HTTPS API endpoints."""
import json
import re
import urllib.error
import urllib.parse
import urllib.request

BASE = "https://huggingface.co"
TASKS = {"", "text-to-image", "image-to-image", "text-to-video", "image-to-video"}
MAX_RESPONSE = 16 * 1024 * 1024


class CatalogError(Exception):
    pass


def fetch_json(path):
    request = urllib.request.Request(BASE + path, headers={
        "User-Agent": "MediaWorkbench/0.1.0 (public-model-discovery)", "Accept": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            content = response.read(MAX_RESPONSE + 1)
            if len(content) > MAX_RESPONSE:
                raise CatalogError("The Hub returned too much data. Narrow the search.")
            return json.loads(content)
    except urllib.error.HTTPError as exc:
        if exc.code in (401, 403):
            raise CatalogError("This resource requires account access. Authenticated discovery is a later milestone.") from exc
        if exc.code == 429:
            raise CatalogError("Hugging Face is rate limiting requests. Please try again shortly.") from exc
        if exc.code == 404:
            raise CatalogError("The model was not found or is not publicly accessible.") from exc
        raise CatalogError(f"Hugging Face returned HTTP {exc.code}.") from exc
    except (urllib.error.URLError, TimeoutError, OSError, ValueError) as exc:
        raise CatalogError("Could not reach Hugging Face. Check your connection and try again.") from exc


def access_label(value):
    return "Gated access" if value else "Public"


def search(payload, fetch=fetch_json):
    query = payload.get("query", "")
    task = payload.get("task", "")
    limit = payload.get("limit", 30)
    if not isinstance(query, str) or len(query) > 200 or task not in TASKS:
        raise ValueError("Invalid search query or task filter")
    if type(limit) is not int or not 1 <= limit <= 50:
        raise ValueError("Search limit must be between 1 and 50")
    parameters = {"search": query.strip(), "limit": limit, "sort": "downloads", "direction": -1}
    if task:
        parameters["pipeline_tag"] = task
    data = fetch("/api/models?" + urllib.parse.urlencode(parameters))
    if not isinstance(data, list):
        raise CatalogError("Unexpected Hub search response.")
    models = []
    for item in data:
        if not isinstance(item, dict) or not isinstance(item.get("id"), str):
            continue
        models.append({"id": item["id"], "downloads": int(item.get("downloads") or 0),
                       "likes": int(item.get("likes") or 0), "task": item.get("pipeline_tag") or "Task not specified",
                       "library": item.get("library_name") or "Library not specified",
                       "access": access_label(item.get("gated"))})
    return {"models": models, "source": "Hugging Face public API"}


def details(payload, fetch=fetch_json):
    repo_id = payload.get("repoId", "")
    if not isinstance(repo_id, str) or not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repo_id):
        raise ValueError("Use a valid publisher/model repository ID")
    data = fetch("/api/models/" + urllib.parse.quote(repo_id, safe="/"))
    card = data.get("cardData") or {}
    license_value = card.get("license") or "Not specified"
    if not isinstance(license_value, str):
        license_value = json.dumps(license_value)
    return {"id": data.get("id", repo_id), "revision": data.get("sha") or "Not reported",
            "task": data.get("pipeline_tag") or "Not specified", "library": data.get("library_name") or "Not specified",
            "license": license_value, "access": access_label(data.get("gated")),
            "files": [entry["rfilename"] for entry in data.get("siblings", [])
                      if isinstance(entry, dict) and isinstance(entry.get("rfilename"), str)][:200]}
