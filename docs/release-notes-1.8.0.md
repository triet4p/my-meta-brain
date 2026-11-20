# Meta Brain 1.8.0 — release notes

**Status: Sprint 18 local candidate documentation — not published.** 1.7.0 remains the latest published release (`v1.7.0`, 2026-11-18). 1.8.0 is versioned in source (`Directory.Build.props`) and documented here; the exact CLI setup and MCP ZIP candidate was assembled from `e149a34a30c5-tree2ce06fb85f6a` (candidate `2bc84743d74c`), with exact digests and verification in the [1.8.0 release record](release-1.8.0.md) and `artifacts/sprint-18/task-6.md`. No asset is published and no installed-host upgrade was performed.

## Release scope

Two products, as in every release from 1.2.0 under [CR03](REQUIREMENTS.md#approved-change-record--cr03-2026-11-01): a dedicated CLI setup for the full owner workflow and a standalone self-contained MCP ZIP for the scoped agent surface. No WPF Owner UI installer, no separate browser/document product, no new host service or general web console. MCP shares the versioned runtime but carries no owner authority.

Sprint 18 adds a **guarded, opt-in legacy-to-workspace migration** and its lifecycle, without changing the support contract or backgrounding any migration:

- `owner migration-preview` is write-free and lists each legacy record with exact bytes, digest, zone, destination, eligibility and reason. Only exact-byte originals within the unchanged 1 MiB per-act cap and without an unenforceable zone are eligible; structured-only items, oversized snapshots and zoned records stay legacy and are reported. No declassification is offered and no permission is widened.
- `owner migration-execute` requires a verified encrypted pre-migration backup, a fresh preview token and a typed `MIGRATE` confirmation. The encrypted reverse map records provenance; legacy history stays immutable and there is no dual write. `owner migration-report` traces legacy id to workspace node, revision and digest.
- `owner migration-resume` reconciles an interrupted transfer with a fresh token and typed `RESUME`; `owner migration-purge-preview`, `owner migration-purge`, `owner migration-purge-resume` and `owner migration-export` complete retention/purge and exact-byte portability across representations.
- The owner daily workflow was accepted through the CLI in S18-T5; the optional foreground document preview/edit surface is unchanged. No WPF replay was required.

## MCP protocol and client compatibility

MCP uses stdio/JSON-RPC and negotiates `2025-11-25`, `2025-06-18`, `2025-03-26` or `2024-11-05`; an unknown or absent offer falls back to `2025-11-25`. Sprint 18 adds no MCP tool, so the MCP surface is inherited from the published 1.7.0 package. Codex CLI `0.164.0` and OMP `18.20.0` are historical T5 evidence points, not a supported client/SDK version range or exact-package normal-client proof of 1.8.0.

## Installation, migration and recovery

Installing or upgrading never migrates, resets, re-encrypts or overwrites the vault. Migration is a separate owner act described in [the 1.8.0 migration guide](migration-1.8.0.md). Rollback is whole-vault restore only from a verified encrypted backup into a separate directory; there is no per-record undo or in-place downgrade, and no promise that 1.0/1.1 binaries read a vault written after 1.8. Post-backup edits are excluded and disclosed. The Microsoft Visual C++ v14 x64 Redistributable `14.43.34808` or later is owner-installed separately; Meta Brain does not bundle, download or install it.

The frozen 1.1.0 WPF installed-component transition evidence is retained from Sprint 12; 1.8.0 does not replay or revive WPF and does not change the CLI/MCP/data formats.

## Known limits

The D112 1k/10k handler p95 targets and four 100k A18-2-relative comparisons are **report-only for 1.8.0** under [D113](../.agents/memory/decisions.md); the 256 MiB service-idle RAM ceiling and 1 MiB per-act cap remain hard. No benchmark PASS or equivalence is claimed, and no T5 budget/provider/R37 replay is performed here. Assets are unsigned: SHA-256 checks integrity, not publisher identity. See [the migration guide](migration-1.8.0.md) for the complete compatibility, known-limit, G2-removal and evidence-map record.

## Future major removals

No feature is removed in 1.x. Any default/API clean cutover or removal of obsolete legacy write paths and WPF packaging callers belongs to a separately owner-approved G2 removal contract (roadmap §8), not to 1.8.0. This note does not open G2.
