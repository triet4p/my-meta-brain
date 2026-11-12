# Meta Brain

> A local-first, owner-controlled encrypted second brain and scoped memory sharing service for AI agents.

Meta Brain serves as an owner-controlled second brain designed to preserve evidence, recall context, connect insights, and selectively synthesize knowledge over time. Built local-first for daily engineering and research workflows, Meta Brain interfaces with normal AI coding assistants (such as Codex CLI and Oh My Pi) via the Model Context Protocol (MCP) while maintaining strict cryptographic boundaries.

## Website

Visit the [Meta Brain website](https://triet4p.github.io/my-meta-brain/) for downloads and
practical usage guides:

- [CLI and MCP scenarios](https://triet4p.github.io/my-meta-brain/scenarios.html)
- [Connect Codex CLI](https://triet4p.github.io/my-meta-brain/codex.html)
- [Connect Oh My Pi](https://triet4p.github.io/my-meta-brain/omp.html)
- [Installation and operations runbook](https://triet4p.github.io/my-meta-brain/operations.html)

The static landing-page source is under [`website/`](website/).

Build its reviewed local candidate preview with:

```powershell
powershell.exe -NoProfile -File scripts/site/build.ps1
python -m http.server --bind 127.0.0.1 --directory dist/site 8000
```

Open `http://127.0.0.1:8000/`. The ignored `dist/site/` output is a local preview with verified,
byte-identical candidate copies under the short public filenames. It does not publish or deploy
anything; the public Release and Pages site have separate owner-controlled publication flows.
---

## Overview

Meta Brain operates under an **owner-controlled personal vault** security model (CR01 / D016). The owner retains exclusive custody of the vault encryption key; client AI agents discover only owner-published catalog projections and must explicitly request memory access. Approved requests are minted into single-use, opaque tokens that agents redeem for time- and scope-bounded in-memory sessions over local named pipes.

### Core Philosophy

- **Assistant, Not Autonomous Leader:** Meta Brain assists the owner; it never unilaterally adopts agendas, changes user beliefs, or closes unresolved inquiries.
- **Epistemic Integrity:** Separates original author, operator/executor, inquiry vs. belief, hypothesis vs. decision, and evidence strength. AI outputs are never relabeled as user conviction.
- **Local-First & Encrypted at Rest:** All canonical notes, items, indices, caches, and snapshots remain encrypted at rest. The service starts locked and wipes held keys from memory upon locking.
- **Zero Raw Bearers in Model Context:** Keys and raw tokens never enter prompt contexts, command-line arguments, or agent logs. Local DPAPI-protected handoff files mediate token redemption.
- **In-Memory Scoped Delivery:** Scoped retrieval streams directly in memory via stdio MCP tool calls without creating unconsented plaintext cache files on disk.

---

## System Architecture

The release bundle carries one shared runtime and two supported product entrypoints (.NET 10 on Windows):

```
┌────────────────────────────────────────────────────────┐
│  AI Clients (MCP)                  Owner CLI           │
│  Codex CLI / OMP             cli\MetaBrain.Cli.exe     │
│  mcp\MetaBrain.Mcp.exe                                 │
└───────────┬───────────────────────────┬────────────────┘
            │ stdio JSON-RPC 2.0        │ owner channel
┌───────────▼───────────────────────────▼────────────────┐
│          service\MetaBrain.Connections.exe             │
│  - Shared locked host (Windows Named-Pipe Server)      │
│  - Stdio MCP agent bridge                              │
└──────────────────────────┬─────────────────────────────┘
                           │ In-Process Dispatch
┌──────────────────────────▼─────────────────────────────┐
│              MetaBrain.Application                     │
│  - Service Request Handling & Channel Dispatch         │
│  - Policy Verification & Access Context Enforcement    │
└──────────────────────────┬─────────────────────────────┘
                           │ Domain Invariants
┌──────────────────────────▼─────────────────────────────┐
│                 MetaBrain.Core                         │
│  - Encrypted Vault Storage (AES-256-GCM / PBKDF2)      │
│  - Owner Catalog Authority & Access Requests           │
│  - Scope Grants & In-Memory Agent Session Authority    │
└────────────────────────────────────────────────────────┘
```

The two supported products share one instance, one locked host, one vault and one policy implementation.
Each runs when the other is closed; opening one never restarts or unlocks the instance in use, and
closing one never stops the host the other is using. The WPF Owner UI is the frozen 1.1.0 legacy
product and is neither packaged nor upgraded by this release.

### Components

1. **`MetaBrain.Core` (`net10.0`)**: The domain and security core. Implements authenticated ciphertext storage, owner-managed access requests, scope snapshots, and in-memory session lifecycles. Independent of UI and transport layers.
2. **`MetaBrain.Application` (`net10.0`)**: The mediation and application-service layer. Dispatches typed operations between channels while strictly enforcing owner vs. agent policy boundaries.
3. **`MetaBrain.Connections` (`net10.0-windows`)**: The shared runtime: Windows named-pipe host and service control, the shared instance lifecycle every frontend uses, the owner management CLI, and the vault-blind stdio MCP agent bridge.
4. **`MetaBrain.Cli` (`net10.0-windows`)**: The packaged owner CLI entrypoint (`cli/MetaBrain.Cli.exe`): `--help`/`--version`, the shared `lifecycle` verbs and every `owner` workflow, with no second implementation.
5. **`MetaBrain.OwnerUi` (`net10.0-windows`)** — **frozen 1.1.0 legacy source**: the WPF owner console (`owner-ui/MetaBrain.OwnerUi.exe`) as published in 1.1.0. It is retained for history and transition proof only; the current release packages no UI component and makes no compatibility claim for the frozen console against the current runtime. `MetaBrain.OwnerUi.Setup` is the matching frozen 1.1.0 graphical setup source.
6. **`MetaBrain.Mcp` (`net10.0-windows`)**: The packaged MCP server entrypoint (`mcp/MetaBrain.Mcp.exe`) that starts/connects the shared locked host and serves the existing agent-scoped tools over stdio.
7. **`MetaBrain.Installer` (`net10.0-windows`)**: The per-user installer (`installer/MetaBrain.Installer.exe`) for install/upgrade/uninstall; it always keeps the data root.
8. **`MetaBrain.S1T4.Smoke` (`net10.0-windows`)**: Comprehensive end-to-end integration and smoke verification suite for IPC, crypto, session lifecycles, catalog discovery, installation and MCP communication.

---

## Security & Scoped Sharing Model

The security model is governed by architectural decision **CR01 / D016**:

- **Vault Key Lifecycle:** The owner provisions and unlocks the vault using a passphrase or recovery code. The running service holds data keys in volatile memory only while unlocked. Locking drains active requests, wipes keys, and immediately invalidates all active agent sessions and pending tokens.
- **Published Catalog Projection:** Agents discover available memories via a plaintext projection (`published-catalog.json`) containing only owner-approved IDs, labels, and summaries. Private memory bodies and private indices are never exposed during discovery.
- **Access Requests & Attached Console Preview:** Agents submit requests specifying catalog IDs, intended purpose, and declared provider/model context. The owner reviews exact request bodies in memory on an attached console (`owner request-preview`); redirected streams fail closed to prevent accidental plaintext leakage.
- **Single-Use Token Redemption:** Approved grants mint a 256-bit opaque bearer saved to a DPAPI-protected handoff file. Agents redeem this token once to establish a process-local session bound to the current unlock epoch and policy generation. Replay attempts, expired tokens, or revoked sessions fail closed.
- **Authorize-Decrypt-Reauthorize Retrieval:** Scoped read requests authorize the operation, decrypt the vault ciphertext, re-verify policy generation and registration, and stream raw bytes or strict UTF-8 text directly to the caller.

---

## MCP Tools for AI Agents

The packaged `mcp/MetaBrain.Mcp.exe` serves the agent-scoped MCP tools over stdio against the
shared locked host. `--version` and `--help` print the entrypoint identity and the release layout
on stderr, so stdout stays JSON-RPC only. The client discovers the exact schemas at `initialize`;
the owner approves or narrows requests in the CLI or the Owner console, never in the MCP client.

| MCP Tool | Description |
|---|---|
| `metabrain_catalog_list` | Lists owner-published catalog metadata (IDs, labels, summaries; no bodies). |
| `metabrain_catalog_query` | Queries published catalog entries matching a search text. |
| `metabrain_access_request` | Submits an access request with purpose, operations, and model context. Returns an opaque request ID. |
| `metabrain_request_status` | Checks approval status of a previously submitted access request. |
| `metabrain_redeem` | Redeems an owner-approved DPAPI token handoff file into a local DPAPI session file. |
| `metabrain_session_inspect` | Inspects metadata of an active session file without exposing secrets. |
| `metabrain_scoped_read` | Retrieves approved resource revision content in memory (UTF-8 text or base64 binary). |
| `metabrain_authorize` | Checks whether a session file authorizes one scoped operation (allow/deny decision only, no content). |
| `metabrain_memory_read` | Reads one approved memory item revision with its stored citations and availability warnings (`memory.read` scope only; never opens source content). |
| `metabrain_memory_search` | Scoped lexical recall over approved items (kind/level, current/history/as-of, archived), returning lean hits only. |
| `metabrain_memory_brief` | Projects a short brief under the frozen `memory.context` grant and character budget; reads never change lifecycle state. |
| `metabrain_memory_feedback` | Records useful/wrong/stale feedback on one seen revision (`memory.feedback` scope only; never upgrades verification). |
| `metabrain_feedback_read` | Reads one stored feedback record (`feedback.read` scope only). |
| `metabrain_source_read` | Reads one approved registered-source revision (`source.read` scope only). |
| `metabrain_source_snapshot_read` | Reads one approved source-snapshot revision as exact bytes (`source.snapshot.read` scope only). |
| `metabrain_propose` | Proposes a create/revise memory item for owner review (`proposal.create` plus an approved inbox); pending only, never published. |
| `metabrain_relation_resolve` | Resolves endpoint titles only inside the frozen read scope; ambiguous endpoints are asked back, never mutated. |
| `metabrain_relation_list` | Lists relation metadata only when both pinned endpoints are readable in the frozen scope. |
| `metabrain_relation_create` | Creates a proposed typed relation (`link.create` plus endpoint read rights); similarity is never inferred as support. |
| `metabrain_relation_supersede` | Creates a proposed `supersedes` relation only (`link.supersede` plus endpoint read rights). |
| `metabrain_relation_unlink` | Retracts a relation with explicit `link.remove` and endpoint read rights; undo uses restore. |
| `metabrain_relation_restore` | Restores a prior unlink to `proposed`, never accepted (`link.restore` plus endpoint read rights). |

---

## Prerequisites

- **Operating System:** 64-bit Windows releases that Microsoft lists as supported for .NET 10 at
  release time. The 1.4 CLI and MCP server need no desktop/WPF capability. The product is not
  verified on every supported Windows SKU; Linux, macOS, ARM, Server Core and Nano Server are out
  of scope for this release (uses Windows Named Pipes and DPAPI). The optional `open` document
  session needs an installed Chromium-based browser (Chrome or Edge); the rest of the CLI does not.
- **Running the 1.4 candidate:** use the dedicated CLI setup executable and/or the standalone MCP
  ZIP. They are self-contained `win-x64` distributions, so no .NET SDK, installed .NET runtime,
  source checkout or PowerShell is required. The 1.4 candidate has no Owner UI installer; 1.1.0
  keeps its own unchanged WPF downloads. The repository payload bundle described below is
  developer tooling, not the end-user installation path.
- **Building from source:** [.NET 10 SDK](https://dotnet.microsoft.com/) (`net10.0` /
  `net10.0-windows`), plus PowerShell for the release script.
- **AI Clients (Optional):** Official [Codex CLI](https://github.com/openai/codex) or
  [Oh My Pi (OMP)](https://github.com/can1357/oh-my-pi) with MCP stdio support.

---

## Getting Started

Release 1.1.0 remains published and unchanged at
[`v1.1.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.1.0):
[`MetaBrain-CLI-1.1.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0/MetaBrain-CLI-1.1.0.exe),
[`MetaBrain-UI-1.1.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0/MetaBrain-UI-1.1.0.exe)
and
[`MetaBrain-MCP-1.1.0.zip`](https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0/MetaBrain-MCP-1.1.0.zip),
with its frozen hashes in the [1.1.0 release record](docs/release-1.1.0.md). 1.1.0 adds owner
folder enrollment, raw-preserved partial ingestion and folder refresh; an installed 1.0.0 binary
does not gain those surfaces, and a 1.1.0 journal v2 source fails closed on a 1.0.0 binary instead
of being rewritten.

Release 1.0.0 remains published and unchanged at
[`v1.0.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.0.0):
[`MetaBrain-CLI-1.0.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.0.0/MetaBrain-CLI-1.0.0.exe),
[`MetaBrain-UI-1.0.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.0.0/MetaBrain-UI-1.0.0.exe)
and
[`MetaBrain-MCP-1.0.0.zip`](https://github.com/triet4p/my-meta-brain/releases/download/v1.0.0/MetaBrain-MCP-1.0.0.zip),
with its frozen hashes in the [1.0.0 release record](docs/release-1.0.0.md).

**1.2.0 is published** at [`v1.2.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.2.0):
[`MetaBrain-CLI-1.2.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.2.0/MetaBrain-CLI-1.2.0.exe)
and
[`MetaBrain-MCP-1.2.0.zip`](https://github.com/triet4p/my-meta-brain/releases/download/v1.2.0/MetaBrain-MCP-1.2.0.zip),
with its frozen hashes in the [1.2.0 release record](docs/release-1.2.0.md). It added the encrypted
owner workspace, original read/export, backup/restore/purge and the CLI-complete owner path.

**1.3.0 is published** at [`v1.3.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.3.0):
[`MetaBrain-CLI-1.3.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.3.0/MetaBrain-CLI-1.3.0.exe)
and
[`MetaBrain-MCP-1.3.0.zip`](https://github.com/triet4p/my-meta-brain/releases/download/v1.3.0/MetaBrain-MCP-1.3.0.zip),
with its frozen hashes in the [1.3.0 release record](docs/release-1.3.0.md). It ships the same two
assets — a CLI setup and the standalone MCP ZIP, with the document bridge compiled into the
CLI/shared runtime — and no Owner UI installer.
1.3.0 adds CLI lexical workspace search, the optional foreground `open` document
preview/edit session, and fail-closed lock/exit/disconnect/expiry revocation. The WPF Owner UI
1.1.0 is the frozen legacy product: this release refuses to change its installation in place and
makes no compatibility claim for it.

**1.4.0 is published** at [`v1.4.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.4.0):
[`MetaBrain-CLI-1.4.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.4.0/MetaBrain-CLI-1.4.0.exe)
and
[`MetaBrain-MCP-1.4.0.zip`](https://github.com/triet4p/my-meta-brain/releases/download/v1.4.0/MetaBrain-MCP-1.4.0.zip),
with its frozen hashes in the [1.4.0 release record](docs/release-1.4.0.md). It keeps the same two
products — the CLI setup and the standalone MCP ZIP, with the shared runtime compiled in — and adds
the owner cognitive workflows: six independent facets, encrypted revisioned passage annotations with
review, grounded structuring results that stay pending, and lifecycle/intention/recall for memory
items and classified annotations, all through the CLI. The MCP agent tool surface is unchanged.

**1.5.0 is published** at [`v1.5.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.5.0):
[`MetaBrain-CLI-1.5.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.5.0/MetaBrain-CLI-1.5.0.exe)
and
[`MetaBrain-MCP-1.5.0.zip`](https://github.com/triet4p/my-meta-brain/releases/download/v1.5.0/MetaBrain-MCP-1.5.0.zip),
with its frozen hashes in the [1.5.0 release record](docs/release-1.5.0.md). It keeps the same two
products — the CLI setup and the standalone MCP ZIP — and adds the encrypted structure-aware passage
index, scoped hybrid workspace search with explicit `literal_match` / `semantic_candidate` output,
purpose-aware recall regions, coalesced background refresh and `--open-rank` exact-revision opening,
all through the owner CLI. A semantic candidate is an unconfirmed neighbour, not evidence or an
answer; exact/lexical evidence still ranks first. Hybrid indexing and search require the owner to
supply a pinned local E5 model directory explicitly — the product bundles no model weights, downloads
none and calls no cloud embedding provider — and the dynamic tokenizer requires an owner-installed
Microsoft Visual C++ v14 x64 Redistributable 14.43.34808 or later. The MCP agent tool surface is
unchanged at 22 tools. The publication and public smoke are recorded in
[`artifacts/sprint-15/publication/S15-PUBLISH-P2.md`](artifacts/sprint-15/publication/S15-PUBLISH-P2.md);
independent public-evidence review [`artifacts/sprint-15/reviews/S15-PUBLIC-C1-R1.md`](artifacts/sprint-15/reviews/S15-PUBLIC-C1-R1.md) (PASS) and the fresh Bronze checkpoint [`artifacts/sprint-15/checkpoints/S15-PUBLIC-C1.md`](artifacts/sprint-15/checkpoints/S15-PUBLIC-C1.md) (`9780091f`) are complete.

**1.6.0 is a prepared, locally verified candidate — not published.** No `v1.6.0` tag, Release or
download exists. The candidate keeps the same two products and adds Sprint 16 typed workspace
relations between stable file, folder and passage endpoints: attributed create/inspect/accept/
reject/retract/restore with exact revision pins, review-needed staleness, a rebuildable adjacency
derivative and scoped export. Owner hybrid search returns bounded `relationCandidates` and one
deterministic `rankedEvidence` list that keeps direct literal evidence first, surfaces
supports/contradictions/derived evidence for why/decision questions and never filters
contradictions; traversal stays within folder and exact-revision scope and is limited to accepted/
current edges, two hops and the request limit. `owner relation-list --workspace-node-id` inspects the
exact endpoint neighborhood without expanding folder membership. The MCP agent surface is unchanged
at 22 tools; agent relation calls remain proposals and cannot accept or share owner workspace
relations. The candidate identity and hashes are in the [1.6.0 release record](docs/release-1.6.0.md);
the [release contract](docs/release-contract.md#product-and-support-matrix-for-160-s16-t6) and
[release notes](docs/release-notes-1.6.0.md) state the exact scope and limits.

### Developer bundle: build the payload from a checkout

```powershell
powershell.exe -NoProfile -File scripts\release\publish-release-bundle.ps1
```

The result is `runtime\release\MetaBrain-<version>-win-x64\`, its `.zip` and the zip's SHA-256.
`release-manifest.json` inside the bundle records the product version, the entrypoints, and every
file's size and SHA-256. Unpack the zip wherever the application should live. The unpacked layout
is the payload the installer consumes:

```text
cli\MetaBrain.Cli.exe               owner CLI (--help, --version, lifecycle, owner commands)
mcp\MetaBrain.Mcp.exe               agent MCP server over stdio
service\MetaBrain.Connections.exe   shared runtime host (started by the shared lifecycle)
installer\MetaBrain.Installer.exe   per-user CLI/MCP install/upgrade/uninstall
```

### 2. Install for the current user

```text
installer\MetaBrain.Installer.exe install --payload <unpacked-bundle> --install-root %LOCALAPPDATA%\MetaBrain\App --data-root %LOCALAPPDATA%\MetaBrain\Data
```

The install is per-user, needs no administrator rights, and changes no registry, service,
autostart, firewall, account or environment state. `--start` starts the host now as an explicit
opt-in; there is no login autostart registration.

An install **never creates the vault**: the first start is locked and uninitialized, and the owner
creates the vault explicitly. `<install root>\install-state.json` records the instance, the data
root and the product version. The install root (application files) and the data root (vault,
settings, runtime state, source snapshots, backups, recovery material) are separate sibling
directories on purpose: an upgrade replaces the application and leaves the data alone, and an
uninstall removes the application and keeps the data.

### 3. Owner CLI quick start

Every command below is the installed `cli\MetaBrain.Cli.exe`. No PowerShell helper, hand-written
settings file or second frontend is involved. `--help` prints the release layout and
`owner --help` lists every workflow group:

```text
cli\MetaBrain.Cli.exe --version
cli\MetaBrain.Cli.exe lifecycle start --data-root %LOCALAPPDATA%\MetaBrain\Data
cli\MetaBrain.Cli.exe lifecycle status --data-root %LOCALAPPDATA%\MetaBrain\Data
cli\MetaBrain.Cli.exe owner provision --control-pipe <control pipe printed by lifecycle status>
cli\MetaBrain.Cli.exe owner unlock --control-pipe <control pipe>
cli\MetaBrain.Cli.exe owner status --control-pipe <control pipe>
cli\MetaBrain.Cli.exe owner memory-create --control-pipe <control pipe> --body "..." --title "..."
cli\MetaBrain.Cli.exe owner library --control-pipe <control pipe> --query "..."
cli\MetaBrain.Cli.exe owner lock --control-pipe <control pipe>
cli\MetaBrain.Cli.exe lifecycle stop --data-root %LOCALAPPDATA%\MetaBrain\Data
```

A clean data root needs no hand-written settings: `lifecycle start` creates the ordinary
nonsensitive `service-settings.json` and starts the shared host locked. An existing root keeps its
settings, endpoints and vault byte-for-byte.

Credentials travel only through secure interactive prompts — the vault passphrase, the recovery
key and the provider key are never option values, environment values, config entries, log lines
or model context. Destructive or authority-minting acts print a frozen preview and require a
typed confirmation (`ISSUE`, `SET`, `PUBLISH`, `APPROVE`, `REJECT`, `PURGE`). Request bodies are
shown only on an attached owner console: `owner request-preview` refuses redirected output
(`preview_redirect_denied`). Exit codes are truthful: `0` done, `2` usage (nothing changed), `3`
refused/locked/unavailable/denied, `4` invalid input or unwritable output, `5` a job that ended
without completing, `1` transport or unexpected failure.

Sharing one item with an agent is also a CLI workflow, with no script and no second frontend:

```text
cli\MetaBrain.Cli.exe owner requests --control-pipe <control pipe>
cli\MetaBrain.Cli.exe owner request-preview --control-pipe <control pipe> --request-id <request id>
cli\MetaBrain.Cli.exe owner request-approve --control-pipe <control pipe> --request-id <request id> --preview-id <preview id> --operations memory.read,memory.search --expires-at-utc <UTC instant ending in Z or +00:00> --handoff-file <new file>
```

`owner request-preview` shows the exact bodies and must run in the attached terminal (redirected
output is refused). `--operations` must contain the operations the preview printed (for example
`memory.read,memory.search`); omitting it narrows the approval to `resource.read`, which the
service refuses as `request_invalid` when the request did not ask for that operation.
`--resource-ids` is optional narrowing, and `--handoff-file` must be a new path: the typed
`APPROVE` mints the one-time token there, and the agent redeems that path with
`metabrain_redeem`.

### 3.1 Workspace search and the optional document session

The encrypted workspace is searched and read entirely through the CLI; no browser is needed:

```text
cli\MetaBrain.Cli.exe owner workspace-search --control-pipe <control pipe> --query "cache miss" [--folder-id <folder id>] [--format json]
cli\MetaBrain.Cli.exe owner workspace-read --control-pipe <control pipe> --file-id <file id> [--revision <n>] --output-file <new file>
cli\MetaBrain.Cli.exe owner workspace-revise --control-pipe <control pipe> --file-id <file id> --expected-content-revision <live> --input-file <new content file>
```

Search is exact and local: it matches file names, logical paths, Markdown headings and decoded
UTF-8/UTF-16 text, returns the file revision and byte-range digest of each hit, and adds no
semantic ranking. A hit can be opened at its exact location with
`open <file-id> --revision <n> --anchor-offset <bytes> --anchor-length <bytes> --anchor-sha256 <digest>`;
a stale or mismatched anchor is refused.

`open` is optional and foreground-only: it starts an ephemeral `127.0.0.1` bridge for exactly one
workspace file at one content revision, hands the page a per-process capability (never a key, path
or owner API), and offers inert Markdown/CSV/text preview plus raw-text editing with an explicit
revision-gated save. Ctrl+C, CLI exit, `--lifetime-seconds`, vault lock or a service restart stops
the bridge and clears the page. No plaintext HTML, draft, cache or private URL is written to disk.
The settled support matrix, encoding/newline policy and erasure limits are in
[`docs/META-BRAIN.md` §13.5](docs/META-BRAIN.md#135-s13-t1-document-bridge-contract--foreground-open)
and the operational steps are in [`docs/OPERATIONS.md`](docs/OPERATIONS.md).

### 4. Owner console (frozen 1.1.0 legacy — not part of 1.4)

```text
owner-ui\MetaBrain.OwnerUi.exe
```

This console is the 1.1.0 WPF product. It is documented for history only: 1.2 through 1.4 ship no
UI installer and do not upgrade, replace or edit a 1.1.0 installation, and no current compatibility
is claimed for it. Use the CLI for every owner workflow; the optional `open` document session covers
preview and explicit editing.

The console opens on the **Owner session** view with no instance selected: it starts nothing,
provisions nothing and unlocks nothing. Pick the data-root folder (`Choose folder…` or type the
path), choose **Use this folder**, then **Start service** (or **Connect to service** when the
shared host is already running from the CLI or MCP). **Check status** re-reads the real host
state; **Restart service** is a fresh locked host with no surviving session; **Stop service** is
the only act that ends the shared host. Closing the console never stops the host another
frontend is using. The title bar carries the same product version the other entrypoints report.

The full workflow coverage per view (vault & key, library, inbox, relations, sharing, sources,
jobs, intentions, review queue, backup & export, operations & audit) and its keyboard shortcuts
are documented in [`docs/OPERATIONS.md`](docs/OPERATIONS.md) §3.3 and §5.

### 5. MCP client registration

The standalone self-contained MCP 1.0.0 ZIP includes `mcp\MetaBrain.Mcp.exe`, its shared
runtime at `service\MetaBrain.Connections.exe`, and package/license manifests. Extract the
complete ZIP to a stable directory while preserving its folders; configure the absolute
extracted MCP executable path with either `--data-root <owner data root>` or
`--config <service-settings.json>`. No CLI/Owner UI installation, SDK, repository or PowerShell is
required. The MCP entrypoint connects to the same shared locked host and serves only the
existing agent-scoped tools; owner authority remains in the CLI or Owner console.

Codex CLI and Oh My Pi configuration examples, observed client versions, compatibility limits,
and path-quoting instructions are in [`docs/OPERATIONS.md`](docs/OPERATIONS.md) §3.2.1. Register
only with the client's supported process/project scoped configuration; do not auto-edit profiles
or place keys or bearer tokens in configuration.

### 6. Upgrade and uninstall

- **Upgrade:** run `install` again from a newer unpacked bundle with the same install root. The
  installer stops the running host, replaces the application directories, and keeps the data
  root, the vault, the pipe endpoints, the source snapshots, the backups and the recovery
  material. The vault opens with the same passphrase and recovery key as before.
- **Uninstall:** run the installed `<install root>\installer\MetaBrain.Installer.exe uninstall`.
  It stops the host, removes the application files and the login entry (only if it points at
  that installation), and always preserves the data root. It takes no options and has no
  option that deletes the owner's data.

### 7. Build and test from source

```powershell
# Build the products
dotnet build src/MetaBrain.Cli/MetaBrain.Cli.csproj -c Release
dotnet build src/MetaBrain.OwnerUi/MetaBrain.OwnerUi.csproj -c Release
dotnet build src/MetaBrain.Mcp/MetaBrain.Mcp.csproj -c Release

# Run the permanent smoke suite
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj
```

The suite also exposes named scenarios, including `s5-t1` for the owner application boundary (owner-channel states plus agent-channel owner-operation denials), `s5-t2` for the owner vault key path (provisioning, wrong/missing-key refusal, lock invalidation and owner-held recovery), `s5-t3` for the owner library (bilingual search, all filters, service-side ranking, evidence detail with citations, title edit, collection non-grant and the lock/stale-answer boundary), `s5-t4` for owner capture and proposal review, `s5-t5` for relations, `s5-t6` for catalog publication, access requests with bound preview/narrowing/approval, single-use redemption, revoke and expiry, `s8-t2` for encrypted backup/restore/export, and `s8-t1` for the installed lifecycle (install, locked start, provision/unlock, grant/redemption, stop/restart, uninstall with the data preserved).

```powershell
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t1 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t2 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t3 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t4 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t5 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t6 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s8-t2 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s8-t2-ui src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
```

`s8-t2` proves the backend acts against the real service: a concurrent encrypted
backup, the ciphertext and the temporary output, the clean restore with the
passphrase and with the recovery key, the wrong/missing/lost key and the
tampered/truncated refusals, the restored records, relations, sources, archive
and deletion, the rebuilt private index, the restored vault's refusal of an
already used token as `token_used` (not `token_unknown`), and a scoped readable
export whose out-of-scope citation is withheld whole. `s8-t2-ui` proves the
owner console's **Backup & export** view on the real WPF window: the export
scope picked by title, the readable file written and read back, the encrypted
backup file inspected as ciphertext, the clean restore, the wrong and missing
credential refusals, the refusal of an existing target, the recovery key
restore, the lock that clears the paths, the selection and the credential while
the restore stays available, and the real renders of the view.

---

## Project Status & Roadmap

The project follows the authoritative milestone roadmap in [`docs/PLAN.md`](docs/PLAN.md).
Milestones 1–9 and Sprint 9's three-product delivery are complete; the public 1.0.0 release,
asset-name cutover, updated Pages deployment and public smoke are complete. Sprints 11–15 published
1.1.0–1.5.0; Sprint 16 has a locally verified 1.6.0 candidate awaiting review, checkpoint and
owner-authorized publication. See
[`docs/PLAN.md`](docs/PLAN.md) for the authoritative status.

- [x] **Milestone 1 (M1) — Encrypted Vault & Scoped Sharing (Sprint 1):** User-controlled key lifecycle, authenticated ciphertext, owner-approved catalog publication, single-use token issuance/redemption, and in-memory scoped reads verified with official Codex and OMP clients.
- [x] **Milestone 2 (M2) — Durable Encrypted Core (Sprint 2):** Canonical encrypted item/source/zone/revision models, relations, and rebuildable protected index.
- [x] **Milestone 3 (M3) — Faithful Encrypted Capture (Sprint 3):** Read-only allowlisted transcript ingestion from Codex/OMP sessions, encrypted caching, and lock-aware refresh.
- [x] **Milestone 4 (M4) — Scoped Agent Memory Access (Sprint 4):** Rich retrieval, evidence briefs, proposals, and linking over MCP.
- [x] **Milestone 5 (M5) — Owner Workbench (Sprint 5):** Desktop UI (WPF) for unlock, review, catalog curation, and token handoffs.
- [x] **Milestone 6 (M6) — Grounded Structuring (Sprint 6):** Layered syntheses, coding profile extraction, and strict provider egress controls.
- [x] **Milestone 7 (M7) — Time & Lifecycle (Sprint 7):** Temporal recall, contextual intentions, active forgetting, and audited purging.
- [x] **Milestone 8 (M8) — Daily Production (Sprint 8):** Installer, automated backups, encrypted restore/recovery, and production hardening.
- [x] **Milestone 9 (M9) — Public Release 1.0.0 & Onboarding (Sprint 10):** CLI, Owner UI and MCP publication, landing page and scenario guides. The public Release assets and Pages site are live under the approved short filenames; refreshed deployment and public smoke are complete.

---

## License

Meta Brain is licensed under the [MIT License](LICENSE). Copyright (c) 2026 triet4p.
The release assets include this project license and separate Microsoft .NET runtime
licenses and third-party notices; those bundled components retain their own terms.

## Documentation Links

- **Architecture & Product Specification:** [`docs/META-BRAIN.md`](docs/META-BRAIN.md)
- **Requirements & Change Control Registry:** [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md)
- **Master Plan & Milestones:** [`docs/PLAN.md`](docs/PLAN.md)
- **Daily Installation and Operations Runbook:** [`docs/OPERATIONS.md`](docs/OPERATIONS.md)
- **Sprint 1 Execution & Gate Records:** [`docs/sprint-plans/sprint-1.md`](docs/sprint-plans/sprint-1.md)
- **Architecture Decision Log:** [`.agents/memory/decisions.md`](.agents/memory/decisions.md)
- **Agent Guidelines & Operational Invariants:** [`AGENTS.md`](AGENTS.md)
