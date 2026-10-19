"""Opt-in real GPU check of Create's exported defaults. Requires already verified SD 1.5 and XL.

Run after publishing: runtime/python/python.exe -B tests/gpu_picture_defaults_check.py
    --app <published app folder> --data <existing development data folder> --report <report.json>

Never installs or downloads models. Keeps pictures/job records as evidence in the explicit data folder.
"""
import argparse
import ctypes
import hashlib
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time
import uuid


class WorkerJob:
    """Windows kill-on-close ownership, matching Infrastructure/ProcessTreeJob.cs.

    Assign the coordinator before sending requests. Windows also closes this handle
    if the check itself is killed, stopping inference descendants without a finally.
    """

    class Limits(ctypes.Structure):
        _fields_ = [
            ("process_time", ctypes.c_longlong), ("job_time", ctypes.c_longlong),
            ("flags", ctypes.c_ulong), ("min_working", ctypes.c_size_t), ("max_working", ctypes.c_size_t),
            ("active", ctypes.c_ulong), ("affinity", ctypes.c_size_t),
            ("priority", ctypes.c_ulong), ("scheduling", ctypes.c_ulong),
            *[(name, ctypes.c_ulonglong) for name in ("reads", "writes", "other", "read_bytes", "write_bytes", "other_bytes")],
            *[(name, ctypes.c_size_t) for name in ("process_memory", "job_memory", "peak_process", "peak_job")],
        ]

    def __init__(self):
        if os.name != "nt":
            raise RuntimeError("This check requires the published Windows application.")
        self.lock = threading.Lock()
        self.kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        self.kernel.CreateJobObjectW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p]
        self.kernel.CreateJobObjectW.restype = ctypes.c_void_p
        self.kernel.SetInformationJobObject.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p, ctypes.c_ulong]
        self.kernel.SetInformationJobObject.restype = ctypes.c_int
        self.kernel.AssignProcessToJobObject.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        self.kernel.AssignProcessToJobObject.restype = ctypes.c_int
        self.kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        self.kernel.CloseHandle.restype = ctypes.c_int
        self.handle = self.kernel.CreateJobObjectW(None, None)
        if not self.handle:
            raise ctypes.WinError(ctypes.get_last_error())
        limits = self.Limits(flags=0x2000)  # JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if not self.kernel.SetInformationJobObject(self.handle, 9, ctypes.byref(limits), ctypes.sizeof(limits)):
            error = ctypes.get_last_error()
            self.close()
            raise ctypes.WinError(error)

    def assign(self, worker):
        if not self.kernel.AssignProcessToJobObject(self.handle, int(worker._handle)):
            raise ctypes.WinError(ctypes.get_last_error())

    def close(self):
        with self.lock:
            if self.handle:
                self.kernel.CloseHandle(self.handle)
                self.handle = None


