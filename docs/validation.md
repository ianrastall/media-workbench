# Initial alpha validation

Validated on the Windows 11 development machine, 17 October 2026.

| Check | Result |
|---|---|
| Full Debug/Release solution build | Passed, zero warnings/errors |
| Rust unit tests | 4 passed |
| Rust formatting / Clippy with warnings denied | Passed |
| Python behavioral tests | 9 passed |
| C# native/worker/settings checks | 19 passed, including optional existing ffprobe probe |
| Public Hugging Face search/detail smoke check | Passed |
| Published application native/worker self-test | Passed with bundled Python |
| Published GUI load/resource smoke test | Passed |
| Interactive GUI discovery | Opened, navigated, searched, selected a result, loaded its live metadata |

The published-resource check requires the application's PRI and compiled XBF files, native DLL, private Python executable, and worker files. The GUI smoke test loads the actual window and bindings. These checks were added after discovering separate resource-scope and publish-resource omissions during development.

The per-user installer creates a Start menu shortcut and an uninstaller; data/settings live outside its application directory. Installed-copy smoke tests are run as part of final local packaging verification. Uninstall preservation and clean-machine Windows 10 behavior are not yet certified.

No model weights, inference packages, or user account credentials are used in these checks. GPU detection is not an inference compatibility benchmark. Session activity is currently transient rather than a durable job history.

## Process ownership and crash recovery

Validated on the same Windows 11 machine (GTX 1660 Ti 6 GB, driver 610.60), 17 October 2026.

| Check | Result |
|---|---|
| Rust tests/format/Clippy, 25 Python tests, 20 C# checks | Passed |
| Debug solution build | Passed, zero warnings/errors |
| Built app `--self-test` and `--ui-self-test` | Passed |
| C# check without the job object (deliberately disabled) | Failed as expected: an orphaned grandchild survived |
| Re-verification after the profile digest changed | Ready; 4-step 512 × 512 test in 39.1 s, 4841 MiB peak |
| CLI host force-killed during real 30-step generation | Coordinator and ML child ended within 1 s; job recorded interrupted; no partial image; Library reported 1 recovered |
| Normal 10-step generation afterwards | Succeeded in 14.0 s; no worker processes left |

The crash test kills the CLI host process; the GUI uses the same `PythonWorkerClient` ownership path, but a forced GUI kill during generation has not been separately timed.

## Evaluation fixes: data folder, readiness, VRAM, Library paths

Validated on the same Windows 11 machine (GTX 1660 Ti 6 GB), 17 October 2026.

| Check | Result |
|---|---|
| Rust tests/format/Clippy, 37 Python tests, 26 C# checks (6 new view-model checks) | Passed |
| Debug solution build | Passed, zero warnings/errors |
| GPU memory as reported to setup / inference child | nvidia-smi 6144 MiB, CUDA 6143.7 MiB; both pass the recipe's 6144 MiB limit |
| Existing install after the readiness-stamp change | Reported needs verification; Install / verify returned ready, 4-step test 16.9 s |
| 10-step generation afterwards | Succeeded with no model re-hash stage before inference; job stores `<id>/image.png` |
| Library after that generation | Listed the new image and an older absolute-path record |

The generation's 33.3 s inference time on this run is a single sample, not a performance comparison with the earlier 14.0 s run.
