"""Summarize where Media Workbench's own process tree changed files during a traced acceptance run.

Usage: python summarize_writes.py <run folder>

The run folder holds run.json (written by acceptance.py: data folder, app folder, phases, space samples) and
one Process Monitor CSV export per phase in procmon/. Each phase starts a fresh MediaWorkbench.Cli.exe, so a
phase's tree is rebuilt from its own Process Create events: the tree is every process created by a tracked
process, starting from MediaWorkbench.Cli.exe. Only successful changes count: data written, files created or
replaced, files opened for writing, renames, deletions, size and metadata changes. Paths are grouped by area:
the data folder (by its first subfolder), the app folder, devices and pipes, and anywhere else by its leading
folders with the user's profile replaced by environment-variable names. Writes report.json and report.md.
"""
import csv
import json
import os
from pathlib import Path
import re
import sys
from collections import defaultdict

ROOT_IMAGE = "\\mediaworkbench.cli.exe"
CHANGE_OPERATIONS = {
    "SetRenameInformationFile": "rename", "SetRenameInformationEx": "rename",
    "SetDispositionInformationFile": "delete", "SetDispositionInformationEx": "delete",
    "SetEndOfFileInformationFile": "resize", "SetAllocationInformationFile": "resize",
    "SetValidDataLengthInformationFile": "resize", "SetBasicInformationFile": "metadata",
    "SetSecurityFile": "metadata", "SetLinkInformationFile": "link", "SetHardLinkInformationFile": "link",
}
WRITE_ACCESS = ("Write", "Append", "Delete")
CREATED = ("Created", "Overwritten", "Superseded")


def field(detail, name):
    match = re.search(re.escape(name) + r": ([^,]*(?:, [^:,]*(?=,|$))*)", detail)
    return match.group(1).strip() if match else ""


def classify(operation, result, detail):
    """(kind, bytes) for a successful change, or None for anything that leaves files as they were."""
    if result != "SUCCESS":
        return None
    if operation == "WriteFile":
        match = re.search(r"Length: ([\d,]+)", detail)
        return "write", int(match.group(1).replace(",", "")) if match else 0
    if operation == "CreateFile":
        opened = field(detail, "OpenResult")
        if opened in CREATED:
            return "create", 0
        access = field(detail, "Desired Access")
        return ("open-write", 0) if any(word in access for word in WRITE_ACCESS) else None
    if operation in CHANGE_OPERATIONS:
        kind = CHANGE_OPERATIONS[operation]
        if kind == "delete" and "Delete: True" not in detail and "FILE_DISPOSITION_DELETE" not in detail:
            return None
        return kind, 0
    if operation == "FileSystemControl" and "FSCTL_SET" in detail:
        return "fsctl", 0
    if operation.startswith("Set") and operation.endswith(("File", "Volume")):
        return "metadata", 0
    return None


def anonymize(path, environment):
    lowered = path.lower()
    for name, folder in environment:
        if lowered == folder or lowered.startswith(folder + "\\"):
            return f"%{name}%" + path[len(folder):]
    return path


def area_of(path, data_root, app_root, environment):
    """A stable grouping key: data/<subfolder>, app/<subfolder>, device, or a leading-folder prefix."""
    clean = path[4:] if path.startswith("\\\\?\\") else path
    lowered = clean.lower()
    if not re.match(r"^[a-z]:\\", lowered):
        return "device"
    for label, root in (("data", data_root), ("app", app_root)):
        if lowered == root or lowered.startswith(root + "\\"):
            rest = clean[len(root) + 1:].split("\\")
            return f"{label}/{rest[0]}" if rest and rest[0] and len(rest) > 1 else f"{label}/(top level)"
    shown = anonymize(clean, environment)
    parts = shown.split("\\")
    depth = 3 if shown.startswith("%") else 4
    return "\\".join(parts[:depth])


