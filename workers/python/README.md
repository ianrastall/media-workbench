# Python workers

The application bundles CPython 3.14 for metadata/setup coordination; it does not depend on global Python. The coordinator uses the standard library. ML dependencies belong to a separately provisioned Python 3.12 inference profile.

Run `media_worker.py --root <app-or-repository-root>`. It emits a protocol-1 hello, then accepts JSON lines on stdin. Stdout contains protocol events; stderr contains diagnostics.

Operations: `health`, `catalog.list` (bundled recipes), `catalog.search`, `catalog.details`, `installation.status`, `installation.run`, `generate.image`, `library.list`, and `demo.run` (developer text-artifact transport check). `cancel` targets the active request ID and does not emit a second terminal response.

`inference_setup.py` owns persistent setup/job state, locks, checksums, runtime installation, and child supervision. Under the data-folder lock it records work left by a dead owner as interrupted; `library.list` returns that count as `recovered`. The desktop client places each worker in a kill-on-close Job Object, so children started here belong to it; do not start them with a breakaway flag. `inference_worker.py` runs only in the private ML interpreter, uses the Hub SDK to fetch a pinned selection, and loads the built-in local safetensors pipeline for inference. See [inference lifecycle](../../docs/inference.md).

Requests are bounded, inputs validated, and metadata responses capped. Public model downloads explicitly disable account tokens. A worker subprocess isolates dependencies and failures; it is not a security sandbox.
