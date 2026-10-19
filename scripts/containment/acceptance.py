"""Drive-containment acceptance run: every app operation through the installed app's own MCP host and CLI.

Usage (normally started by scripts/containment/trace.ps1):
    python -I -B acceptance.py --run <run folder> --app <installed app folder> [--control <folder>] [--quick]

Each phase starts a fresh MediaWorkbench.Cli.exe (so the shipped C# supervisor sets every worker's temporary
and cache folders, as in the app) against a new data folder in <run>/data, and waits for it to exit before the
phase ends. With --control, the elevated trace controller starts a Process Monitor capture before each phase
and stops and exports it afterwards. Free space on every fixed drive and the size of the data folder's temp/
are sampled every five seconds. Results go to <run>/run.json; summarize_writes.py turns the traces into a report.
--quick runs only read-only phases (no downloads, no GPU), for checking the tooling.
"""
import argparse
import json
import os
from pathlib import Path
import queue
import shutil
import string
import subprocess
import sys
import threading
import time

PROTOCOL = "2025-11-25"


class Host:
    """A minimal MCP stdio client for MediaWorkbench.Cli.exe mcp."""

    def __init__(self, app, data):
        self.process = subprocess.Popen([str(app / "MediaWorkbench.Cli.exe"), "mcp", "--data-root", str(data), "--allow-setup", "--allow-inference"],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
        self.messages = queue.Queue()
        self.next_id = 0
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=self._drain_errors, daemon=True).start()
        self.request("initialize", {"protocolVersion": PROTOCOL, "capabilities": {}, "clientInfo": {"name": "containment-acceptance", "version": "1"}})
        self.notify("notifications/initialized", {})

    def _read(self):
        for line in self.process.stdout:
            if line.strip():
                self.messages.put(json.loads(line))
        self.messages.put(None)

    def _drain_errors(self):
        for line in self.process.stderr:
            print("   host:", line.rstrip(), flush=True)

    def notify(self, method, params):
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": method, "params": params}) + "\n")
        self.process.stdin.flush()

    def send(self, method, params):
        self.next_id += 1
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.next_id, "method": method, "params": params}) + "\n")
        self.process.stdin.flush()
        return self.next_id

    def wait(self, request_id, on_progress=None):
        stage = None
        while True:
            message = self.messages.get()
            if message is None:
                raise RuntimeError("The MCP host exited before answering.")
            if message.get("method") == "notifications/progress":
                text = message["params"].get("message")
                if text != stage:
                    stage = text
                    print("    ", stage, flush=True)
                if on_progress:
                    on_progress(text)
            elif message.get("id") == request_id:
                if "error" in message:
                    raise RuntimeError(message["error"]["message"])
                return message["result"]

    def request(self, method, params):
        return self.wait(self.send(method, params))

    def tool(self, name, arguments=None, expect_error=False):
        print(f"   {name} {json.dumps(arguments or {})}", flush=True)
        result = self.request("tools/call", {"name": name, "arguments": arguments or {}, "_meta": {"progressToken": name}})
        if result.get("isError") and not expect_error:
            raise RuntimeError(f"{name}: {result['content'][0]['text']}")
        return result.get("structuredContent") or {"text": result["content"][0]["text"], "isError": result.get("isError", False)}

    def close(self, timeout=120):
        try:
            self.process.stdin.close()
        except OSError:
            pass
        self.process.wait(timeout=timeout)


class Sampler(threading.Thread):
    """Lowest free space per fixed drive and the largest temp/ size, per phase; also the controller heartbeat."""

    def __init__(self, data, control):
        super().__init__(daemon=True)
        self.data, self.control, self.phase, self.lock = data, control, None, threading.Lock()
        self.stats = {}
        self.drives = [f"{letter}:\\" for letter in string.ascii_uppercase if os.path.exists(f"{letter}:\\") and _fixed(f"{letter}:\\")]

    def run(self):
        while True:
            with self.lock:
                phase = self.phase
            if phase:
                stats = self.stats.setdefault(phase, {"minFree": {}, "maxTempBytes": 0})
                for drive in self.drives:
                    try:
                        free = shutil.disk_usage(drive).free
                    except OSError:
                        continue
                    key = drive[:2]
                    stats["minFree"][key] = min(stats["minFree"].get(key, free), free)
                stats["maxTempBytes"] = max(stats["maxTempBytes"], folder_bytes(self.data / "temp"))
            if self.control:
                (self.control / "heartbeat").write_text(str(time.time()), encoding="ascii")
            time.sleep(5)


