# Meta Brain 2.0.0 — migration, compatibility and known limits

**Status:** published as [`v2.0.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v2.0.0) on 2026-11-22 (release id `454321658`). 1.8.0 is a prior published release. This guide covers what changes in 2.0.0, what deliberately does not, how install/upgrade/uninstall and recovery behave, the D114 retention/removal boundary, the D115 user-PATH opt-in, and the evidence map. It is not a publication record or a task PASS.

Normative sources: [roadmap §7/§8](ROADMAP-2.0.md#7-definition-of-release-mỗi-sprint), [D114](REQUIREMENTS.md#owner-approved-g2-removal-scope--d114-2026-11-20), [D115](REQUIREMENTS.md#owner-approved-cli-user-path-opt-in--d115-2026-11-20), [D116](REQUIREMENTS.md#owner-approved-cli-user-path-opt-in--d115-2026-11-20), [D117](REQUIREMENTS.md#owner-approved-codex-mcp-evidence-adjustment--d117-2026-11-22), [release contract](release-contract.md), [1.8.0 migration guide](migration-1.8.0.md) (still authoritative for the S18 opt-in migration itself), [2.0.0 candidate record](release-2.0.0.md).

## 1. What 2.0.0 changes — and what it does not

2.0.0 is the approved **default/API clean cutover**, not a data cutover:

- The workspace becomes the default content surface of the packaged `metabrain` command. `<install root>\cli\metabrain.exe` is a byte-verified copy of the CLI apphost; the running file name selects the workspace-first front door. `read` → `workspace-read`, `search` → `workspace-search`, `open` → the existing workspace document session; every other retained command keeps its name and is accepted with or without the `owner` prefix. `MetaBrain.Cli.exe` keeps its exact historical dispatch.
- The CLI setup offers an explicit `--add-to-path` opt-in for command discovery (D115), described in §4.
- No active redundant WPF packaging callers existed, so D114’s removal authorization removed none. The frozen 1.1.0 WPF product is not revived, rebuilt, bundled or installed; the installer still refuses it or an unknown/mixed installation in place.
- **Nothing is migrated automatically.** Installing, upgrading or opening the new default does not migrate, reclassify, rewrite, dual-write or delete a record. The S18 opt-in migration remains exactly as documented in the [1.8.0 migration guide](migration-1.8.0.md): separate preview, explicit owner confirmation, verified backup, narrow eligibility, resume/purge/export and whole-vault restore.
- Vault, workspace, crypto, key, envelope, settings-schema and distribution formats are unchanged from 1.8.0. Legacy memory/source/relation workflows, structured export and the separate plaintext-settings `owner migrate` remain reachable (D114).
- No provider, network, model, semantic, browser, WPF or publication behavior is added by the cutover.

## 2. Compatibility and transition

| Existing installation | 2.0.0 behavior |
|---|---|
| 1.2.0–1.8.0 CLI/MCP | Upgraded in place. The installer drains/stops the shared host, replaces application files, keeps the recorded data root, settings, both endpoints, vault, workspace, sources, backups and recovery material, and records version `2.0.0` and the new candidate identity. Verified on the exact 1.8.0 → 2.0.0 candidate path: settings byte-identical, endpoints identical, one host after upgrade, restart locked, exact workspace bytes after owner unlock. |
| 1.0.0 or any pre-1.2 installed layout | **Refused before any host stop or file/data change.** The message names the supported transition: install 2.0.0 into a separate install root, with its own data root and distinct `--control-pipe`/`--agent-pipe`, and leave the old installation and its data untouched. The real released 1.0.0 bundle layout, a representative installed 1.0.0 state and a representative installed 1.1.0 state were all refused with zero file-tree change and no data-root creation. |
| 1.1.0 WPF / `owner-ui` | Refused as the frozen legacy product. `--component owner-ui` is refused; no WPF build, installer, download or compatibility promise is created. |
| Unknown/mixed or conflicting shared runtime | Refused before changes, with remediation; one live data root is never shared between two installations. |
| Existing MCP-only or CLI-only component registration | The matching complete candidate updates the shared runtime; a different candidate/version registered for another frontend is refused instead of silently replacing the runtime. |

Data rules: install never creates or unlocks a vault; the first start is locked. Uninstall removes application files for the selected component and always preserves the data root; a reinstall/reselect opens the same data with the owner’s key. No global ACL, owner, service, autostart, registry, profile or permission change is made by the product.

## 3. D114 retention and removal scope

Authorized by D114: make the workspace the CLI default, migrate supported CLI/MCP/document-bridge/settings callers, add the packaged `metabrain` entrypoint, and remove **only** redundant active WPF packaging callers if present. The inventory found no active redundant WPF packaging callers, so no source or packaging caller was removed. No WPF replay gate is revived.

Retained and verified or inherited: legacy `owner migrate` plaintext-settings migration, fail-closed protocol mismatch, backup/restore, data-preserving upgrade/uninstall, installed WPF detection/refusal/remediation, memory/source/relation workflows, structured export, installed component layouts, data and historical source/assets/evidence. No declassification, no migration, no permission widening and no deletion of unmigrated data is bundled with the default change.

## 4. D115 user PATH opt-in

The CLI setup accepts `--add-to-path` (or answers yes at the interactive prompt). Semantics:

- Appends `<install root>\cli` **once** to the current user’s PATH value and broadcasts the change. Existing entries and their order are preserved.
- Writes no PowerShell profile, system PATH, WindowsApps, service or other registry value.
- An entry the owner already had is neither claimed nor later removed.
- Upgrade keeps an entry this installation owns; CLI uninstall removes only the entry this installation added.
- A **new** PowerShell session sees the change; an existing session keeps its inherited environment.
- Without the option, no environment value is touched and setup prints the manual alternative.

The agent verified the ownership logic in-process with a fixture PATH store only; the worker may not mutate real PATH/registry/profile. The persistent user-PATH evidence is owner-run. Under D116, the owner-run clean-install add/keep/uninstall lifecycle and pre-existing-entry preservation are accepted together with owner-confirmed fresh-PowerShell command discovery after the opted-in 1.8.0 upgrade; a distinct fresh-shell observation after clean install was not required for this candidate and was not observed. The procedural walkthrough remains at [owner walkthrough](../artifacts/g2/owner-walkthrough-2.0.0.md).

## 5. Recovery, rollback and downgrade

- The supported rollback remains a **whole-vault restore** of a verified encrypted backup with its key into a separate new directory, then switching the data root. There is no per-record undo and no in-place downgrade.
- Restore re-applies the durable purge ledger, resurrects no live grant/session/redeemable token, and excludes/discloses writes, exports and revisions made after the backup.
- A 1.0/1.1 binary is not promised to read a vault written by a later release. Restore of a compatible backup is the downgrade path.
- Exact 2.0.0 candidate checks: backup written; wrong passphrase → `restore_denied`, no target created; correct passphrase → restore to a separate directory with the disclosure; upgraded 1.8 vault restarted locked and read exact pre-upgrade bytes after owner unlock only.

## 6. Known limits and performance boundary

- **D113 was a 1.8.0-only resource exception.** It does not automatically carry into 2.0.0, and this task does not invent, lower or re-adopt a quantitative gate. The D112/S18 1k/10k p95 targets and 100k A18-2-relative comparisons remain S18-scoped history; the approved baseline R38 measurement obligation is not replaced by a new claim. No benchmark was re-run for this candidate.
- Migration eligibility remains the narrow D112 contract; no real owner corpus was migrated here and no real owner files (R37) were read.
- Assets are unsigned; SHA-256 does not authenticate the publisher. The Microsoft Visual C++ v14 x64 Redistributable `14.43.34808+` remains a separate owner-installed prerequisite.
- Local model assets, semantic queries, index rebuilds and browser rendering are outside this candidate’s evidence, and no provider/model run beyond the bounded read-only catalog calls in §7 was made. A local stdio initialization/tools-list probe is direct MCP wire proof; the bounded normal-client executions in §7 are not a compatibility guarantee or client-range claim.
- The assembled manifest retains two stale documentation strings (`supportTransition` 1.2-era text; `testedOhMyPiVersion 18.12.6`); they are disclosed in the candidate record and do not change behavior.

## 7. Owner-run proofs and walkthrough

Owner-only (cannot be performed by the worker without forbidden host mutation):

- opted-in install or upgrade with `--add-to-path`;
- a **fresh `pwsh`** started after the change, from outside the install directory, running `metabrain --help`/`--version`;
- uninstall, then confirming the install’s own PATH entry was removed and any pre-existing entry was not;
- optionally an installed-host upgrade on the owner’s own installation (not required by the walkthrough’s synthetic roots).

The executable walkthrough with exact candidate/baseline identities, commands, expected results and rollback is `artifacts/g2/owner-walkthrough-2.0.0.md`.

Actual normal-client evidence is bounded and recorded under D117; no further provider call is authorized. Observed on this candidate: direct MCP stdio (`2025-11-25`, `serverInfo 2.0.0`, 26 tools), the `s4-t1` transport/session bridge, a no-model Codex CLI `0.164.0` `mcp list` configuration acceptance under a fresh isolated `CODEX_HOME`, an OMP `19.0.0` isolated-workspace session that executed the read-only `metabrain_catalog_list` tool (`CATALOG no_match`), and an owner-operated interactive Codex session that showed `metabrain: connected (1 tool)` and called `metabrain.metabrain_catalog_list` with the same result ([owner demo](../artifacts/g2/supervision/G2-T2-owner-codex-demo.md)). Two authorized non-interactive `codex exec` runs initialized the packaged server but recorded no tool call; the second reported the tool unavailable. D117 accepts the OMP call, Codex server initialization and direct stdio verification in place of a successful Codex tool call. Do not claim a Codex `exec` PASS, a full compatibility guarantee or a supported version range.

## 8. Evidence map

| Area | Changed in 2.0? | Evidence |
|---|---|---|
| Exact 2.0.0 candidate, assets, payload, inputs, licenses, integrity | yes (new version) | [2.0.0 candidate record](release-2.0.0.md); `runtime/release-candidates/2.0.0-win-x64-a5e13717a31e-tree7eb452354d39-bc8ebc909aa8/candidate-manifest.json` (ignored); logs `runtime/g2-t2-verify/` (ignored) |
| Workspace-first `metabrain`, preserved legacy CLI, byte identity | yes | [2.0.0 candidate record](release-2.0.0.md#verification-actually-observed); T1 `artifacts/g2/task-1.md`, `artifacts/g2/reviews/G2-T1-C1-R1.md`, checkpoint `artifacts/g2/checkpoints/G2-T1-CP1.md` (`a5e13717`) |
| D115 PATH ownership | yes (code); owner evidence accepted under D116 | T1 `tests/MetaBrain.S1T4.Smoke/G2T1InstallPathSmoke.cs` (fixture store); D116 combined owner-run lifecycle; owner walkthrough |
| D114 retention/removals | yes (scope; no callers removed) | `artifacts/g2/task-1.md` §changed files; exact-candidate `s12-t5-legacy` + `s18-t2` exit 0; this guide §3 |
| Install/upgrade/refusal/uninstall | yes (changed defaults; version) | [2.0.0 candidate record](release-2.0.0.md#verification-actually-observed); logs `refusal-1.0.log`, `refusal-1.1-state.log`, `upgrade-1.8-to-2.0.log`, `uninstall-2.0.log` |
| Recovery/backup/restore | unchanged; exact-candidate re-check | `backup.out`, `restore-wrong.out`, `restore-ok.out`; S18-T3/T4 accepted evidence |
| MCP wire surface | unchanged; new version | `mcp_probe.py` output: `2025-11-25`, serverInfo `2.0.0`, 26 tools; member hashes in the manifest |
| Normal-client MCP execution | bounded read-only catalog call; no new tool | `artifacts/g2/task-2.md` §A2; `artifacts/g2/supervision/G2-T2-owner-codex-demo.md`; D117; ignored `runtime/g2-t2-a2-20261122-135933/logs/` |
| Opt-in migration contract | unchanged | [1.8.0 migration guide](migration-1.8.0.md); S18-T2/T3/T4 evidence; exact-candidate `s18-t2` exit 0 |
| Unchanged 1.x surfaces | no | Inherited accepted evidence mapped in the [candidate record](release-2.0.0.md#feature-and-requirement-mapping) |

Historical 1.0/1.1/1.8 assets, bytes, acceptance and evidence are preserved unchanged.
