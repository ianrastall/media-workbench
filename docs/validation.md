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

## Searchable Library (0.2.0-alpha.4)

Validated on the Windows 11 development machine, 17 October 2026. This change does not alter inference or the readiness stamp.

| Check | Result |
|---|---|
| `scripts/check.ps1 -Online`: Rust tests/format/Clippy, Python, C#, live Hub search/details | Passed: 5 Rust tests, 72 Python tests, 63 C# checks |
| `scripts/build.ps1 -Configuration Release -Publish` | Passed, zero warnings/errors; bundled Python/SQLite self-test passed |
| `scripts/test-publish.ps1` | Native/worker/SQLite and real window/resource/binding smoke checks passed |
| Library pagination fixture | All 253 images reachable with 48-image pages; equal modification times did not duplicate or skip records |
| Real C# → Python → SQLite search | Unicode search, image-type filter, typed settings and page metadata passed |
| Index recovery and incremental refresh | Missing/corrupt/future-version index rebuilt; moved folders, removed/restored images, changed records, cancellation rollback passed |
| View-model navigation during inference | Completing generation while Library is open refreshes its applied filters from page one |

The new controls have behavioral and window-load coverage; their full interactive workflow and clean-machine installer behavior remain to be checked. No additional GPU/driver coverage is claimed.

## Native artifact export (0.2.0-alpha.5)

Validated on the same Windows 11 machine, 17 October 2026. Rust owns the export transaction; the GUI calls the C ABI directly and worker clients use a ctypes adapter.

| Check | Result |
|---|---|
| `scripts/check.ps1` | Passed: 7 Rust tests plus formatting/Clippy, 84 Python tests, 69 C# checks |
| Release publish and published core/UI self-tests | Passed, zero build warnings/errors |
| Copy integrity, original/job preservation, existing destination replacement | Passed through the real Rust library |
| Hash mismatch, cancellation, failed replacement, callback exception | Destination preserved; temporary copy cleaned up |
| Direct native callers bypassing Python preflight | Managed destinations (including alternate Windows path casing), nonterminal jobs, ID mismatch, and traversal rejected |
| Null/short output buffers | Required size returned without creating any output |
| C# direct native export and managed-folder refusal | Passed |

The native save dialog's complete interactive overwrite/cancel workflow and clean-machine update installation still need validation. Automated tests cover the underlying native transaction and actual GUI resource initialization.

## Local LLM tool access (0.2.0-alpha.6)

Validated on the same Windows 11 machine, 17 October 2026.

| Check | Result |
|---|---|
| `scripts/check.ps1 -Artifacts <temporary-check-directory>` | Passed: 7 Rust tests plus format/Clippy, 85 Python tests, 98 C# checks |
| Release publish and `scripts/test-publish.ps1` | Passed, zero build warnings/errors; native/worker, GUI load, and shipped MCP host/worker tests passed |
| Real MCP stdio sessions with inspection-only and setup/inference capabilities | Initialized, advertised the granted tools, rejected guessed disabled tools and folder overrides, queried a real Library, rejected unverified inference |
| Controlled active MCP operations | Overlap refused, cancellation reached the service, client EOF cancelled/joined work, generated settings/large seeds passed unchanged |
| Invalid/oversized protocol input | Typed validation, parse errors with session reuse, bounded oversized-message closure passed |
| Settings MCP configuration | Uses the saved folder and defaults to inspection; optional permissions included only when chosen |
| Empty data-folder lock | Competing owners excluded without writing/flushing a sentinel byte |

Local first-time filesystem/cache writes on the data drive intermittently exceeded the checks' timeouts. The full suite passed with fixture data on system temporary storage; Python tests already use temporary folders. Published self-tests now keep their disposable fixture data in the system temporary folder as well. No SQLite durability setting was relaxed. These observations do not establish the cause of the drive stalls.

The protocol and service boundaries are tested; a complete provider-account/client workflow and successful GPU inference through an external MCP client remain to be exercised. Hosted authenticated remote access, video, clean-machine installation/update behavior, and additional GPU coverage are not claimed.

## Release documentation (0.2.0-alpha.7)

