# Meta Brain 1.7.0 — release notes

**Status: published as `v1.7.0` on 2026-11-18; public release-boundary re-review PASS.** Candidate `20af9d477aba` is the exact reviewed and checkpointed (`fd8ad2f…`) release published as [GitHub Release v1.7.0](https://github.com/triet4p/my-meta-brain/releases/tag/v1.7.0) (id `449509004`) with byte-exact [CLI](https://github.com/triet4p/my-meta-brain/releases/download/v1.7.0/MetaBrain-CLI-1.7.0.exe) and [MCP](https://github.com/triet4p/my-meta-brain/releases/download/v1.7.0/MetaBrain-MCP-1.7.0.zip) assets; 1.7.0 is the latest published CLI + MCP release. The [public site](https://triet4p.github.io/my-meta-brain/) was originally published at `gh-pages ebf5f9f…` and now serves the corrected status text deployed at `gh-pages 05c4b0cff402d5a0c6709d13f2e37f53d75d8d77`. `S17-PUBLIC-C1` R1 was FAIL on stale live text (historical); R2 returned **PASS** with no actionable findings. Sprint 17 Complete is Main-owned and not claimed here. Exact identity, hashes, provenance and verification remain in [the 1.7.0 release record](release-1.7.0.md).

## Release scope

Two products: a dedicated CLI setup for the full owner workflow and a standalone self-contained MCP ZIP for the scoped agent surface. No WPF Owner UI installer, separate document/browser product, new host service or general web console. The CLI and MCP share one versioned runtime; MCP carries no owner authority.

Sprint 17 adds grant-separated workspace sharing and context tools:

- `metabrain_workspace_folder_request` submits one stable folder ID and purpose and returns a non-authoritative receipt; `metabrain_workspace_request_status` reports only receipt status. Neither grants file access, and owner receipt review is not D108 approval.
- The owner separately previews and approves an exact D108 folder snapshot for file reads; agent redemption uses a protected one-time handoff.
- `metabrain_workspace_agent_recall` requires a separate D111 grant selecting cognitive annotation, hybrid search and/or relation recall. Pins and currentness are rechecked; hybrid semantic queries still require their own local-inference consent.
- `metabrain_workspace_context_pack` requires a separate D110 grant and returns only owner-selected, pinned, budget-fitting verbatim entries and in-scope accepted/current relation paths. It does not summarize, call a provider, or imply source-read or proposal rights.
- Source reads, proposals, owner actions and provider egress remain independently authorized. Revoke, expiry or vault lock denies subsequent calls; content already received by a client/provider cannot be recalled.

On the exact 1.7.0 package these paths were exercised with synthetic fixtures via the packaged CLI/service/MCP (`s17-t3`, `s17-t4`) and real MCP stdio calls. They are direct package checks, not normal-client or provider proofs.

## MCP protocol and client compatibility

MCP uses stdio/JSON-RPC and negotiates one of `2025-11-25`, `2025-06-18`, `2025-03-26`, `2024-11-05`; an unknown or absent offer falls back to `2025-11-25`. The exact 1.7.0 package answered `initialize` with `serverInfo` version `1.7.0` for both `2025-11-25` and `2025-06-18`, advertised 26 tools and served `metabrain_catalog_list`. Sprint 17 T5 normal-client evidence observed Codex CLI `0.164.0` and OMP `18.20.0` against reviewed T5 source; those are evidence points, not a supported client/SDK version range and not exact-package 1.7.0 normal-client evidence. No client SDK is bundled. The frozen manifest's `testedOhMyPiVersion` field still reads `18.12.6`; this stale documentation value is disclosed in the candidate record.

## Installation, compatibility and recovery

Install the CLI setup and separately extract the MCP ZIP. The owner-installed Microsoft Visual C++ v14 x64 Redistributable `14.43.34808` or later is a separate prerequisite; Meta Brain does not download, bundle or install it. The 1.6.0 → 1.7.0 in-place upgrade was exercised on fresh sibling install/data roots with synthetic data: the install root, data root, settings and endpoints were retained, only the ownership marker changed, the host restarted locked, a wrong passphrase was refused, and the original passphrase and recovery key both unlocked the preserved vault. Exact workspace bytes, search, folder identity and a canonical memory item survived byte-identically; a second install sharing the data root was refused before writes. Pre-1.2, unknown/mixed, conflicting candidate or shared-runtime installations refuse before changing files. Installer static review found no PATH/profile/autostart/service/registry/permission changes; the only registry access is a read-only VC++ version check.

No 1.7.0 vault migration, format/key/crypto change, reset or data overwrite is intended or observed. Before a downgrade, stop and lock the service and make and verify an encrypted owner backup. Older releases must not be assumed to understand newer grant records; never reset or overwrite the vault as a rollback shortcut.

## Integrity, provenance, license and signing

Both assets are unsigned. SHA-256 detects byte changes but does not authenticate the publisher. The project license is MIT, `Copyright (c) 2026 triet4p`; the candidate manifest binds the candidate ID, source HEAD and assembly-instant working-tree digest, product version, exact asset names, sizes and hashes, the embedded CLI payload and its member hashes, and the dependency/license/notice/signing inventory. Do not disable SmartScreen, Defender or security policy to install.

## Verification limits

Exact candidate integrity, CLI setup version/payload verification, packaged MCP protocol/tool checks, packaged CLI/service sharing and context-pack workflows, and the isolated 1.6.0 → 1.7.0 upgrade/recovery are verified on synthetic fixtures. Independent evidence review `S17-T6-C1` R1 PASS, the exact-snapshot checkpoint at `fd8ad2f…` and the final differential PASS are recorded as inherited gates; the release was published as [v1.7.0](https://github.com/triet4p/my-meta-brain/releases/tag/v1.7.0) on 2026-11-18 with byte-exact public assets and a Pages site. Candidate-level normal-client proof is absent and requires fresh T6-scoped owner consent. Public release-boundary re-review `S17-PUBLIC-C1` R1 was FAIL on stale live text (historical); R2 returned **PASS** with no actionable findings against the corrected site deployed at `gh-pages 05c4b0cf…`. Sprint 17 Complete is Main-owned and not claimed here. Publication records: `artifacts/sprint-17/publication/S17-PUBLISH-P1.md`, `artifacts/sprint-17/reviews/S17-PUBLIC-C1-R1.md`, `artifacts/sprint-17/reviews/S17-PUBLIC-C1-R2.md`, `artifacts/sprint-17/publication/S17-PUBLIC-FIX-DEPLOY-C1.md`.
