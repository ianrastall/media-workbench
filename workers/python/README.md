# Python workers

The application bundles CPython for lightweight metadata operations; it does not depend on global Python. The discovery worker uses the standard library and public Hugging Face HTTPS API. Future generation packages belong in separate tested inference profiles.

Run `media_worker.py --root <app-or-repository-root>`. It emits a protocol-1 hello, then accepts JSON lines on stdin. Stdout contains protocol events; stderr contains diagnostics.

Operations: `health`, `catalog.list` (local draft recipes), `catalog.search` (public Hub), `catalog.details` (public metadata), and `demo.run` (developer transport check). `cancel` targets the active request ID and does not emit a second terminal response. The demonstration creates text, not generated media. Generation and installation operations return `not_implemented`.

Requests are bounded, search inputs validated, and Hub responses capped. Network access is read-only. Account integration, inference, package installation, and model downloads are not implemented in this alpha. A worker subprocess isolates dependencies and failures; it is not a security sandbox.
