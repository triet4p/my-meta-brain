# Meta Brain 1.8.0 — migration, compatibility and known limits

**Status:** Sprint 18 release-candidate documentation. 1.7.0 remains the latest published release; 1.8.0 is a local candidate and is **not published, not a 2.0 PASS, and not an installed-host upgrade authorization**. This guide records the opt-in legacy-to-workspace migration contract, compatibility and downgrade limits, the remaining known limits, which removals are deferred to a separately approved G2, and the evidence map that traces reviewed Sprint 18 work to the candidate.

Normative sources: [roadmap §7](ROADMAP-2.0.md#7-definition-of-release-mỗi-sprint) and [§8 G2](ROADMAP-2.0.md#8-g2--final-release-200-không-phải-sprint-bổ-sung), [D112](https://github.com/triet4p/my-meta-brain/blob/main/.agents/memory/decisions.md) and [D113](../.agents/memory/decisions.md), [spec §13.6](META-BRAIN.md#136-approved-migrationcutover-contract--s18-t1--d112), [§14 S18 envelope](META-BRAIN.md#s18-acceptance-envelope--approved-2026-11-18-d112), [§18 D113](META-BRAIN.md#180-only-resource-exception--d113), [requirements](REQUIREMENTS.md#owner-approved-migrationcutover-contract--s18-t1--d112-2026-11-18) and [S18 plan](sprint-plans/sprint-18.md).

## 1. What migration does and does not do

Migration is a **separate, owner-initiated act**. Installing or upgrading the CLI/MCP never migrates, resets, re-encrypts or overwrites the vault. The owner first previews, then explicitly confirms with a typed token, and only then does any write occur.

- `owner migration-preview` (write-free) lists every legacy family with exact bytes, digest, length, zone and destination and an eligibility `reason`. It mints no authority, changes no classification and never offers declassification.
- `owner migration-execute` consumes a fresh preview token plus a typed `MIGRATE` confirmation and a verified encrypted pre-migration backup. Any vault or workspace change makes the token stale and the execute is refused.
- `owner migration-resume` reconciles an interrupted transfer with a fresh token and typed `RESUME`; `owner migration-report` traces legacy id to workspace node id, revision and digest.
- `owner migration-purge-preview` / `owner migration-purge` / `owner migration-purge-resume` extend purge across the workspace node and retained revisions, the legacy snapshot, the encrypted reverse map and rebuildable derivatives. `owner migration-export` returns exact original bytes with labelled `generatedRepresentation` metadata.

### Eligibility (D112, narrow by design)

Only a legacy record whose **exact original bytes** are recoverable from a preserved source snapshot (byte, length and SHA-256 equality), that is **within the unchanged 1 MiB workspace per-act cap**, and whose zone is workspace-independent (`workspace`, zone-independent) is eligible.

These stay in the legacy surface and appear in the unmigrated report:

- structured-body-only items — no original file is fabricated;
- snapshots over 1 MiB (the cap is unchanged; a larger transfer path needs its own approval);
- records carrying a zone the destination cannot enforce (the workspace file-id/revision authorization never consults a zone), and unknown/empty classification.

An annotation plus the encrypted reverse map preserve **provenance only**; they are not enforcement. No protection is relaxed to make a record move, catalog entries/grants/tokens/sessions are not re-pointed, and the legacy history stays immutable. The encrypted `migration-map` becomes a permanent canonical dependency while legacy snapshots remain readable.
## 2. Compatibility and downgrade

- Crypto, key, envelope and storage formats are unchanged in 1.x. CR03/D089 explicitly changed only the support contract (CLI owner-complete + MCP, WPF frozen); it did not change crypto/key/storage or auto-migrate data.
- Rollback/restore is **whole-vault restore only**, from a backup that was verified with the owner key before the act. There is no per-record undo and no in-place downgrade. Restoring into a separate directory re-applies the durable purge ledger; edits made after the backup are excluded and must be disclosed. No promise is made that 1.0/1.1 binaries read a vault written after 1.8.
- Installed 1.1.0 WPF transition evidence is **frozen history** from Sprint 12 (owner CLI/UI component ownership/version mismatch, explicit owner transition or safe refusal/remediation, no silent shared-runtime replacement, no auto-uninstall, no data deletion). 1.8.0 does not replay or revive WPF; it verifies only the changed 1.x install/upgrade/refusal path on synthetic fixtures. See [OPERATIONS §2.3](OPERATIONS.md#23-support-transition-from-the-11-wpf-product-s12-t6-carried-by-140).
- The Microsoft Visual C++ v14 x64 Redistributable `14.43.34808` or later remains a separately owner-installed prerequisite; Meta Brain does not bundle, download or install it. Local E5 model assets remain an explicit external input.
- MCP is stdio/JSON-RPC negotiating `2025-11-25`, `2025-06-18`, `2025-03-26`, `2024-11-05` (unknown/absent offer falls back to `2025-11-25`). Sprint 18 adds no MCP tool; the surface is inherited from 1.7.0. Codex CLI `0.164.0` and OMP `18.20.0` are historical evidence points, **not** a supported version range or exact-package proof.

## 3. Known limits retained for 1.8.0

- **D113 report-only performance:** the D112 1k/10k handler p95 targets and the four 100k A18-2-relative comparisons are report-only for 1.8.0, not PASS gates. The original comparable runner is unavailable and the worker host has competing load; values are recorded with instrument/run gaps, never as an equivalence or benchmark PASS. The **256 MiB service-idle RAM ceiling** and **1 MiB per-act cap** remain hard and binding. No repeat of the old benchmark is required. R37 real files were used only under the separate per-run consent already recorded in S18-T5; this release task does not replay T5, provider/model work, budget or R37.
- Migration eligibility is deliberately narrow (§1). Full C1/C2/C3 corpus counts, the >1 MiB edge, conflicts and tombstones are covered by the earlier task evidence, not re-run here.
- Retained encrypted backups and agent-local/external copies stay outside purge control and are disclosed, not silently deleted. No plaintext HTML/temp, no app-managed browser cache, no owner web console and no WPF replay.
- Assets are unsigned: SHA-256 checks integrity, not publisher identity. Do not disable SmartScreen, Defender or security policy to install.

## 4. Future major removals require a separate G2 approval — no 1.x removals

Nothing is removed in 1.x. [Roadmap §8](ROADMAP-2.0.md#8-g2--final-release-200-không-phải-sprint-bổ-sung) makes G2 a distinct major-release milestone after Sprint 18, gated on Sprint 18 completion plus a **separate owner-approved G2 removal contract**. Candidate removals in G2-T1 (workspace as the default, migration of supported CLI/MCP/document-bridge/settings callers, removal of obsolete active legacy write paths/shims and WPF product/packaging callers) are **inventory only here**. G2-T2 builds a fresh 2.0.0 candidate (never retags 1.8.0), and G2-T3 publishes only after major differential PASS and owner authorization. Historical WPF assets/source/evidence and unmigrated owner data are never deleted to look clean, and the S12 installed-transition evidence stays retained as history. [Spec §13.6 G2 boundary](META-BRAIN.md#136-approved-migrationcutover-contract--s18-t1--d112) and [CR03](REQUIREMENTS.md#approved-change-record--cr03-2026-11-01) are the controlling records; this document does not open G2 or authorize any removal.

## 5. Evidence map — reviewed Sprint 18 changes to the candidate

Each task was accepted by independent evidence review and a Bronze exact-snapshot checkpoint before the next task. HEAD at S18-T6 preparation is `e149a34a30c5646129b1df401780305a7a530ec1` (S18-T5 checkpoint commit).

| Task | Accepted worker evidence | Independent review | Exact checkpoint | Reviewed path / scope |
|---|---|---|---|---|
| T1 contract | `artifacts/sprint-18/task-1.md` | `reviews/S18-T1-C1-R1.md` | `checkpoints/S18-T1-CP1.md` (`af453699`) | Documentation-only: D112 migration/cutover contract, corpus C1/C2/C3, G2 removal inventory |
| T2 preview/transfer | `artifacts/sprint-18/task-2.md` | `reviews/S18-T2-C1-R2.md` | `checkpoints/S18-T2-CP1.md` (`a6bb26ce`) | `RecordMigration`, `RecordMigrationService`, `ServiceRequestHandler`, `NamedPipeService`, `VaultBackupService`, `OwnerControlCli`, `s18-t2` smoke |
| T3 resume/restore | `artifacts/sprint-18/task-3.md` | `reviews/S18-T3-C1-R1.md` | `checkpoints/S18-T3-CP1.md` (`8139526b`) | `owner migration-resume`, restore disclosure, `s18-t3` smoke |
| T4 purge/portability | `artifacts/sprint-18/task-4.md` | `reviews/S18-T4-C1-R1.md` | `checkpoints/S18-T4-CP1.md` (`48371b7`) | `MigrationPurgeService`, four purge/export operations, `s18-t4` smoke |
| T5 daily-use/resource | `artifacts/sprint-18/task-5.md` | `reviews/S18-T5-C1-R2-verified.md` | `checkpoints/S18-T5-CP1.md` (`e149a34a`) | CLI daily-use walkthrough, D113 report-only resource comparison, hard caps, R37 per-run consent |

All five checkpoints and their manifests are under `artifacts/sprint-18/`. The `1.8.0` candidate binds the compiled CLI installer + MCP ZIP to the reviewed HEAD and the assembly-instant working-tree digest in `candidate-manifest.json`; the exact identities, digests and drift classification are recorded in `artifacts/sprint-18/task-6.md`. S12 installed-WPF/shared-runtime transition evidence and the 1.7.0 publication record remain inherited history, not re-derived by this task.
