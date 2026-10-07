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

### 4. Running the Smoke Test Suite

Verify all security invariants, cryptoproof, token redemption, and IPC boundaries:

```powershell
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj
```

---

## Project Status & Roadmap

The project follows a phased milestone roadmap defined in [`docs/PLAN.md`](docs/PLAN.md):

- [x] **Milestone 1 (M1) — Encrypted Vault & Scoped Sharing (Sprint 1):** User-controlled key lifecycle, authenticated ciphertext, owner-approved catalog publication, single-use token issuance/redemption, and in-memory scoped reads verified with official Codex and OMP clients.
- [ ] **Milestone 2 (M2) — Durable Encrypted Core (Sprint 2):** Canonical encrypted item/source/zone/revision models, relations, and rebuildable protected index.
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
