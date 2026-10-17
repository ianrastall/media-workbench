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
