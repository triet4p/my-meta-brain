# Meta Brain

> A local-first, owner-controlled encrypted second brain and scoped memory sharing service for AI agents.

Meta Brain serves as an owner-controlled second brain designed to preserve evidence, recall context, connect insights, and selectively synthesize knowledge over time. Built local-first for daily engineering and research workflows, Meta Brain interfaces with normal AI coding assistants (such as Codex CLI and Oh My Pi) via the Model Context Protocol (MCP) while maintaining strict cryptographic boundaries.

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

The project is structured into three clean layers (.NET 10 on Windows):

```
┌────────────────────────────────────────────────────────┐
│                   AI Clients (MCP)                     │
│         Codex CLI  /  Oh My Pi (OMP)  /  Tools         │
└──────────────────────────┬─────────────────────────────┘
                           │ stdio (JSON-RPC 2.0)
┌──────────────────────────▼─────────────────────────────┐
│              MetaBrain.Connections                     │
│  - Stdio MCP Agent Bridge (agent-mcp)                  │
│  - Windows Named-Pipe Server (Owner & Agent Pipes)     │
│  - Owner Control CLI (owner) & Service Host            │
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

### Components

1. **`MetaBrain.Core` (`net10.0`)**: The domain and security core. Implements authenticated ciphertext storage, owner-managed access requests, scope snapshots, and in-memory session lifecycles. Independent of UI and transport layers.
2. **`MetaBrain.Application` (`net10.0`)**: The mediation and application-service layer. Dispatches typed operations between channels while strictly enforcing owner vs. agent policy boundaries.
3. **`MetaBrain.Connections` (`net10.0-windows`)**: Windows named-pipe endpoints, Windows service control, owner management CLI, and the vault-blind stdio MCP bridge.
4. **`MetaBrain.S1T4.Smoke` (`net10.0-windows`)**: Comprehensive end-to-end integration and smoke verification suite for IPC, crypto, session lifecycles, catalog discovery, and MCP communication.

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

When running `MetaBrain.Connections agent-mcp --agent-pipe <pipe-name>`, the stdio bridge exposes the following MCP tools:

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

---

## Prerequisites

- **Operating System:** Windows 10 / 11 or Windows Server (uses Windows Named Pipes and DPAPI).
- **Runtime / SDK:** [.NET 10 SDK](https://dotnet.microsoft.com/) (`net10.0` / `net10.0-windows`).
- **AI Clients (Optional):** Official [Codex CLI](https://github.com/openai/codex) or [Oh My Pi (OMP)](https://github.com/canhld94/oh-my-pi) with MCP stdio support.

---

## Getting Started

### 1. Build the Solution

Clone the repository and build all projects using the .NET CLI:

```powershell
# Build the entire project
dotnet build src/MetaBrain.Connections/MetaBrain.Connections.csproj -c Release

# Build the smoke test harness
dotnet build tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -c Release
```

### 2. Start the Service (Console Mode)

Create a configuration file (e.g., `settings.json`):

```json
{
  "OwnerPipeName": "metabrain-owner",
  "AgentPipeName": "metabrain-agent",
  "VaultDirectory": "C:\\path\\to\\vault",
  "CatalogDirectory": "C:\\path\\to\\catalog"
}
```

Run the background console service:

```powershell
dotnet run --project src/MetaBrain.Connections/MetaBrain.Connections.csproj -- serve-console --config settings.json
```

### 3. Owner Workflow (CLI)

In another terminal, use the `owner` CLI subcommands to manage the vault:

```powershell
# Check service status (initially locked)
dotnet run --project src/MetaBrain.Connections/MetaBrain.Connections.csproj -- owner status --pipe metabrain-owner

# Provision a new vault key
dotnet run --project src/MetaBrain.Connections/MetaBrain.Connections.csproj -- owner provision --pipe metabrain-owner --passphrase "YourSecurePassphrase"

# Unlock the vault
dotnet run --project src/MetaBrain.Connections/MetaBrain.Connections.csproj -- owner unlock --pipe metabrain-owner --passphrase "YourSecurePassphrase"