def _fixed(drive):
    import ctypes
    return ctypes.windll.kernel32.GetDriveTypeW(drive) == 3  # DRIVE_FIXED


def folder_bytes(folder):
    total = 0
    for base, _, files in os.walk(folder):
        for name in files:
            try:
                total += os.stat(os.path.join(base, name)).st_size
            except OSError:
                pass
    return total


def owned_processes(app, data):
    """Processes still running from the app's bundled runtime or the data folder (should be none after a host exits)."""
    def literal(prefix):
        return "'" + prefix.replace("'", "''") + "'"
    # The bundled runtime, the app's own executables (MediaWorkbench*.exe), and anything run from the data folder.
    roots = [str(app / "runtime") + "\\", str(app / "MediaWorkbench"), str(data) + "\\"]
    script = (f"$roots = @({', '.join(literal(root) for root in roots)}); "
              "Get-CimInstance Win32_Process | Where-Object { $path = $_.ExecutablePath; $path -and ($roots | Where-Object { $path.StartsWith($_, 'OrdinalIgnoreCase') }) }"
              " | ForEach-Object { \"$($_.ProcessId) $($_.Name)\" }")
    found = subprocess.run(["powershell", "-NoProfile", "-Command", script], capture_output=True, text=True)
    if found.returncode != 0:
        raise RuntimeError("Could not list processes: " + found.stderr.strip())
    return [line for line in found.stdout.splitlines() if line.strip()]


def with_host(app, data, action):
    host = Host(app, data)
    try:
        return action(host)
    finally:
        host.close()


# Phases: each returns a one-line outcome and records the job ids later phases use.
def inspect(app, data, state):
    def run(host):
        health = host.tool("workbench_health")
        status = host.tool("workbench_model_status")
        storage = host.tool("workbench_storage")
        return f"Health read; model status {status.get('status')}; storage summary read ({len(json.dumps(storage))} bytes). " + health["summary"].splitlines()[0]
    return with_host(app, data, run)


def setup_sd15(app, data, state):
    return with_host(app, data, lambda host: "SD 1.5 " + _installed(host.tool("workbench_install_model")))


def _installed(result):
    if result.get("status") != "ready":
        raise RuntimeError(f"Setup ended {result.get('status')}: {result.get('message')}")
    return f"ready: {result.get('message')}"


def pictures_sd15(app, data, state):
    def run(host):
        first = host.tool("workbench_generate_image", {"prompt": "A small red ceramic house beside a green tree, watercolor illustration, daylight", "seed": 20261017, "steps": 20})
        state["sd15"] = first["id"]
        restyled = host.tool("workbench_generate_image", {"prompt": "The same house at night under a starry sky", "seed": 7, "steps": 20, "sourceJobId": first["id"], "strength": 0.6})
        painted = host.tool("workbench_generate_image", {"prompt": "a yellow door", "seed": 8, "steps": 20, "sourceJobId": first["id"],
                                                         "changeLeft": 0.4, "changeTop": 0.5, "changeWidth": 0.2, "changeHeight": 0.3})
        bigger = host.tool("workbench_upscale_image", {"sourceJobId": first["id"], "scale": 4})
        return (f"Text {first['width']}x{first['height']} in {first['seconds']} s; image-to-image {restyled['seconds']} s; painted change {painted['seconds']} s; "
                f"4x upscale to {bigger['width']}x{bigger['height']} in {bigger['seconds']} s.")
    return with_host(app, data, run)


def setup_xl(app, data, state):
    return with_host(app, data, lambda host: "SDXL " + _installed(host.tool("workbench_install_xl")))


