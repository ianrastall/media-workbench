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

## 0.2.0-alpha.2 pre-release checks

Validated on the same Windows 11 machine (GTX 1660 Ti 6 GB), 17 October 2026.

| Check | Result |
|---|---|
| Rust tests/format/Clippy, 50 Python tests, 28 C# checks | Passed |
| Debug solution build; built app `--ui-self-test` | Passed, zero warnings/errors |
| Install / verify through the coordinator's native plan gate | Ready in 32 s (4-step test 15.8 s); runtime marker recorded |
| 10-step generation | Succeeded, 14.1 s inference, 22 s end to end; log written as `logs/generate-<id>.log` |
| Library page with a throwaway data folder (one image, one failed and one interrupted job) | Grid and Unfinished generations rendered; Open log enabled only for the job with a log |
| Second launch with the first window minimized | Exited and restored the first instance's window |

Focus changes could not be observed from a background shell, so bringing the window to the foreground on a second launch was not separately confirmed.

## Generation controls and Storage

Validated on the same Windows 11 machine (GTX 1660 Ti 6 GB), 17 October 2026.

| Check | Result |
|---|---|
| Rust tests/format/Clippy, 56 Python tests, 33 C# checks; Release publish self-test | Passed |
| 768 × 512, DPM++ 2M Karras, 20 steps, guidance 7 | Succeeded, coherent image, peak 4880 MiB allocated |
| 512 × 768, Euler ancestral, 20 steps | Succeeded, coherent image, peak 4879 MiB |
| 512 × 512, PNDM, 20 steps, guidance 4 | Succeeded, coherent image, peak 4841 MiB |
| 10 steps, nothing else running: 512 × 512 vs 768 × 512 | 28.9 s vs 111.5 s inference (about 3.9×) |
| Storage summary of the real data folder (read-only) | Runtime 4.14 GiB, model 2.55 GiB, reclaimable download caches 4.13 GiB |
| Settings, Library, and Create pages with a throwaway data folder | Rendered as intended |

The 512 × 512 time was about twice this morning's 14.1 s on the same settings, so absolute timings vary with the machine's state; the ratio between sizes is the useful figure. Clearing caches and removing the installation were exercised only on temporary folders, not on the real data folder.

## Image from an image and updates

Validated on the same Windows 11 machine (GTX 1660 Ti 6 GB), 17 October 2026.

| Check | Result |
|---|---|
| Rust tests/format/Clippy, 58 Python tests, 49 C# checks | Passed |
| Image-to-image from a 768 × 512 painting, moderate change, 20 steps | Succeeded: landscape shape kept, composition preserved, peak 5207 MiB; 274 s while builds ran alongside |
| Library → Use as starting picture → Create, driven through UI Automation | Create opened with the picture, change level, and no shape picker |
| `update-check` as 0.2.0-alpha.2, and pretending to be 0.2.0-alpha.1 with `--download` | Up to date; then found 0.2.0-alpha.2 and downloaded the 75 MB installer through GitHub's redirects with a matching SHA-256 |
| Home page of the published build | Showed the installed version and a live "up to date" check |

| Install / verify after the upscaler was added (existing install) | Reported needs verification; fetched only the 64 MiB upscaler; both GPU tests passed (643 s while a build ran alongside) |
| 4× upscale of a 768 × 512 image | 3072 × 2048 in 4.1 s, peak 754 MiB; tiles show no visible seams; sharper than a Lanczos resize |

Running the downloaded installer silently and reopening the app was not exercised, because it would install over this machine's environment; it needs a clean-machine or sandbox run.
