# Meta Brain 1.4.0

Windows x64, self-contained, **unsigned**. SHA-256 verifies downloaded bytes only; it does not
authenticate the publisher.

**Status: release candidate, not published.** The 1.4.0 candidate is frozen and verified locally;
there is no public `v1.4.0` tag, Release, upload or deployment yet. Publication is a separate
owner-authorized step that follows the independent evidence review, the exact-snapshot checkpoint
and the final differential. Until then, download nothing from a URL that claims to be 1.4.0; the
exact candidate identity, sizes, SHA-256 values and verification are in the
[`1.4.0 release record`](release-1.4.0.md).

## Downloads (planned mapping, not live)

1.4.0 keeps exactly two products. These are the public filenames a later owner-authorized
publication would use; no 1.4.0 download is live today, and the frozen internal candidate filenames,
sizes and SHA-256 values are in the [release record](release-1.4.0.md).

- `MetaBrain-CLI-1.4.0.exe` — CLI setup. Installs `cli\MetaBrain.Cli.exe`, the shared runtime and the
  shared installer; installs and registers the CLI component only. The owner CLI carries every
  cognitive workflow described below, plus the existing workspace, search and optional `open`
  document session. No new Owner UI setup is shipped.
- `MetaBrain-MCP-1.4.0.zip` — standalone MCP server package. Extract it completely and register the
  absolute `mcp\MetaBrain.Mcp.exe` path. The agent tool surface is unchanged at 22 tools.
- There is **no** 1.4.0 Owner UI setup, `owner-ui` component or UI download, and no separate web
  console.

This release does not rebuild, rename or withdraw the published 1.3.0, 1.2.0, 1.1.0 and 1.0.0
assets. This release does not upgrade, replace, migrate or withdraw the 1.1.0 WPF console.

## What is new in 1.4.0

- **A six-facet cognitive vocabulary.** The owner CLI gains `owner taxonomy` and independent
  `world`, `experience`, `opinion`, `habit`, `intention` and `action-effect` facets. A passage may
  carry zero, one or several facets; no file is forced into one type and nothing is classified
  automatically.
- **Encrypted, revisioned passage annotations.** `owner cognitive-annotation-create/list/read`
  stores annotations against an exact workspace anchor (file ID, content revision, byte range and
  range digest) inside the unlocked vault. Owner-authored annotations are confirmed owner state;
  metadata and review history are versioned, and a stale anchor keeps its original revision instead
  of being silently rebound.
- **Query, source inspection and owner review.** `owner cognitive-annotation-query` filters by any of
  the six facets, existing memory kinds, review state, AI origin and source availability;
  `owner review-list` collects pending suggestions with source availability;
  `cognitive-annotation-read` identifies the exact file and revision. `workspace-read --revision`
  returns the anchored source bytes unchanged.
- **Attribution that survives correction.** `cognitive-annotation-correct/accept/reject` are
  revision-gated: a suggestion keeps `origin=AiSuggestion`, its author and evidence across an owner
  correction, and only an explicit accept or reject records the owner decision. A stale expected
  revision is refused. Saving a file or note never requires taxonomy or a model.
- **Cognitive-kind lifecycle, intentions and recall.** `lifecycle-create` applies the existing
  revision-gated lifecycle record to an annotation with a known cognitive kind, deriving question or
  intention state from the kind and refusing an unclassified annotation.
  `intention-trigger-set`, `intentions`, `lifecycle-intention-acknowledge` and
  `lifecycle-intention-complete` record one-time due/context notifications and distinguish
  acknowledgement from completion. `recall-apply`/`recall-read` record pin/demote/archive/restore
  without rewriting the annotation, its origin, review status or anchor.
- **Same two-product release contract.** One shared runtime and one version for the CLI, MCP server,
  shared host and installer (1.4.0); candidate-bound manifests, per-file and embedded-payload
  SHA-256 values, dependency/license inventories, source provenance and truthful unsigned status.
  The frozen 1.1.0 WPF console keeps its own version and is never built or packaged.

## Compatibility and upgrade