def pictures_xl(app, data, state):
    def run(host):
        first = host.tool("workbench_generate_image", {"prompt": "A lighthouse on a rocky coast at sunset, oil painting", "seed": 20261018, "steps": 12,
                                                       "model": "sdxl-base", "width": 1216, "height": 832})
        painted = host.tool("workbench_generate_image", {"prompt": "a full moon in the sky", "seed": 7, "steps": 12, "model": "sdxl-base", "width": 1216, "height": 832,
                                                         "sourceJobId": first["id"], "changeLeft": 0.55, "changeTop": 0.1, "changeWidth": 0.3, "changeHeight": 0.35})
        return f"SDXL {first['width']}x{first['height']} in {first['seconds']} s; painted change {painted['seconds']} s."
    return with_host(app, data, run)


def setup_video(app, data, state):
    return with_host(app, data, lambda host: "Videos " + _installed(host.tool("workbench_install_video")))


def video(app, data, state):
    def run(host):
        clip = host.tool("workbench_generate_video", {"prompt": "Ocean waves rolling onto a sandy beach at sunset, cinematic", "seed": 20261018, "steps": 12, "frames": 16})
        if not Path(clip["videoPath"]).is_file():
            raise RuntimeError("The video file is missing.")
        return f"{clip['frames']}-frame video in {clip['seconds']} s."
    return with_host(app, data, run)


def discover(app, data, state):
    def run(host):
        found = host.tool("workbench_search_models", {"query": "stable diffusion 1.5", "task": "text-to-image"})
        details = host.tool("workbench_model_details", {"repoId": "stable-diffusion-v1-5/stable-diffusion-v1-5"})
        return f"Search returned {len(found.get('models', []))} models; details read for {details.get('id') or details.get('repoId') or 'the repository'}."
    return with_host(app, data, run)


def _when_generating(host, request_id, seconds=30):
    """Wait until the request reports inference (or a time limit), leaving its answer unread."""
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            message = host.messages.get(timeout=1)
        except queue.Empty:
            continue
        if message is None or message.get("id") == request_id:
            raise RuntimeError("The picture finished or the host exited before it could be interrupted.")
        if message.get("method") == "notifications/progress" and "generat" in (message["params"].get("message") or "").lower():
            time.sleep(3)
            return


def cancel(app, data, state):
    def run(host):
        request = host.send("tools/call", {"name": "workbench_generate_image", "arguments": {"prompt": "A harbour at dawn", "seed": 30, "steps": 50},
                                           "_meta": {"progressToken": "cancel"}})
        _when_generating(host, request)
        host.notify("notifications/cancelled", {"requestId": request, "reason": "containment acceptance"})
        result = host.wait(request)
        if not result.get("isError"):
            raise RuntimeError("The picture was not cancelled.")
        unfinished = host.tool("workbench_library", {"query": "", "kind": "generate", "limit": 5})
        return "Cancelled: " + result["content"][0]["text"] + f" Library unfinished: {_unfinished(unfinished)}."
    return with_host(app, data, run)


def _unfinished(library):
    jobs = library.get("problems") or []
    return ", ".join(f"{job.get('status')}" for job in jobs[:3]) + f" (recovered {library.get('recovered', 0)})" if jobs else "none listed"


def crash(app, data, state):
    host = Host(app, data)
    request = host.send("tools/call", {"name": "workbench_generate_image", "arguments": {"prompt": "A windmill in a tulip field", "seed": 31, "steps": 50},
                                       "_meta": {"progressToken": "crash"}})
    _when_generating(host, request)
    host.process.kill()  # Only the host: its kill-on-close Job Object must end the workers.
    host.process.wait(timeout=60)
    time.sleep(5)
    left = owned_processes(app, data)
    def recover(fresh):
        status = fresh.tool("workbench_model_status")
        library = fresh.tool("workbench_library", {"query": "", "kind": "generate", "limit": 5})
        return status, library
    status, library = with_host(app, data, recover)
    if left:
        raise RuntimeError("Processes outlived the killed host: " + "; ".join(left))
    return f"Killed the host mid-picture; no owned process outlived it; model status {status.get('status')}; Library unfinished: {_unfinished(library)}."


def reverify(app, data, state):
    return with_host(app, data, lambda host: "Install / verify again " + _installed(host.tool("workbench_install_model")))


