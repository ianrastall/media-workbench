# Development direction after 0.2.0-alpha.8

This is a plan, not a list of supported features. The implemented path is SD 1.5 text/image-to-image, Real-ESRGAN upscaling, durable job evidence, indexed Library browsing and PNG export, storage maintenance, and checksum-verified app updates. Those operations share one saved data folder and preserve model/runtime verification gates.

## Immediate validation

Exercise the current installer and complete image workflow in clean Windows 10 22H2 and Windows 11 environments. Cover install, upgrade, uninstall/data preservation, offline launch after setup, and the app's update/relaunch sequence. Check the Library search, paging, settings reuse, save picker overwrite/cancel behavior, default image viewer, high DPI, narrow windows, and keyboard navigation. Record observed OS/GPU/driver evidence without implying certification of untested configurations. The current installer is unsigned; signing needs an actual certificate/distribution decision.

The hosted build does not provision models or run GPU inference. A green CI run is packaging and behavioral evidence, not a hardware compatibility result.

## Expand the native core

Artifact export and storage inventory are now Rust services, called directly by the desktop/MCP/CLI and through thin Python adapters by worker clients. Continue moving owned-storage pruning, artifact/job validation and indexing, durable queue state, and package/file verification into narrow, documented C ABI services. Keep path containment, atomic commit, cancellation, and file ownership inside Rust with meaningful boundary tests. C# should own presentation and host coordination; Python should focus on Hub integration, runtime provisioning, and the ML libraries that require it. Avoid adding a second implementation of domain policy in each client.

## LLM tool access

The local stdio MCP host is now implemented; see [LLM tools](llm-tools.md). Continue validating real clients and design authenticated remote access so a cloud-only LLM client can inspect the installation, plan/setup the supported model, and submit image/upscale jobs. The LLM client handles its own provider account; the application should not collect or embed provider account tokens. An account alone does not guarantee that a particular client supports local tools.

The implemented stdio host uses the existing C# service and Windows Job Object ownership. Require an explicit data folder, default to inspection tools, and grant setup downloads and GPU generation only through launch-time capabilities the model cannot enable for itself. Keep model IDs/runtime profiles constrained to the verified bundled recipes. Reuse the normal native plan and inference/readiness gates, bounded JSON messages, cancellation, and durable records. Tool outputs must describe actual installed/verified state and explicitly report that video is unsupported.

A cloud-only client needs an authenticated remote transport or a trusted local connector. Do not equate a local stdio server with an Internet-accessible endpoint. A later remote service needs scoped authorization, authenticated sessions, origin checks, TLS, auditing, and durable long-running job handles; it must not expose arbitrary shell execution, provider credentials, or unrestricted file access. See the official [MCP transports](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports) and [tool protocol](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).

## Durable generation queue

Start with the existing job JSON as the authoritative store. Persist a queued job's complete settings and private input copy before acknowledging it. Distinguish queued, running, succeeded, failed, cancelled, and interrupted states. An explicit queue owner must claim work under the data-folder lock; separate clients must never run simultaneous GPU jobs in the same folder. Removing a pending item must not delete an already completed image. A failed job must not discard later queued items.

Pending work should be visible after a restart and require an explicit Resume action; do not start GPU work merely because Library was opened. Cancellation must distinguish cancelling the active job from removing pending work. Closing/crashing the app must still kill the owned process tree, and recovery must mark its active job interrupted without converting pending jobs to failed work.

The current global `IsBusy` and one-request-per-worker transport are deliberate limitations. Introduce separate inference/maintenance and browsing state before allowing enqueue or Library actions during generation. Folder changes, setup, installation removal, and updates must coordinate with the queue owner. Retain a clear UI representation of which saved folder owns pending jobs.

Acceptance checks: atomic enqueue and claim, input copies surviving source removal, restart without automatic inference, multiple-client exclusion, active versus pending cancellation, failure followed by another job, crash recovery, data-folder changes, and unchanged readiness gating. Keep routine tests free of model downloads and hardware requirements; use opt-in real GPU checks for the complete lifecycle.

## Then evaluate a warm inference worker

Measure model-load/import time separately from inference on the existing verified profile before choosing an idle policy. Keep the ML runtime in a child process owned by the same Windows Job Object; the GUI must never load PyTorch or Diffusers. Reuse a pipeline only for the same verified profile, model stamps, GPU selection, precision, and operation-compatible configuration. Sampler and image-to-image changes must not leak state into the next job.

Cancellation or protocol failure may discard the warm child and recreate it for the next queued job. Release it before install/verify, removal, saved-folder changes, updates, or shutdown, and after an explicit idle timeout. Account for retained CPU/GPU memory on 6 GB devices. A crash must still produce an interrupted active job and no partially committed Library image. Changes to inference loading or verification semantics require an adapter-version bump and fresh real inference checks.

## Later expansion

Add Library organization and portable settings/provenance export before destructive artifact pruning. Any deletion needs precise image/job/source relationships so a reused private input is not removed accidentally. Extend model adapters, protected Hub access, masked editing, other hardware, and video only with explicit runtime/license/hardware validation. Discovery results must continue to describe metadata without implying installation support.