- The vault envelope format, workspace format and metadata schema are unchanged from 1.3.0
  (workspace format version 1). This release performs **no automatic migration**; existing 1.3
  workspaces open exactly and remain byte-compatible. Cognitive annotations are additional encrypted
  records under the existing vault.
- **Upgrade in place from 1.2.0 or 1.3.0 is supported.** The 1.4.0 CLI setup upgrades a CLI/MCP
  installation in the same install root: it drains/stops the shared host, replaces the application
  files, keeps the selected data root, vault, settings and pipe endpoints, and changes no owner data.
  Re-running the setup is the ordinary reinstall path.
- **Pre-1.2 installations are refused in place.** A 1.1.0 WPF installation, an unknown/mixed
  component registration, a data root owned by another installation or a live endpoint speaking
  another protocol revision is refused before the host is stopped or any file changes. Install this
  release beside it with separate install/data roots and distinct endpoints; the old installation
  and its data are untouched.
- **Uninstall keeps the data root.** Removing the CLI (or the final component) removes application
  files only; the data root, vault, backups and recovery material remain. Reinstalling and
  reselecting the same data root opens the same data with the owner's credential.
- **Rollback.** To return to 1.3.0, stop the 1.4 host, take an encrypted `owner backup`, and run the
  published 1.3.0 CLI setup as an upgrade over the same install root; the data root is preserved.
  A 1.3 build does not provide the 1.4 cognitive annotation workflows, so take and verify the backup
  before downgrading. The 1.3.0 record remains the authority for 1.3 behavior.

## Security and attribution boundaries

- The cognitive store is encrypted with the vault data key. Every read, write, correction and
  lifecycle act goes through the owner control pipe under the unlocked vault; a locked service
  refuses private reads with `vault_locked`.
- AI-origin suggestions stay distinct from owner belief. No read, query or correction promotes a
  suggestion to a verified fact, and no facet or kind is inferred automatically. The six facets are
  owner review labels, not truth levels.
- The optional browser document session is unchanged and remains document-only: the browser receives
  no vault key, filesystem handle, private path or owner cognitive API, and no plaintext annotation
  or draft is written by the product.
- The MCP agent surface is unchanged at 22 tools with no owner-authority tool; cognitive records and
  the document bridge are not agent tools.
- A structuring job may create pending suggestions for an exact workspace revision, but that is
  provider egress under the existing job consent/budget, not part of this release's local
  verification. No provider call is made or claimed by the release task.

## Signing and licensing

- Both assets are **unsigned**. SHA-256 is an integrity check only; it does not authenticate the
  publisher. Never disable SmartScreen, Defender or a security policy to install.
- Project license: MIT, `Copyright (c) 2026 triet4p`. The self-contained runtime contains
  `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT) and no WindowsDesktop/WPF runtime or external
  NuGet package. Per-dependency license texts and third-party notices ship inside each asset.

## Known limits

- Windows x64 only, on the framework-supported boundary in the
  [release contract](release-contract.md#product-and-support-matrix-for-140-s14-t6); not every
  listed Windows SKU was exercised.
- The six facets and kinds are owner review labels; there is no automatic classification, semantic
  retrieval or model ranking. Annotation query is structured filtering, and workspace search remains
  lexical.
- An annotation is bound to one exact file revision and byte range. A revised or deleted source
  leaves it `stale`/`Unavailable` at its original revision; it is never silently rebound.
- An unclassified annotation cannot receive an annotation lifecycle; classify it first. Due
  notifications are recorded once, and acknowledgement is distinct from completion.
- A provider/model structuring job that produces suggestions requires the existing job consent and
  budget; this candidate's verification made no provider call, and the separate T3 live result is
  not 1.4.0-candidate provider proof.
- The optional `open` document session needs an installed Chromium-based browser; non-Chromium
  engines are not covered. Sprint 14 does not change that surface, so no new browser run is claimed.
- A workspace act still carries at most 1 MiB of original bytes; larger files are refused rather
  than truncated. Source-session JSONL records remain read-only and are never served.
- The exact candidate identity, asset sizes, SHA-256 values, provenance, dependency/license
  inventories and the verification actually performed are in the
  [1.4.0 release record](release-1.4.0.md).