def update_download(app, data, state):
    python = app / "runtime" / "python" / "python.exe"
    done = subprocess.run([str(app / "MediaWorkbench.Cli.exe"), str(python), str(app), "update-check", "0.3.0-alpha.8", "--download", str(data)],
                          capture_output=True, text=True, timeout=1800)
    if done.returncode != 0:
        raise RuntimeError("update-check failed: " + (done.stderr or done.stdout).strip())
    kept = sorted(path.name for path in (data / "temp" / "updates").glob("*")) if (data / "temp" / "updates").is_dir() else []
    return done.stdout.strip().splitlines()[-1].split(":", 1)[0] + f"; temp/updates holds {kept or 'nothing'}."


def storage(app, data, state):
    summary = with_host(app, data, lambda host: host.tool("workbench_storage"))
    state["storage"] = summary
    return f"Data folder {folder_bytes(data) / 1024 ** 3:.1f} GiB after the run."


# Drive-failure checks: the data folder is on the controller's test virtual disk. Filler files outside the data
# folder (written by this driver, so never counted as the app's) bring free space just below each requirement.
def _filler(data):
    return Path(data.anchor) / "filler.bin"


def fill(data, leave):
    filler = _filler(data)
    filler.unlink(missing_ok=True)
    size = max(0, shutil.disk_usage(data.anchor).free - leave)
    allocate(filler, size)
    return shutil.disk_usage(data.anchor).free


def allocate(path, size):
    """Reserve `size` bytes with SetEndOfFile, which allocates without writing (Python's truncate writes zeros)."""
    import ctypes
    from ctypes import wintypes
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.CreateFileW.restype = wintypes.HANDLE
    kernel.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    kernel.SetFilePointerEx.argtypes = [wintypes.HANDLE, ctypes.c_longlong, ctypes.POINTER(ctypes.c_longlong), wintypes.DWORD]
    kernel.SetEndOfFile.argtypes = [wintypes.HANDLE]
    kernel.CloseHandle.argtypes = [wintypes.HANDLE]
    handle = kernel.CreateFileW(str(path), 0x40000000, 0, None, 2, 0x80, None)  # GENERIC_WRITE, CREATE_ALWAYS, NORMAL
    if handle in (None, wintypes.HANDLE(-1).value):
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        if not kernel.SetFilePointerEx(handle, size, None, 0) or not kernel.SetEndOfFile(handle):
            raise ctypes.WinError(ctypes.get_last_error())
    finally:
        kernel.CloseHandle(handle)


def unfill(data):
    _filler(data).unlink(missing_ok=True)


def vhd(control, verb):
    marker = control / ("vhd-detached" if verb == "detach" else "vhd-attached")
    marker.unlink(missing_ok=True)
    (control / f"vhd-{verb}.request").write_text("", encoding="ascii")
    deadline = time.time() + 300
    while not marker.exists():
        if (control / "controller-failed").exists():
            raise RuntimeError("The trace controller stopped: " + (control / "controller-failed").read_text(encoding="utf-8"))
        if time.time() > deadline:
            raise RuntimeError(f"The test disk did not {verb}.")
        time.sleep(1)


def _expect_refusal(result, words):
    if not result.get("isError"):
        raise RuntimeError("The operation was not refused.")
    if not any(word in result["text"].lower() for word in words):
        raise RuntimeError("Refused for another reason: " + result["text"])
    return result["text"]


def _jobs(data):
    return sorted(path.name for path in (data / "jobs").glob("*.json") if not path.name.startswith("install-")) if (data / "jobs").is_dir() else []


def setup_low_space(app, data, state):
    free = fill(data, 10 * 1024 ** 3)
    data.mkdir(parents=True, exist_ok=True)
    text = with_host(app, data, lambda host: _expect_refusal(host.tool("workbench_install_model", expect_error=True), ["free space"]))
    written = folder_bytes(data)
    if written > 50 * 1024 ** 2:
        raise RuntimeError(f"{written / 1024 ** 2:.0f} MiB was written before the refusal.")
    return f"With {free / 1024 ** 3:.1f} GiB free, setup refused: {text} The data folder holds {written / 1024:.0f} KiB."


