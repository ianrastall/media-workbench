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


def metadata(repo_id, revision="", fetch=None):
    component = r"[A-Za-z0-9_](?:[A-Za-z0-9_.-]{0,94}[A-Za-z0-9_])?"
    if not isinstance(repo_id, str) or not re.fullmatch(component + "/" + component, repo_id):
        raise ValueError("Use a valid publisher/model repository ID")
    if not isinstance(revision, str) or revision and not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("Use the pinned model revision from its details")
    suffix = "/revision/" + revision if revision else ""
    data = (fetch or fetch_json)("/api/models/" + urllib.parse.quote(repo_id, safe="/") + suffix + "?blobs=true")
    if not isinstance(data, dict) or str(data.get("id", repo_id)).casefold() != repo_id.casefold():
        raise CatalogError("The model repository changed. Search again to load its current source.")
    if revision and data.get("sha") != revision:
        raise CatalogError("The Hub did not return the requested model revision.")
    return data


def describe(data, repo_id):
    card = data.get("cardData") if isinstance(data.get("cardData"), dict) else {}
    license_value = card.get("license") or "Not specified"
    if not isinstance(license_value, str):
        license_value = json.dumps(license_value)
    siblings = data.get("siblings") if isinstance(data.get("siblings"), list) else []
    return {"id": data.get("id", repo_id), "revision": data.get("sha") or "Not reported",
            "task": data.get("pipeline_tag") or "Not specified", "library": data.get("library_name") or "Not specified",
            "license": license_value[:1024], "access": access_label(data.get("gated") or data.get("private") or data.get("disabled")),
            "files": [entry["rfilename"] for entry in siblings
                      if isinstance(entry, dict) and isinstance(entry.get("rfilename"), str)][:200]}


def search(payload, fetch=fetch_json):
    query = payload.get("query", "")
    task = payload.get("task", "")
    limit = payload.get("limit", 30)
    if not isinstance(query, str) or len(query) > 200 or task not in TASKS:
        raise ValueError("Invalid search query or task filter")
    if type(limit) is not int or not 1 <= limit <= 50:
        raise ValueError("Search limit must be between 1 and 50")
    parameters = {"search": query.strip(), "limit": limit, "sort": "downloads", "direction": -1, "full": "true", "cardData": "true"}
    if task:
        parameters["pipeline_tag"] = task
    data = fetch("/api/models?" + urllib.parse.urlencode(parameters))
    if not isinstance(data, list):
        raise CatalogError("Unexpected Hub search response.")
    models = []
    for item in data:
        if not isinstance(item, dict) or not isinstance(item.get("id"), str):
            continue
        card = item.get("cardData") if isinstance(item.get("cardData"), dict) else {}
        license_value = card.get("license")
        has_file = any(isinstance(entry, dict) and isinstance(entry.get("rfilename"), str)
                       and "/" not in entry["rfilename"] and entry["rfilename"].endswith(".safetensors")
                       for entry in item.get("siblings") or [])
        hint = "Model file listed — check this model." if has_file else "No model download listed; inspect the source."
        if item.get("pipeline_tag") in ("text-to-video", "image-to-video"):
            hint = "Video is not supported yet."
        models.append({"id": item["id"], "downloads": int(item.get("downloads") or 0),
                       "likes": int(item.get("likes") or 0), "task": item.get("pipeline_tag") or "Task not specified",
                       "library": item.get("library_name") or "Library not specified",
                       "access": access_label(item.get("gated")),
                       "license": license_value[:1024] if isinstance(license_value, str) else "Not specified", "hint": hint})
    return {"models": models, "source": "Hugging Face public API"}


def details(payload, fetch=fetch_json):
    repo_id = payload.get("repoId", "")
    return describe(metadata(repo_id, fetch=fetch), repo_id)