def read_events(stream, events):
    try:
        for line in stream:
            events.put(json.loads(line))
    except Exception as error:
        events.put(error)
    finally:
        events.put(None)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--app", type=Path, required=True)
    parser.add_argument("--data", type=Path, required=True)
    parser.add_argument("--report", type=Path, required=True)
    args = parser.parse_args()
    app, data, report = args.app.resolve(), args.data.resolve(), args.report.resolve()
    if not app.is_dir() or not data.is_dir() or not report.parent.is_dir():
        parser.error("App, data and report-parent folders must already exist.")
    defaults = json.loads(subprocess.check_output([str(app / "MediaWorkbench.Cli.exe"), "picture-defaults"], text=True))
    python = app / "runtime/python/python.exe"
    job = WorkerJob()
    worker = None
    deadline = None
    events = queue.Queue()
    expired = threading.Event()
    results = []
    started = time.monotonic()

    def save_report(status):
        temporary = report.with_name(report.name + ".tmp")
        temporary.write_text(json.dumps(dict(status=status, defaults=defaults, results=results), indent=2), encoding="utf-8")
        temporary.replace(report)

    def timeout():
        expired.set()
        print("TIMEOUT: 30-minute check limit reached; stopping the worker and its inference children.", flush=True)
        job.close()

    def receive():
        while True:
            try:
                event = events.get(timeout=30)
            except queue.Empty:
                print(f"  Still waiting for worker progress ({time.monotonic() - started:.0f}s elapsed).", flush=True)
                continue
            if isinstance(event, Exception):
                raise event
            if event is None:
                raise RuntimeError("GPU check timed out." if expired.is_set() else "The worker stopped before returning a result.")
            return event

    def call(operation, payload):
        request = uuid.uuid4().hex
        worker.stdin.write(json.dumps(dict(protocol=1, id=request, operation=operation, payload=payload)) + "\n")
        worker.stdin.flush()
        while True:
            event = receive()
            if event.get("id") != request:
                raise RuntimeError("The worker returned an unexpected request ID.")
            if event["kind"] == "progress":
                progress = event["payload"]
                print(f"  {progress.get('stage', operation)}: {progress.get('completed', 0)}/{progress.get('total', 0)} "
                      f"{progress.get('unit', '')} ({time.monotonic() - started:.0f}s elapsed)", flush=True)
            if event["kind"] == "result":
                return event["payload"]
            if event["kind"] == "error":
                raise RuntimeError(f"{operation}: {event['payload']['message']}")

    try:
        worker = subprocess.Popen([str(python), "-I", "-B", "-u", str(app / "workers/python/media_worker.py"), "--root", str(app)],
                                  stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, encoding="utf-8")
        job.assign(worker)  # No request (and thus no inference child) can start before ownership succeeds.
        threading.Thread(target=read_events, args=(worker.stdout, events), daemon=True).start()
        deadline = threading.Timer(1800, timeout)
        deadline.start()
        save_report("in_progress")
        print("GPU validation may take many minutes on memory-saving profiles. Progress is printed at least every 30 seconds.", flush=True)
        assert receive()["kind"] == "hello"
        models = {row["id"]: row["status"] for row in call("models.list", {"dataRoot": str(data)})["models"]}
        if any(models.get(row["model"]) != "ready" for row in defaults):
            raise RuntimeError("Both picture models must already be verified. This check never installs them.")
        for row in defaults:
            settings = dict(row["settings"], model=row["model"], seed=20261019,
                            prompt="A small red ceramic house beside a green tree, watercolor illustration, daylight")
            print("Checking " + row["model"] + ": " + json.dumps(row["settings"]), flush=True)
            result = call("generate.image", dict(dataRoot=str(data), **settings))
            image = Path(result["imagePath"])
            assert image.is_file() and hashlib.sha256(image.read_bytes()).hexdigest() == result["imageSha256"]
            assert result["width"] == settings["width"] and result["height"] == settings["height"]
            for key, value in row["settings"].items():
                assert result["settings"][key] == value, (key, result["settings"])
            results.append(result)
            save_report("in_progress")  # Keep completed evidence if a later model is interrupted.
            print(f"PASS: {row['model']} saved {result['width']}x{result['height']} in {result['seconds']:.1f}s using {result['runtime']}", flush=True)
        save_report("complete")
    except BaseException as error:
        save_report("interrupted" if isinstance(error, KeyboardInterrupt) else "failed")
        raise
    finally:
        if deadline:
            deadline.cancel()
        try:
            if worker:
                try:
                    worker.stdin.close()  # EOF asks the coordinator to cancel and release its loaded model.
                except BrokenPipeError:
                    pass
                try:
                    worker.wait(timeout=30)
                except subprocess.TimeoutExpired:
                    job.close()
                    worker.kill()  # Also covers assignment failing before ownership was established.
                    worker.wait(timeout=10)
                worker.stdout.close()
        finally:
            job.close()


if __name__ == "__main__":
    main()
