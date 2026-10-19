"""Exercise GPU-check process ownership with sleeping Python processes, without AI runtimes."""
import ctypes
import json
import os
from pathlib import Path
import queue
import subprocess
import sys
import threading
import unittest


@unittest.skipUnless(os.name == "nt", "Windows worker ownership")
class WorkerOwnershipTests(unittest.TestCase):
    def check_tree(self, crash):
        # The worker cannot start its child until the driver assigns it to the job.
        worker_code = (
            "import subprocess,sys,time; sys.stdin.readline(); "
            "child=subprocess.Popen([sys.executable,'-c','import time; time.sleep(120)']); "
            "print(child.pid,flush=True); time.sleep(120)"
        )
        driver_code = f"""
import json, subprocess, sys, time
sys.path.insert(0, {str(Path(__file__).resolve().parent)!r})
from gpu_picture_defaults_check import WorkerJob
job = WorkerJob()
worker = subprocess.Popen([sys.executable, '-c', {worker_code!r}], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
job.assign(worker)
worker.stdin.write('start\\n')
worker.stdin.flush()
print(json.dumps([worker.pid, int(worker.stdout.readline())]), flush=True)
sys.stdin.readline()
job.close()
worker.wait(timeout=10)
"""
        driver = subprocess.Popen([sys.executable, "-B", "-c", driver_code], stdin=subprocess.PIPE,
                                  stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)
        self.addCleanup(driver.stderr.close)
        self.addCleanup(driver.stdout.close)
        self.addCleanup(driver.stdin.close)
        self.addCleanup(lambda: driver.wait(timeout=10))
        self.addCleanup(lambda: driver.kill() if driver.poll() is None else None)
        lines = queue.Queue()
        threading.Thread(target=lambda: lines.put(driver.stdout.readline()), daemon=True).start()
        pids = json.loads(lines.get(timeout=15))
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.OpenProcess.argtypes = [ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
        kernel.OpenProcess.restype = ctypes.c_void_p
        kernel.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        kernel.WaitForSingleObject.restype = ctypes.c_ulong
        kernel.TerminateProcess.argtypes = [ctypes.c_void_p, ctypes.c_uint]
        kernel.CloseHandle.argtypes = [ctypes.c_void_p]
        handles = []
        try:
            for pid in pids:
                handle = kernel.OpenProcess(0x100001, False, pid)  # SYNCHRONIZE | PROCESS_TERMINATE
                self.assertTrue(handle, ctypes.get_last_error())
                handles.append(handle)
            if crash:
                driver.kill()  # No Python finally: the OS must close its job handle.
            else:
                driver.stdin.write("stop\n")
                driver.stdin.flush()
            driver.wait(timeout=10)
            for handle in handles:
                self.assertEqual(kernel.WaitForSingleObject(handle, 10000), 0,
                                 "The worker or inference descendant survived its owning check")
        finally:
            for handle in handles:
                kernel.TerminateProcess(handle, 1)  # Keep even a failed regression check from leaving a sleeper.
                kernel.CloseHandle(handle)

    def test_close_stops_worker_and_descendant(self):
        self.check_tree(crash=False)

    def test_killed_check_stops_worker_and_descendant(self):
        self.check_tree(crash=True)


if __name__ == "__main__":
    unittest.main()