# Publish catalog entries
dotnet run --project src/MetaBrain.Connections/MetaBrain.Connections.csproj -- owner catalog-publish --pipe metabrain-owner --id "lesson-01" --label "Core Lesson" --description "Summary of lesson"

# Review pending agent access requests (must be an attached console)
dotnet run --project src/MetaBrain.Connections/MetaBrain.Connections.csproj -- owner request-preview --pipe metabrain-owner --request-id <REQUEST_ID>

# Approve request and mint token handoff
dotnet run --project src/MetaBrain.Connections/MetaBrain.Connections.csproj -- owner request-approve --pipe metabrain-owner --preview-id <PREVIEW_ID> --handoff-file "token.handoff"

# Lock the vault at any time
dotnet run --project src/MetaBrain.Connections/MetaBrain.Connections.csproj -- owner lock --pipe metabrain-owner
```

### 4. Start the Owner Console

Build and start the Windows owner console (S5-T1). It attaches an explicit owner session to a service that is already running and shows the real vault state the service reports:

```powershell
# Build the owner console
dotnet build src/MetaBrain.OwnerUi/MetaBrain.OwnerUi.csproj -c Release

# Start the console against the owner control pipe of a running service
src/MetaBrain.OwnerUi/bin/Release/net10.0-windows/MetaBrain.OwnerUi.exe --control-pipe metabrain-owner
```

Choose **Connect to service** to open the owner session, **Check again** to probe the service for its current state, and **End session** to drop it. The console reports a stopped service as unavailable instead of a stale unlocked vault, reports an endpoint that refuses owner authority (for example the agent pipe) as a denial, and holds no vault key, no private content and no listening socket.

The **Vault & key** view is where the vault itself is created, opened and locked (S5-T2). It sends the credential you type to the running service over the owner control pipe in one request; the service owns the vault, the key and the lock lifecycle. Provisioning shows the recovery key the service returns once, tells you to keep your own copy outside this machine, and stores no copy itself; unlock takes your passphrase, recovery unlock takes the recovery key you saved, and **Lock vault now** asks the service to drain in-flight private work, clear the key, invalidate pending tokens and live sessions and stop private reads, ingestion and jobs. A wrong, missing or unusable key leaves the vault locked and is reported as a refusal, never as an unavailable service. Locking, disconnecting, ending the session and a refused credential all clear the credential fields and the displayed recovery key. If both the passphrase and the recovery key are lost, the encrypted data cannot be opened again: there is no reset, no backdoor and no automatic unlock. Library, capture, review, relations, catalog and session-console work arrive with their own sprint tasks.

The **Library** view is where you search, browse and open what the vault holds (S5-T3). Type a query (Vietnamese or English) or leave it empty to browse, set the filters you want — zone, owner collection, kind, abstraction level, an inclusive UTC time window, and whether to show current items or history — and press **Search library**. The service applies every filter and ranks the page; this console only shows the answer it received, with the exact IDs, revisions, statuses and scores. Choose a result and press Enter to read that exact revision with its stored evidence level, author, executor, creation method, body and citations; the citation status (current, superseded, unknown) and any warning are the service's own answer, so a missing source stays visibly missing. Choose a citation and press Enter to open that source registry revision, edit the title and **Save new title** to write one new revision of the same item, and **Rebuild library index** to materialize a generation after unlocking or after a write (the service refuses to search a generation that no longer matches the vault). Locking the vault clears the page, the open item, the open source and your search text, and an answer already in flight is discarded rather than restoring them. Keyboard: Ctrl+F focuses the search box, Enter searches, the arrows choose a result, Enter opens it or its citation, and Ctrl+L locks the vault.

The **Inbox** view is where you capture a note and review what agents proposed (S5-T4). Type the text, the zone it belongs to and — if you want — a title, a locator/URL, a source speaker and a source date, then press **Save capture**: the text is stored as an encrypted registered source in your vault before any structuring step could run, so the note survives a restart and a provider failure, and nothing is fetched, classified or summarized (a URL is only a locator, never a paper you have read). Text this console cannot encode as UTF-8 is refused with an explanation and stays in the editor, and a capture the service refuses changes nothing and keeps your text too. **Read it back** serves the exact characters the vault holds for a note ID, which is also how you check a saved note after restarting the service. **Refresh proposal inbox** lists the proposals agents left for you — a proposal is a pending write and nothing else: it is not a memory item and it changed nothing. **Open selected proposal** shows the proposed content beside the source its citation names (read at exactly the revision the citation pins, with its registry metadata), names the exact revision a revise proposal pins, and shows the agent's user-confirmation claim as a claim, never as verified. Edit the kind, level, title and body you want, then **Publish reviewed content** to write the reviewed content into your vault as a memory item and record your publication decision on it, or **Reject proposal** to record your decision — with the rationale the service requires — without applying the proposal to any item. The author, creation method and evidence level always come from the proposal, so approving an AI-written note never makes it user-written or user-confirmed, and publishing a proposal whose underlying item moved is refused with nothing overwritten. **Undo decision** withdraws a published review item or returns a rejected one to proposal, on the same canonical record, with your rationale in its history and nothing rewritten or duplicated. Locking asks once when the capture editor still holds text you never saved, then clears the editor, the proposal inbox and every open source.

The **Relations** view is where you see and shape the links of your vault (S5-T5). **Read relation set** asks the service for every link with its live revision, who recorded it and how, and the review state the service computed; **Inspect selected relation** opens one link at exactly the revision that was served, with both pinned endpoints, its reason, the act that last changed its state and the words a caller reported with it. To add a link, **Find endpoints in library** ranks candidate memory items from the service's materialized index generation (rebuild it in **Library** when it is behind the vault), or name a registered source by ID and live registry revision, then use one as the subject and one as the object, name the relation ID, choose the kind — `related_to` is an association, `supports` asserts that one endpoint is evidence for the other, and nothing here ever turns a resemblance into support — record it as your own accepted conclusion or as a proposal, and write the reason. A link an agent created over MCP appears in the same set with `executor=agent`, its reported words shown as a claim and a proposal state this view states is not your confirmation; **Confirm link (owner)** records the acceptance as your act and leaves the agent's words untouched. A pinned evidence endpoint that has since moved is reported as **NEEDS REVIEW** with the reasons and the endpoint the service names, while an association stays current; a derivation cycle is refused with `relation_cycle` and the same endpoints are still accepted as `related_to`; **Remove link** records your rationale, and **Undo removal** returns the link to proposed rather than asserting an acceptance again. The console keeps no link store and no visibility policy: what an agent session may see stays decided by that session's grant, and locking clears the list, the opened link, the candidates and your draft.

The **Sharing** view is where you publish what agents may discover and decide what they may read (S5-T6). **Preview publication** shows exactly the label/description you typed with the live mapped revision and mints nothing — this screen never fills public metadata from a private title, path or citation; **Publish this exact metadata** stores that triplet so discovery serves only the approved ID/label/description, and **Withdraw selected entry** removes it from future discovery. **Read access requests** lists each pending request with its purpose and catalogs (a request is never permission); **Preview selected request** binds its frozen snapshot with the exact resources, revisions, bodies and disclosure for your eyes only, and **Approve this narrowed scope** mints only a narrowed subset of those IDs and operations into a DPAPI-protected local handoff file whose path — never the token — is shown. **Reject request** records the terminal refusal with no grant. **Read live sessions** lists each session's effective frozen scope, expiry and egress, and **Revoke selected session** ends its bearer immediately; locking the vault ends every session and clears every sharing view, preview, handoff path and draft.

### 5. Running the Smoke Test Suite

Verify all security invariants, cryptoproof, token redemption, and IPC boundaries:

```powershell
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj
```

The suite also exposes named scenarios, including `s5-t1` for the owner application boundary (owner-channel states plus agent-channel owner-operation denials), `s5-t2` for the owner vault key path (provisioning, wrong/missing-key refusal, lock invalidation and owner-held recovery), `s5-t3` for the owner library (bilingual search, all filters, service-side ranking, evidence detail with citations, title edit, collection non-grant and the lock/stale-answer boundary), `s5-t4` for owner capture and proposal review (encrypted capture with malformed-input handling, restart read-back, pending proposals beside their cited source, publish/reject/undo with the proposal's own attribution, revision-conflict refusal and the lock/private-state boundary), `s5-t5` for the explicit relations workbench, `s5-t6` for the catalog/access-request/session console, `s5-t7` for the sources/organization console (capture, allowed enroll, mapping review, unknown/unsupported/missing refusals, replacement quarantine, pause/resume, refresh consent, reference registration, snapshot preserve/list/open/retain with stale refusal, collection non-grant, agent denials and the lock/stale-answer boundary with original bytes unchanged), and `s5-t1-console-fixture`/`s5-t2-console-fixture`/`s5-t3-console-fixture`/`s5-t3-console-corpus`/`s5-t4-console-fixture`/`s5-t4-console-inbox`/`s5-t4-console-proposals`/`s5-t5-console-fixture`/`s5-t5-console-corpus`/`s5-t6-console-fixture`/`s5-t6-console-corpus`/`s5-t7-console-fixture`/`s5-t7-console-corpus` for preparing an owner-console fixture:

```powershell
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t1 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t2 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t3 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t4 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t5 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s5-t6 src/MetaBrain.Connections/bin/Release/net10.0-windows/MetaBrain.Connections.exe
```

---

## Project Status & Roadmap

The project follows a phased milestone roadmap defined in [`docs/PLAN.md`](docs/PLAN.md):

- [x] **Milestone 1 (M1) — Encrypted Vault & Scoped Sharing (Sprint 1):** User-controlled key lifecycle, authenticated ciphertext, owner-approved catalog publication, single-use token issuance/redemption, and in-memory scoped reads verified with official Codex and OMP clients.
- [x] **Milestone 2 (M2) — Durable Encrypted Core (Sprint 2):** Canonical encrypted item/source/zone/revision models, relations, and rebuildable protected index.
- [ ] **Milestone 3 (M3) — Faithful Encrypted Capture (Sprint 3):** Read-only allowlisted transcript ingestion from Codex/OMP sessions, encrypted caching, and lock-aware refresh.
- [ ] **Milestone 4 (M4) — Scoped Agent Memory Access (Sprint 4):** Rich retrieval, evidence briefs, proposals, and linking over MCP.
- [ ] **Milestone 5 (M5) — Owner Workbench (Sprint 5):** Desktop UI (WPF) for unlock, review, catalog curation, and token handoffs.
- [ ] **Milestone 6 (M6) — Grounded Structuring (Sprint 6):** Layered syntheses, coding profile extraction, and strict provider egress controls.
- [ ] **Milestone 7 (M7) — Time & Lifecycle (Sprint 7):** Temporal recall, contextual intentions, active forgetting, and audited purging.
- [ ] **Milestone 8 (M8) — Daily Production (Sprint 8):** Installer, automated backups, encrypted restore/recovery, and production hardening.

---

## Documentation Links

- **Architecture & Product Specification:** [`docs/META-BRAIN.md`](docs/META-BRAIN.md)
- **Requirements & Change Control Registry:** [`docs/REQUIREMENTS.md`](docs/REQUIREMENTS.md)
- **Master Plan & Milestones:** [`docs/PLAN.md`](docs/PLAN.md)
- **Sprint 1 Execution & Gate Records:** [`docs/sprint-plans/sprint-1.md`](docs/sprint-plans/sprint-1.md)
- **Architecture Decision Log:** [`.agents/memory/decisions.md`](.agents/memory/decisions.md)
- **Agent Guidelines & Operational Invariants:** [`AGENTS.md`](AGENTS.md)