Validated on Windows 11, 17 October 2026. The full checks (7 Rust, 85 Python, 98 C#) and self-contained publish passed from a temporary source checkout. GitHub Actions then built, checked, and published the installer, checksum, and generated README. The downloaded README was compared with the published release body; installation, MCP, licensing, checksum instructions, and tag-pinned documentation links matched. The public repository description and 17 topics were read back from GitHub.

## Native storage inventory (0.2.0-alpha.8)

Validated on the same Windows 11 host, 17 October 2026: 9 Rust tests plus formatting/Clippy, 91 Python tests, 106 C# checks, a Release published build with no warnings/errors, and published core/GUI/MCP smoke tests passed. Builds/checks ran from a temporary system-drive source checkout after project-drive writes stalled. Automated checks use disposable temporary data, without model downloads or GPU inference. The C# service measured a fixture with a nonexistent Python executable, proving the direct native path. Python/ctypes and real worker/MCP clients returned the same per-area inventory. Tests covered Unicode paths, supported-profile selection, file counts, cancellation before/during traversal, buffer queries without folder creation, skipped internal Windows junctions, refused managed-area junctions, and unchanged preservation of images/jobs during confirmed removal. Native inventory and export share the reparse-point checks.

The self-contained published app and MCP host also queried native storage using the bundled layout. Measurements are logical file sizes, not allocated disk usage; no additional GPU/driver compatibility or clean-machine claim follows from these checks.

The normal Debug GUI output also passed core, GUI initialization, and real MCP/worker smoke checks after the build copied a console host compiled for the same bundled .NET runtime. A framework-dependent host placed beside the GUI's local runtime could not resolve the system framework; building it self-contained fixes that source-build launch path. The full 9 Rust / 91 Python / 106 C# checks passed with this build change.

## Native pinned-file verification (0.2.0-alpha.9)

Validated on Windows 11, 17 October 2026: 11 Rust tests plus formatting/Clippy, 100 Python tests, and 111 C# integration checks passed. A self-contained Release publish completed with no warnings/errors; published core, GUI initialization, and MCP/worker smoke checks passed. These checks used disposable fixtures on system temporary storage, with no model downloads or GPU inference.

Real ABI checks covered Unicode paths, correct bytes, size mismatch and same-size checksum corruption, missing files/core, unsafe declarations, short-buffer queries without filesystem work/callbacks, Windows junction refusal, blocked write/delete access while hashing, cancellation within a large file, retained downloads, and exception containment for Python and C# callbacks. Setup fixtures used Rust rather than Python model hashing and retained the existing size/mtime stamp shape. The shipped app verified a bundled file through the new native export. The inference profile and GPU verification requirements were unchanged; these checks do not add hardware compatibility evidence.

## Painted partial changes (0.2.0-alpha.10)

Validated on the same Windows 11 host (GTX 1660 Ti 6 GB, driver 610.60), 17 October 2026. Checks and Release builds ran from a temporary system-drive copy because the project drive was saturated by unrelated activity.

| Check | Result |
|---|---|
| `scripts/check.ps1` | Passed: 11 Rust tests plus formatting/Clippy, 103 Python tests, 129 C# checks |
| Release publish and `scripts/test-publish.ps1` | Passed, zero warnings/errors; published core, GUI initialization, and MCP/worker checks passed |
| Real painted change through the coordinator: 512 × 512 watercolor, ellipse painted over water, "a yellow rubber duck", strength 1, 20 PNDM steps | Succeeded on the existing verified installation (no re-verification): a coherent duck blended into the painted area; peak 5166 MiB allocated; pixels more than 15 pixels outside the painted area were byte-identical to the original |
| Same on a 768 × 512 picture (768 × 512 region) while builds ran alongside | Ran at about 48 s per step and was stopped by the test harness timeout before finishing; the next check confirms the interrupted job was recovered rather than listed |
| Published GUI driven through UI Automation | Library → Use as starting picture → Paint the part to change showed the painting editor, change-level choice, brush controls, and the translucent painted overlay; clicks painted at the pointer position and a dragged stroke painted continuously |

Synthetic drags were unreliable on this multi-monitor desktop (the cursor sometimes did not move between button down and up), so stroke continuity was confirmed in one run plus event diagnostics showing moves extend the stroke while the button is held. Touch input currently scrolls the page rather than painting; mouse and pen are the supported painting inputs. The 768 × 512 timing reflects contention, not a benchmark.

## Models you added (0.2.0-alpha.11)

Validated on the same Windows 11 host (GTX 1660 Ti 6 GB), 17 October 2026, against the existing verified data folder; the bundled installation stayed ready without re-verification. Checks and Release builds ran from a temporary system-drive copy; the data folder is on the slower project drive.

| Check | Result |
|---|---|
| `scripts/check.ps1` | Passed: 14 Rust tests plus formatting/Clippy, 121 Python tests, 147 C# checks |
| Release publish and `scripts/test-publish.ps1` (with UI self-test) | Passed, zero warnings/errors; published self-test lists models, storage reports 8 areas, MCP lists models |
| `tests/gpu_model_import_check.py` with the installed SD 1.5 Diffusers folder (fp16 parts, 1.99 GiB) | Identified, copied, natively verified, and passed its GPU test; then text to image (512 × 512, 20 steps, 59.6 s, 4841 MiB peak), image to image, a painted change, and a 2× upscale succeeded; job records named the model; removal kept the four images |
| Same with a single-file checkpoint reassembled locally from those installed weights (original key layout, own VAE, 1.99 GiB) | Same sequence passed through `from_single_file` with bundled configuration, offline; text to image 13.1 s. Its first image matched the Diffusers-folder result for the same seed |

The single-file fixture was produced on this machine by renaming the pinned SD 1.5 tensors to the original checkpoint layout; it is not distributed. Both checks were run before a third-party fine-tune was available, so they show that the loading paths, gates, and records work, not that every community checkpoint will. The first real run exposed an over-strict type check (CLIP's integer `position_ids` buffer was treated as a quantized weight); it was fixed and the fixtures now include that buffer. Timings vary with drive contention and are single samples. In the painted-change check the model drew a tree where a moon was asked for; the unpainted area was unchanged.

## Clean-up and drive space (0.2.0-alpha.12)

Validated on the same Windows 11 host, 17 October 2026. Checks and Release builds ran from a temporary system-drive copy of exactly the committed files; the development data folder is on the slower project drive.

| Check | Result |
|---|---|
| `scripts/check.ps1` | Passed: 23 Rust tests plus formatting/Clippy, 132 Python tests, 170 C# checks |
| Release publish and `scripts/test-publish.ps1` (with UI self-test) | Passed, zero warnings/errors; the published self-test reads drive information and runs clean-up on a missing folder without creating it |
| Drive detection through `mw_drive_info` | The system drive reported `ssd` and the project drive `hdd`, matching the drives |
| Clean-up dry run on the real, verified development data folder (installation not marked verified, caches kept) | Removed nothing (no logs past 30 days or 300, no unfinished jobs beyond 50, no stale downloads) in 0.01 s |
| Cache removal on a copy of that folder's download caches on the system drive (`cache/` and `bootstrap/`, 19,621 files, 4.13 GiB) | Removed in 4.35 s with nothing skipped |

The automated checks use disposable temporary folders and cover each rule from Rust, Python, and C#: what is removed and what must be kept (listed unfinished jobs with their Retry pictures and logs, finished images and records, damaged records, an unfinished job's log, fresh downloads, added models, the user's own files), crash leftovers (`.removing/`, a half-written image, stale partial downloads, painted hand-overs), read-only cache files, links that must not be followed, a data folder held by another process (Python and C# lock holders), cancellation, and generation and upscaling refusing before any write when space is short. The real data folder's caches were not removed, so the dry run shows the rules leave a working folder alone, and the copy shows the removal speed; neither is a measurement of a full setup followed by clean-up on a clean machine. Free-space refusal was exercised with a simulated small drive, not by filling a real one.

## Hardware-aware inference and stack alignment (0.2.0-alpha.13)

Validated on Windows 11, 18 October 2026, using .NET SDK 10.0.113 and Rust 1.97.0. The machine has an NVIDIA GeForce GTX 1660 Ti with 6 GB graphics memory, driver 610.60, and about 64 GB system memory. The existing data folder is on a hard disk. No clean-machine or additional GPU claim follows from these checks.

| Check | Result |
|---|---|
| `scripts/check.ps1 -Artifacts <temporary-check-directory>` with `ContinuousIntegrationBuild=true` | Passed: 23 Rust tests, formatting/Clippy, 157 Python tests and 179 C# checks, without build warnings |
| `scripts/build.ps1 -Configuration Release -Publish` with `ContinuousIntegrationBuild=true` | Passed, zero warnings/errors; solution, self-contained GUI and console host built against locked NuGet graphs |
| `scripts/test-publish.ps1` | Passed shipped core/worker, actual GUI initialization, MCP/worker smoke checks, required hardware module, and x64 PE checks for GUI, console host, Rust DLL, .NET runtime and Python |
| Locked restore with unchanged dependencies, including self-contained x64 CLI | Passed |
| Temporarily change Toolkit 8.4.2 to 8.4.1, restore with `--locked-mode`, then restore the original central file | Rejected with NU1004; existing locks unchanged |
| MSBuild native architecture checks | Explicit x64 accepted; ARM64 platform, win-arm64/linux-x64 RIDs and x86 PlatformTarget rejected |
| `tests/gpu_hardware_profile_check.py <existing-data-folder> --expect fp32-offload` | Passed: profile selected the conservative runtime, native file checks and actual GPU verification passed, status remained ready, generation and 4x upscale succeeded, profile reported the tested runtime |
| Real verification evidence | Four-step 512 × 512 GPU test including upscaler: 40.05 s, float32/offload, peak 4841 MiB |
| Real generation evidence | 512 × 512, 20 steps, 139.86 s inference, peak 4841 MiB; recorded runtime was `fp32-offload` |
| Real upscaling evidence | 2048 × 2048 output, 384-pixel tiles selected from free graphics memory, 3.25 s, peak 1474 MiB |
| Published startup maintenance on the verified development folder | Removed 19,621 reproducible setup-cache files, 4.13 GiB, and displayed the amount removed; model installation remained usable |
| Interactive published Diagnostics | GPU/RAM/drive, selected settings, reasons, and hard-disk advice rendered readably. UI Automation exposed only the fixed name "This computer" for the details; that override was removed, and the updated XAML was rebuilt and passed the GUI smoke check. A complete screen-reader audit remains pending |

Hardware tests use fake GPUs to exercise selection, fallback, driver-specific declined settings, readiness stamps and job routing. Real ctypes checks cover native drive information for Unicode/missing folders, invalid paths and missing/incompatible libraries. Readiness regression checks prove that Diagnostics withholds the verified runtime for missing/changed files and an incompatible card, without modifying the saved records. These tests do not certify faster settings on RTX or Pascal GPUs. The GPU test reused the existing models/runtime and retained caches; the subsequent normal app startup performed the default verified-setup cache clean-up.

Timings are single observations while development builds ran, and exclude model import/load time. They are not startup or latency benchmarks. Clean Windows 10/11 installer/upgrade/offline/update/uninstall checks, full accessibility and mixed-DPI evidence, external startup/idle measurements, useful release-symbol archival and mixed debugging remain open in [stack alignment](stack-alignment.md).

## Discover pinned downloads (0.2.0-alpha.14)

Validated on the same Windows 11 host, 18 October 2026, with the existing verified SD 1.5 runtime on a GTX 1660 Ti (6 GB, driver 610.60). The data folder is on a hard disk. The unchanged adapter/profile stamps kept the baseline ready; no baseline reinstallation or runtime provisioning was needed.

| Check | Result |
|---|---|
| Full `scripts/check.ps1` with `ContinuousIntegrationBuild=true` | Passed: 25 Rust tests plus formatting/Clippy, 175 Python tests, 193 C# checks |
| Release solution build | Passed, zero warnings/errors |
| `scripts/build.ps1 -Configuration Release -Publish -ManagedArtifacts <isolated-output>` | Passed against locked dependencies; shipped native/worker/MCP smoke tests and x64 inventory passed. The app self-test exercises the native Hub planner |
| `tests/online_catalog_check.py` | Passed actual public search/details and pinned metadata/header identification without downloading full weights or requiring a GPU |
| Complete opt-in `tests/gpu_model_import_check.py <verified-folder> hf:stable-diffusion-v1-5/stable-diffusion-v1-5 --license-reviewed` | Passed pinned download, native file/notice verification, real GPU test, model listing, text/image-to-image, painted edit, 2x upscale, pinned generation provenance and model removal |
| Downloaded source | Commit `451f4fe16113bff5a5d2269ed5ad43b0592e9a14`, `v1-5-pruned-emaonly.safetensors`, 4,265,146,304 bytes, SHA-256 `6ce0161689b3853acaa03779ec93eafe75a02f4ced659bee03f50797806fa2fa`; license metadata `creativeml-openrail-m`; the pinned README was retained and natively verified |
| Text to image | 512 × 512, 20 steps, `fp32-offload`, 159.16 s inference, peak 4841 MiB; the job retained the repository/commit/file/hash/license source |
| Image to image / painted change | Succeeded in 109.45 s / 182.76 s respectively |
| Upscale / removal | Produced 1024 × 1024; removing the added test model kept all four images and their job records |

Routine fixtures cover unsupported headers, bounded tensor counts/ranks/arithmetic and payload ranges, unsafe IDs/paths, gated or unlicensed sources, immutable snapshot/hash/size mismatches, headers without full weights, missing GPU/runtime/space, low space during download, partial-read progress/cancellation, corrupted weights/notices, duplicate refusal before full transfer, failure/crash clean-up, native ABI sizing/round trips, saved-folder scoping, license review and stale/failed selection checks. A filename containing ordinary words such as `dream-of-spring` remains eligible; numbered shard names are excluded. No download or GPU work runs in the routine suite.

The real check used an official baseline single-file checkpoint, not a community fine-tune; it adds no RTX/Pascal or newer-architecture certification. Timings are single observations under development load, excluding import/model loading. A preliminary download was deliberately stopped while improving streaming progress, and ordinary orphan-import recovery removed its unfinished copy before retrying.

Interactive computer use was stopped by the user with Escape before the Discover walkthrough. No further desktop input was made; interactive layout, keyboard/screen-reader and narrow/high-DPI validation of this flow remain pending. Subsequent publishing used isolated managed outputs and `-SkipUI` smoke checks, leaving the open development app alone. Clean-machine Windows install/upgrade/update validation remains pending.