def setup_on_disk(app, data, state):
    unfill(data)
    return with_host(app, data, lambda host: "SD 1.5 on the test disk " + _installed(host.tool("workbench_install_model")))


def generate_low_space(app, data, state):
    before = _jobs(data)
    free = fill(data, 150 * 1024 ** 2)
    text = with_host(app, data, lambda host: _expect_refusal(host.tool("workbench_generate_image", {"prompt": "A lighthouse", "seed": 1, "steps": 12}, expect_error=True), ["free space"]))
    if _jobs(data) != before:
        raise RuntimeError("A job record was written although the picture was refused.")
    python = app / "runtime" / "python" / "python.exe"
    update = subprocess.run([str(app / "MediaWorkbench.Cli.exe"), str(python), str(app), "update-check", "0.3.0-alpha.8", "--download", str(data)],
                            capture_output=True, text=True, timeout=1800)
    if update.returncode == 0:
        raise RuntimeError("The update download was not refused.")
    kept = list((data / "temp" / "updates").glob("*")) if (data / "temp" / "updates").is_dir() else []
    if kept:
        raise RuntimeError(f"The refused update left {[path.name for path in kept]}.")
    return (f"With {free / 1024 ** 2:.0f} MiB free, the picture was refused with no job record: {text} "
            f"The update download was refused too: {(update.stderr or update.stdout).strip()[:300]}")


def generate_after_space(app, data, state):
    unfill(data)
    picture = with_host(app, data, lambda host: host.tool("workbench_generate_image", {"prompt": "A lighthouse at dusk", "seed": 2, "steps": 12}))
    return f"After freeing space, a picture was made in {picture['seconds']} s."


def disconnected(app, data, state):
    vhd(state["control"], "detach")
    if data.exists():
        raise RuntimeError("The data folder is still reachable after detaching the disk.")
    def run(host):
        status = host.tool("workbench_model_status", expect_error=True)
        picture = host.tool("workbench_generate_image", {"prompt": "A lighthouse", "seed": 3, "steps": 12}, expect_error=True)
        return status, picture
    status, picture = with_host(app, data, run)
    if not picture.get("isError"):
        raise RuntimeError("A picture was made with the data folder's drive disconnected.")
    python = app / "runtime" / "python" / "python.exe"
    update = subprocess.run([str(app / "MediaWorkbench.Cli.exe"), str(python), str(app), "update-check", "0.3.0-alpha.8", "--download", str(data)],
                            capture_output=True, text=True, timeout=1800)
    if update.returncode == 0:
        raise RuntimeError("The update download did not stop with the drive disconnected.")
    shown = status.get("text") or status.get("message") or status.get("status")
    return (f"Model status: {shown}. Picture: {picture['text']} Update download: {(update.stderr or update.stdout).strip()[:300]}")


def disconnected_mid_picture(app, data, state):
    vhd(state["control"], "attach")
    host = Host(app, data)
    try:
        request = host.send("tools/call", {"name": "workbench_generate_image", "arguments": {"prompt": "A windmill in a tulip field", "seed": 4, "steps": 50},
                                           "_meta": {"progressToken": "unplug"}})
        _when_generating(host, request)
        vhd(state["control"], "detach")
        answer = queue.Queue()
        threading.Thread(target=lambda: answer.put(host.wait(request)), daemon=True).start()
        try:
            result = answer.get(timeout=900)
        except queue.Empty:
            raise RuntimeError("The picture neither failed nor finished within 15 minutes of unplugging the drive.")
    finally:
        try:
            host.close(timeout=120)
        except subprocess.TimeoutExpired:
            host.process.kill()
            raise RuntimeError("The MCP host did not exit after the drive was unplugged.")
    left = owned_processes(app, data)
    if left:
        raise RuntimeError("Processes outlived the host: " + "; ".join(left))
    message = result["content"][0]["text"] if result.get("isError") else "the picture was saved"
    vhd(state["control"], "attach")
    def recover(fresh):
        status = fresh.tool("workbench_model_status")
        library = fresh.tool("workbench_library", {"query": "", "kind": "generate", "limit": 5})
        again = fresh.tool("workbench_generate_image", {"prompt": "A windmill at noon", "seed": 5, "steps": 12})
        return status, library, again
    status, library, again = with_host(app, data, recover)
    return (f"Unplugged mid-picture: {message[:300]}; no process left behind. Plugged back in: model status {status.get('status')}, "
            f"Library unfinished: {_unfinished(library)}; a new picture took {again['seconds']} s.")


