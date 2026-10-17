# LLM tool access (MCP)

Media Workbench 0.2.0-alpha.6 includes a local Model Context Protocol stdio host. A compatible desktop LLM client launches it and can inspect the workspace, prepare the supported model when permitted, and request verified local image generation/upscaling. The client handles its own provider account. Media Workbench does not collect an LLM password or API key; an account alone does not guarantee that its client supports local MCP servers.

This implementation does not run an Internet listener. Cloud-only clients need a trusted local connector or a future authenticated remote transport. An unauthenticated HTTP proxy is not a supported configuration. Video is not implemented.

## Connect a client

1. Install the complete Windows x64 release and choose/save a data folder in Settings.
2. Expand **Connect an LLM client (MCP)** in Settings.
3. Leave both optional permissions off for inspection. Include setup permission to download/verify the bundled SD 1.5/upscaler profile; include inference permission to generate/upscale using a verified installation. Setup downloads several GiB and runs real GPU checks.
4. Select **Copy MCP configuration** and add it to the client's local MCP server settings. Configuration formats vary between clients; use the generated command/arguments in the client's equivalent fields.
5. Restart that client's MCP server after changing its configuration. The checkboxes change the configuration being copied; they do not launch a server or alter a running server's permissions.

The generated configuration uses the actual installed host and saved folder. Its structure is:

```json
{
  "mcpServers": {
    "media-workbench": {
      "command": "<absolute install folder>\\MediaWorkbench.Cli.exe",
      "args": ["mcp", "--data-root", "D:\\MediaWorkbenchData"]
    }
  }
}
```

Replace the placeholder with the actual path; copying from Settings does this for you. The installer bundles the console executable, shared self-contained .NET runtime, Rust DLL, and discovery Python runtime. No system Python/.NET is needed. Use the complete released installer.

Add `--allow-setup` and/or `--allow-inference` to `args` for the permissions you grant. They are independent: generation permission does not authorize downloading/installing a model, and setup permission does not authorize creative generation. The model cannot enable its own flags or change the selected folder. A newly copied configuration follows the app's saved folder, not an unsaved Settings draft; a running server stays scoped to its launch folder.

## Tools and scope

| Tool | Availability | Behavior |
|---|---|---|
| `workbench_capabilities` | Default | Selected folder, launch permissions, supported operations, video unsupported |
| `workbench_health` | Default | Native/worker/GPU diagnostics; detection is not inference readiness |
| `workbench_installation_plan` | Default | Rust plan for the bundled model; no download |
| `workbench_model_status` | Default | Actual ready/not-installed/needs-verification state; normal idle crash recovery |
| `workbench_storage` | Default | Storage inventory, with no deletion |
| `workbench_library` | Default | Literal prompt/seed/job-ID search, image-type filter, offset and 1–48-image pages, unfinished-job diagnostics |
| `workbench_search_models` | Default, online | Public Hub metadata; results do not imply installation support |
| `workbench_model_details` | Default, online | Public repository/revision/license/files |
| `workbench_install_model` | `--allow-setup` | Bundled SD 1.5/Real-ESRGAN only, through native planning and file/runtime/GPU verification |
| `workbench_generate_image` | `--allow-inference` | Prompt/negative prompt/seed/steps/size/guidance/sampler; optional `sourceJobId` and strength for image-to-image |
| `workbench_upscale_image` | `--allow-inference` | Completed `sourceJobId`, scale 2 or 4; saves a new Library result |

Image inputs come from completed jobs in the selected folder, with normal containment checks. Arbitrary source paths, arbitrary model/runtime installation, shell commands, deletion, credentials, and app updates are not exposed as tools. Export remains available in the GUI. Results contain metadata/settings and local image paths, not image bytes. A client may use its own separately authorized file tools to display a result.

Start by reading capabilities, the installation plan/model license, and model status. A model is ready only after ordinary file/runtime/inference checks succeed. Setup and generation retain data-folder locking, job records, owned-process cancellation, and crash recovery. GUI operations can contend for the same folder; the active operation owns it until completion or cancellation.

## Privacy and execution

The client may pass tool results to its provider, including prompts, negative prompts, saved image settings, local paths, and GPU/storage diagnostics. Choose the folder and client permissions accordingly. Provider authentication stays in the client and is not stored by Media Workbench.

One tool operation runs at a time; ping/list tools remain available. Setup/inference can take minutes, so configure the client's timeout accordingly. MCP progress tokens receive monotonically increasing notifications with the current stage. The server limits operations to four hours; clients may impose shorter timeouts and send `notifications/cancelled` for the active request ID. Cancellation reaches the worker supervisor, which escalates to terminating its owned process tree. Check model status and Library after interrupted work.

Closing stdin cancels and joins active work. Killing the host closes its Windows Job Object handles, ending worker descendants. Bounded UTF-8 JSON lines (1 MiB input maximum) negotiate `2025-11-25` or `2025-06-18`. Supported methods are initialize, initialized notification, ping, tools/list, tools/call, cancellation, and optional progress. No provider SDK or AI runtime enters the host.

See the official [stdio transport](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports), [lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle), and [tools](https://modelcontextprotocol.io/specification/2025-11-25/server/tools). Authenticated remote transport, durable asynchronous job handles, client/hardware coverage, and video require further work.