def summarize_phase(rows, data_root, app_root, environment):
    tracked, names = set(), {}
    areas = defaultdict(lambda: {"bytes": 0, "kinds": defaultdict(int), "created": 0, "processes": set(), "examples": []})
    for row in rows:
        operation, detail = row.get("Operation", ""), row.get("Detail", "")
        try:
            pid = int(row.get("PID", "0"))
        except ValueError:
            continue
        path = row.get("Path", "")
        if operation == "Process Create":
            child = re.search(r"PID: (\d+)", detail)
            if child and (pid in tracked or path.lower().endswith(ROOT_IMAGE)):
                tracked.add(int(child.group(1)))
                names[int(child.group(1))] = path.rsplit("\\", 1)[-1]
            continue
        if pid not in tracked:
            continue
        if operation == "Process Exit":
            tracked.discard(pid)
            continue
        change = classify(operation, row.get("Result", ""), detail)
        if change is None:
            continue
        kind, size = change
        targets = [path]
        if kind == "rename" and (renamed := field(detail, "FileName")):
            targets.append(renamed)
        for target in targets:
            entry = areas[area_of(target, data_root, app_root, environment)]
            entry["bytes"] += size
            entry["kinds"][kind] += 1
            entry["created"] += kind == "create"
            entry["processes"].add(row.get("Process Name") or names.get(pid, "?"))
            shown = anonymize(target, environment)
            if len(entry["examples"]) < 5 and shown not in entry["examples"]:
                entry["examples"].append(shown)
    return {key: {"bytes": value["bytes"], "kinds": dict(value["kinds"]), "created": value["created"],
                  "processes": sorted(value["processes"]), "examples": value["examples"]} for key, value in sorted(areas.items())}


def read_rows(csv_path):
    with open(csv_path, encoding="utf-8-sig", errors="replace", newline="") as source:
        yield from csv.DictReader(source)


def environment_folders():
    found = []
    for name in ("TEMP", "LOCALAPPDATA", "APPDATA", "ProgramData", "windir", "USERPROFILE"):
        value = os.environ.get(name)
        if value:
            found.append((name, os.path.normpath(value).lower().rstrip("\\")))
    # Longest first, so %TEMP% wins over %LOCALAPPDATA% and %LOCALAPPDATA% over %USERPROFILE%.
    return sorted(found, key=lambda item: -len(item[1]))


def outside(areas):
    return {key: value for key, value in areas.items() if not key.startswith(("data/", "device"))}


def mib(value):
    return f"{value / 1024 ** 2:,.1f} MiB"


def markdown(run, phases):
    lines = [f"# Drive-containment trace", "", f"App version: {run.get('appVersion', '?')}. Trace mode: {run.get('traceMode', '?')}.",
             f"Data folder volume: {run['dataVolume']}. App folder volume: {run['appVolume']}.", ""]
    for phase in phases:
        lines += [f"## {phase['name']}", "", phase.get("outcome", ""), ""]
        space = phase.get("space") or {}
        if space:
            lines.append("Lowest free space during the phase: " + ", ".join(f"{volume} {mib(free)}" for volume, free in sorted(space.get("minFree", {}).items()))
                         + f"; largest data folder temp/: {mib(space.get('maxTempBytes', 0))}.")
            lines.append("")
        rows = phase["areas"]
        if not rows:
            lines += ["No changes by the app's process tree were recorded.", ""]
            continue
        lines += ["| Area | Bytes written | Changes | Created | Processes | Examples |", "|---|---|---|---|---|---|"]
        for key, value in rows.items():
            kinds = ", ".join(f"{kind} {count}" for kind, count in sorted(value["kinds"].items()))
            examples = "<br>".join(value["examples"][:3]).replace("|", "\\|")
            lines.append(f"| {key} | {mib(value['bytes'])} | {kinds} | {value['created']} | {', '.join(value['processes'])} | {examples} |")
        lines.append("")
    total = defaultdict(int)
    for phase in phases:
        for key, value in outside(phase["areas"]).items():
            total[key] += value["bytes"]
    lines += ["## Outside the data folder, all phases", ""]
    lines += [f"- {key}: {mib(value)}" for key, value in sorted(total.items())] or ["- Nothing."]
    return "\n".join(lines) + "\n"


def main():
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    run_folder = Path(sys.argv[1])
    run = json.loads((run_folder / "run.json").read_text(encoding="utf-8"))
    data_root = os.path.normpath(run["dataRoot"]).lower().rstrip("\\")
    app_root = os.path.normpath(run["appRoot"]).lower().rstrip("\\")
    environment = environment_folders()
    phases = []
    for phase in run["phases"]:
        csv_path = run_folder / "procmon" / f"{phase['name']}.csv"
        areas = summarize_phase(read_rows(csv_path), data_root, app_root, environment) if csv_path.is_file() else {}
        phases.append({**phase, "areas": areas, "traced": csv_path.is_file()})
    (run_folder / "report.json").write_text(json.dumps({"run": run, "phases": phases}, indent=2), encoding="utf-8")
    (run_folder / "report.md").write_text(markdown(run, phases), encoding="utf-8")
    print((run_folder / "report.md").read_text(encoding="utf-8"))


if __name__ == "__main__":
    main()