FULL = [setup_sd15, pictures_sd15, setup_xl, pictures_xl, setup_video, video, discover, cancel, crash, reverify, update_download, storage]
QUICK = [inspect, discover, storage]
DRIVE_FAILURE = [setup_low_space, setup_on_disk, generate_low_space, generate_after_space, disconnected, disconnected_mid_picture]


def request_trace(control, verb, phase, timeout):
    if not control:
        return
    (control / f"{verb}-{phase}.request").write_text("", encoding="ascii")
    marker = control / f"{phase}.{'started' if verb == 'start' else 'stopped'}"
    deadline = time.time() + timeout
    while not marker.exists():
        failed = control / "controller-failed"
        if failed.exists():
            raise RuntimeError("The trace controller stopped: " + failed.read_text(encoding="utf-8"))
        if time.time() > deadline:
            raise RuntimeError(f"The trace controller did not confirm {verb} {phase}.")
        time.sleep(1)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--run", required=True, type=Path)
    parser.add_argument("--app", required=True, type=Path)
    parser.add_argument("--control", type=Path)
    parser.add_argument("--quick", action="store_true")
    parser.add_argument("--drive-failure", action="store_true", help="use the controller's test disk (needs --control)")
    options = parser.parse_args()
    run, app = options.run.resolve(), options.app.resolve()
    data = run / "data"
    if options.drive_failure:
        letter = json.loads((options.control / "controller-status.json").read_text(encoding="utf-8")).get("vhdLetter") if options.control else None
        if not letter:
            raise SystemExit("--drive-failure needs the trace controller's test disk.")
        data = Path(f"{letter}:\\") / "Media Workbench"
    data.mkdir(parents=True, exist_ok=True)
    version = subprocess.run(["powershell", "-NoProfile", "-Command", f"(Get-Item -LiteralPath '{app / 'MediaWorkbench.exe'}').VersionInfo.ProductVersion"],
                             capture_output=True, text=True).stdout.strip()
    trace_mode = "none"
    if options.control and (status := options.control / "controller-status.json").exists():
        trace_mode = json.loads(status.read_text(encoding="utf-8")).get("mode", "?")
    record = {"dataRoot": str(data), "appRoot": str(app), "appVersion": version, "traceMode": trace_mode,
              "dataVolume": str(data)[:2], "appVolume": str(app)[:2], "phases": []}
    sampler = Sampler(data, options.control)
    sampler.start()
    state = {"control": options.control}
    failed = False
    try:
        for phase in DRIVE_FAILURE if options.drive_failure else QUICK if options.quick else FULL:
            name = phase.__name__.replace("_", "-")
            print(f"== {name}", flush=True)
            request_trace(options.control, "start", name, 600)
            with sampler.lock:
                sampler.phase = name
            started = time.time()
            try:
                outcome = "PASS: " + phase(app, data, state)
            except Exception as error:  # Record and stop: later phases depend on earlier ones.
                outcome, failed = f"FAIL: {error}", True
            time.sleep(6)  # One more space sample.
            with sampler.lock:
                sampler.phase = None
            left = owned_processes(app, data)
            if left and not failed:
                outcome, failed = outcome + " FAIL: still running after the phase: " + "; ".join(left), True
            request_trace(options.control, "stop", name, 7200)
            record["phases"].append({"name": name, "outcome": outcome, "seconds": round(time.time() - started),
                                     "space": sampler.stats.get(name, {})})
            (run / "run.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
            print("  ", outcome, flush=True)
            if failed:
                break
    finally:
        if options.drive_failure:
            try:
                unfill(data)
            except OSError:
                pass  # The disk is detached; the controller deletes it anyway.
        if options.control:
            (options.control / "quit.request").write_text("", encoding="ascii")
        (run / "run.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
