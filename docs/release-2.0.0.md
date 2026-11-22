# Meta Brain 2.0.0 — exact local candidate and release-delta record

**Status: exact local 2.0.0 candidate assembled and verified; owner accepted for independent review; independent evidence review and checkpoint complete; major differential returned FAIL to advance; NOT published.** The latest published release is **1.8.0** ([`v1.8.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.8.0), 2026-11-20, release id `452370096`, Latest; CLI `f977aada…` 234,638,554 B and MCP `53080c42…` 90,183,118 B). This record binds the new 2.0.0 bytes to source `a5e13717a31e-tree7eb452354d39` (candidate `bc8ebc909aa8`) and records the release-delta work. Independent evidence review `G2-T2-C1` round 1 passed against the frozen candidate, and checkpoint `G2-T2-CP1` recorded the reviewed docs at `54d6cd27aadf674952bd92647f959b829496b469`. Major differential `G2-DEEP-D1` then returned **FAIL to advance** because that checkpointed tree omits the Main-owned records these docs cite and has no measured tag binding, and because this record's gate status was stale. The status correction below is under a new review; a separate documentation-integration review and checkpoint, a post-checkpoint committed-blob measurement and a fresh major differential remain pending. No tag, upload, deployment, site publication or worker-run installed-host mutation was performed.

Normative sources: [roadmap §7/§8](ROADMAP-2.0.md#7-definition-of-release-mỗi-sprint), [G2-T2 criterion](ROADMAP-2.0.md#8-g2--final-release-200-không-phải-sprint-bổ-sung), [D114](REQUIREMENTS.md#owner-approved-g2-removal-scope--d114-2026-11-20), [D115](REQUIREMENTS.md#owner-approved-cli-user-path-opt-in--d115-2026-11-20), [D116](REQUIREMENTS.md#owner-approved-cli-user-path-opt-in--d115-2026-11-20), [D117](REQUIREMENTS.md#owner-approved-codex-mcp-evidence-adjustment--d117-2026-11-22), [release contract](release-contract.md), [1.8.0 candidate record](release-1.8.0.md), and the T1 evidence/checkpoint `artifacts/g2/task-1.md` / `artifacts/g2/checkpoints/G2-T1-CP1.md` (`a5e13717`).

## Candidate identity

| Field | Value |
|---|---|
| Product / version / RID | Meta Brain `2.0.0`, `win-x64`, self-contained |
| Source HEAD | `a5e13717a31ee2b817f1e2c0daa29f395a80f64a` (G2-T1 checkpoint commit) |
| Candidate directory | `runtime/release-candidates/2.0.0-win-x64-a5e13717a31e-tree7eb452354d39-bc8ebc909aa8` |
| Candidate ID / source identity | `bc8ebc909aa8` / `a5e13717a31e-tree7eb452354d39` |
| Assembly-instant tree digest | `7eb452354d391c5673465f15d7ec3be2d579bea604d407e4bee89441563c4fcd` |
| Assembler | `scripts/release/assemble-candidate.ps1`, unchanged guarded invocation |
| Assembled at (UTC) | `2026-11-20T17:14:08.4320774Z` |
| Signing | unsigned; SHA-256 detects byte changes but does not authenticate the publisher |
| Assets | exactly two: the dedicated CLI setup and the standalone MCP ZIP; no WPF/UI installer |

`source.workingTreeIncluded=true`: the digest covers the tracked/non-ignored population enumerated by `git ls-files --cached --others --exclude-standard` minus the assembler's documented exclusions. Documentation edits made after assembly (this record and the delta guides) are not compiled into either asset and change only the live tree digest, as disclosed below.

## Exact assets

| Product | Candidate filename | Bytes | SHA-256 |
|---|---|---|---|
| CLI setup | `MetaBrain-CLI-2.0.0-win-x64-a5e13717a31e-tree7eb452354d39-bc8ebc909aa8.exe` | 234,654,938 | `e08c03e6d9db8833b2794f179d6e12550dcdffe3b9894f8c7d61315dc1bff772` |
| MCP package | `MetaBrain-MCP-2.0.0-win-x64-a5e13717a31e-tree7eb452354d39-bc8ebc909aa8.zip` | 90,184,857 | `cc733b1d8e3fa8ac3fd1b3c56a6d0a93a09a7b2643e91d7218434112e8a76b98` |
| Candidate manifest | `candidate-manifest.json` | 439,258 | `50d64d6c9e6595a59c0a94c5c4efaf2ccaf1a37f1bb16443856c9e4016a9beee` |

- Embedded CLI setup payload: **135,283,760 bytes / SHA-256 `0f4a4f472510e243f86087c7f71e27c7d27e3612ceaf68eb0f82afa59d835f40`**, 626 members, package product `Meta Brain CLI 2.0.0`, 625 manifest file entries.
- MCP ZIP: **421 members**, including `mcp/MetaBrain.Mcp.exe` (`84d81818fe41b020767d7e02ff637603178ea3feec40f79713f2fe76c1fc1099`, 162,304 B), `service/MetaBrain.Connections.exe` (`7379d5859917bd4f7a8a8c53e18cc7160d3bdc42ca9015f1d3ed4cae81abee99`, 162,304 B), `package-manifest.json`, project `LICENSE`, `RELEASE-NOTES.txt`, `THIRD-PARTY-NOTICES.txt` and per-dependency license/notice texts.
- No 1.8.0 (or other historical) bytes were retagged, relabelled or substituted; the 2.0.0 assets were built from the reviewed 2.0.0 source.

## Pinned inputs and provenance

Every pinned input in the table below was present and matched the assembler's constants before assembly; no guard was changed, bypassed or relaxed.

| Input | Bytes | SHA-256 |
|---|---|---|
| `runtime/s15-t6-package-license/system-numerics-tensors-10.0.14/dotnet-vmr-LICENSE.TXT` | 1,115 | `ae48df11a335dc1a615f4f938b69cba73bcf4485c4f97af49b38efb0f216353b` |
| `…/dotnet-vmr-THIRD-PARTY-NOTICES.txt` | 265,586 | `5a90be9000b5c2f928dead74be4e86952420d1160fa38f6f2d34526eeee49ff6` |
| `…/System.Numerics.Tensors-README.md` | 122 | `2ab3b3eb4a6e7e398c631d7187a3f9b99560405387dd0cee9013dc1f202b935e` |
| `…/licenses.nuget.org-MIT.html` | 2,442 | `0ff20ddac3d7ee3c94cd6b60bef6537b4c4f6815063ab28228ab8ca563b1fe8e` |
| `…/spdx-MIT.html` | 13,452 | `b7eaaa2c58e7b36f76e1562cb4070a1e15ad52ee0a7386c724039bb929d3b078` |
| `…/System.Numerics.Tensors-10.0.14-COPYRIGHT.txt` | 46 | `30bedab2f6c25a864a0c7ceac40f5c4ce1816684db188e87644e72acf6a2b8c3` |
| `runtime/s15-t6-native-build/tokenizers-dotnet-b8eaa77d-source.zip` | 1,236,124 | `2ee9c2d81aba55396ca67544765bd5cf2438ff35b5a959fe820915e449f77ded` |
| `runtime/s15-t6-native-build/target/x86_64-pc-windows-msvc/release/hf_tokenizers.dll` | 3,198,464 | `4595800ae02f9c7c96d583347e6cf1e2ddd17c26348ac8169c8314ca28b7480d` |
| `src/MetaBrain.Connections/native/provenance/Cargo.lock` | — | `7597e8861f7368d02ea5daded9b71cbc226286ffbf1bbe7ad57f3d719b53d48f` |
| `src/MetaBrain.Connections/native/THIRD-PARTY-NOTICES.txt` | 219,767 | `1d67c3d687bc74fcd4226ce4288896938a903542c16b02faaf8ae866d1f6f94e` |

The dependency/license inventory, native tokenizer source commit `b8eaa77d72bf9d9c768d75599bfa6a602e467b93`, external Microsoft Visual C++ v14 x64 prerequisite (`14.43.34808`, owner-installed separately, never bundled/downloaded) and the unsigned status are recorded in `candidate-manifest.json` and inside both packages.

## Verification actually observed

All runs used the exact candidate bytes or binaries installed/extracted from them under ignored repository-local roots. The A1 verification below touched no owner vault, real PATH/registry/profile/service/ACL, provider or network, and no historical asset; the later bounded client runs recorded under “Actual normal-client tool execution” made provider calls only for the read-only catalog tool. A1 logs are under ignored `runtime/g2-t2-verify/`.

- **Assembly and integrity.** One unchanged assembler run completed offline restores, four Release publishes, the 817-file stage and printed `VERIFIED` / `CANDIDATE`. Its guarded `-VerifyOnly` returned `VERIFIED` against the same directory. Both asset sizes and SHA-256 values were independently recomputed and equal the manifest.
- **CLI setup.** `--version` → `MetaBrain CLI Setup 2.0.0`; `--verify-payload` → 135,283,760 bytes / `0f4a4f47…`, 626 files, package `Meta Brain CLI 2.0.0`, exit 0.
- **Installed CLI component.** Fresh sibling roots, CLI-only install (no PATH opt-in): `<install>/cli/metabrain.exe` was byte-identical to `<install>/cli/MetaBrain.Cli.exe` (`c36e5f0a984efbc7d8cbcaf9ec319ad2a6c6471ccb74c8aff81aed1408834939`); `metabrain --version` → `metabrain 2.0.0`; `MetaBrain.Cli.exe --version` → `MetaBrain.Cli 2.0.0`; install state recorded product `2.0.0`, candidate identity `a5e13717a31e-tree7eb452354d39-bc8ebc909aa8`, components `["cli"]`, `commandPath=null`, `commandPathOwned=false`.
- **Workspace-first default (changed default).** On an absent pipe, `metabrain read` and `metabrain search` both returned exit 4 `transport_unavailable`, identical to the explicit `workspace-read`/`workspace-search`; the legacy `MetaBrain.Cli.exe read` stayed exit 2 `unknown_command`. The installed command then started the shared host locked, provisioned a synthetic vault, created a folder, imported exact bytes, and `metabrain read` returned the exact content while `metabrain search` found the same file/revision with the same SHA-256. Two concurrent starts produced one `service=running` host with the same PID; later starts attached to it rather than replacing it. The unchanged `MetaBrain.Cli.exe owner workspace-status` reachable command also worked.
- **Retained D114 legacy surface.** On the exact candidate service: `s12-t5-legacy` exited 0 with `PASS old/new protocol boundaries refuse both directions before any private operation` and `PASS the released 1.1 format-1 backup restores records and truthfully no workspace`; `s18-t2` exited 0 (guarded write-free preview, typed execute, refusal paths, reverse map, no automatic cutover). No automatic migration, no reclassification and no dual write was introduced.
- **Recovery on exact candidate.** `owner backup` wrote a 9,431-byte encrypted backup (`74ffa31f…`); a wrong passphrase restore returned exit 3 `restore_denied` and created no target directory; the correct passphrase restored to a separate new directory with the whole-vault disclosure and zero live grants/tokens. Restore remains whole-vault only; there is no per-record undo or in-place downgrade promise.
- **1.8.0 → 2.0.0 in-place upgrade.** The published 1.8.0 CLI setup installed into fresh sibling roots and produced a synthetic vault/workspace file (`8beb6011…`). The 2.0.0 setup upgraded that installation in place, exit 0: `service-settings.json` stayed byte-identical (`307e1405…`, same size/mtime), both pipe endpoints stayed identical, only the ownership state version/candidate changed, the live 1.8 host was drained/stopped (two MetaBrain hosts before, one after) and the data root was kept. After the upgrade the host restarted locked; `vault_locked` refused a read until unlock; the owner passphrase unlocked it; `metabrain read` returned the exact pre-upgrade bytes (`8beb6011…`) and the workspace reported `format=1 schema=1`.
- **1.0/pre-1.2 refusal and safe remediation.** Copying the real released 1.0.0 bundle as an install root and installing the 2.0.0 candidate exited 1 with `unrecognized or unsupported Meta Brain installation … no files were changed`; no data root was created and the 1.0 tree digest was unchanged. With a representative installed 1.0.0 state, the 2.0.0 setup exited 1 with `holds a 1.0.0 installation. This build refuses to upgrade a 1.1 WPF installation or an unknown/mixed candidate in place; it stopped nothing and changed no files.` plus the separate-root remediation; with a representative installed 1.1.0 state it produced the same explicit WPF refusal naming 1.1.0. In both cases no data root was created and the install tree digest was unchanged. `--component owner-ui` was refused as the frozen 1.1.0 WPF product with no files or data root created.
- **Uninstall / data preservation.** Uninstalling the upgraded 2.0.0 CLI component exited 0 (`service=stopped`, `data root=preserved`); the application root was removed. A second 2.0.0 install → uninstall cycle over the same data root gave `service=not running`, `data root=preserved` and app-root removal again. A third clean install → uninstall cycle captured per-file SHA-256 maps of all 10 stable (non-runtime/log) data files before and after; the diff was empty, so `service-settings.json`, `data-root-owner.json`, the vault header/format/manifest/audit/grant files and the workspace head/state/blob were byte-identical across the cycle. No vault, settings, source, backup or recovery file was deleted. No PATH entry was added or removed because this synthetic install never opted in.
- **Actual client configuration acceptance (no model).** On `codex-cli 0.164.0`, a fresh isolated `CODEX_HOME` and a `-c` override produced exit 0 from `codex -c 'mcp_servers.metabrain.command="…"' -c 'mcp_servers.metabrain.args=["--data-root","…"]' mcp list`, listing `metabrain` with the exact candidate `mcp/MetaBrain.Mcp.exe` path and demo data root (`status enabled`, `Auth Unsupported`). This is actual-client configuration acceptance, not a tool-invocation session and not a version-range claim. OMP reads `.omp/mcp.json` and exposes no supported no-model MCP command.
- **Actual normal-client tool execution (bounded, owner-authorized).** Under the authorization recorded by D117: OMP `19.0.0`, in an isolated workspace configured with the extracted candidate server, executed the read-only `metabrain_catalog_list` tool (`tool_execution_end`, inner `serverName=metabrain`, `isError=false`, text `CATALOG no_match`). The owner-operated interactive Codex session under `runtime/codex-owner-demo-bc8ebc909aa8` reported `metabrain: connected (1 tool)` and `Called metabrain.metabrain_catalog_list` with `CATALOG no_match` (`artifacts/g2/supervision/G2-T2-owner-codex-demo.md`). Two explicitly authorized non-interactive `codex exec` runs initialized the packaged server but recorded no `mcp_tool_call`; the second reported the tool unavailable. D117 accepts the OMP call, Codex server initialization and direct stdio verification in place of a successful Codex tool call. These are bounded evidence points for this candidate — not a `codex exec` PASS, a full compatibility claim or a supported version range.
- **Packaged MCP (unchanged wire surface).** Extracted from the exact ZIP: `mcp/MetaBrain.Mcp.exe --version` → `MetaBrain.Mcp 2.0.0`; both key member hashes matched the manifest. A real stdio JSON-RPC session against the extracted `MetaBrain.Mcp.exe` negotiated protocol `2025-11-25`, returned `serverInfo {"name":"metabrain-agent","version":"2.0.0"}`, and `tools/list` returned the unchanged 26 tools. The exact candidate shared host also passed the `s4-t1` MCP transport session bridge: protocol negotiation/fallback, capability surface without bearer material, catalog/request/redeem/read/authorize/inspect/revoke/lock over one stdio bridge, fail-closed replay/unknown-tool/bearer/malformed/method negatives, and fixture cleanup (exit 0). No model/provider call was made.

Not executed or not claimed here: publication/tag/upload/deploy/site build; browser/WPF replay; semantic queries, index rebuilds, benchmark re-measurement and D113/S18 quantitative gates; a fresh-`pwsh` `metabrain` discovery observation after clean install (D116 records that this specific observation was not made); a successful non-interactive Codex `exec` tool call and any full Codex compatibility or client-range claim. Persistent user-PATH behavior is evidenced only by the owner-run lifecycle accepted in D116; the worker ran no persistent PATH/registry/profile change. Provider/model execution occurred only in the bounded read-only catalog calls recorded above.

## Candidate findings disclosed (not silently edited)

- `candidate-manifest.json` `compatibility.supportTransition` still carries 1.2-era wording (“1.2.0 installs…”); it is assembled documentation text, not behavior. The installer itself implements the 2.0.0 pre-1.2 refusal/remediation proven above.
- `compatibility.mcp.testedOhMyPiVersion` is `18.12.6`, while the historical Sprint 17 T5 observation is OMP `18.20.0` and the bounded execution on this candidate used OMP `19.0.0`. This is inherited documentation-only staleness in the frozen manifest; T1/T6 flagged it before. It is not a supported-range or exact-package client claim for the manifest field.
- The packed `RELEASE-NOTES.txt` is the generated distribution notice (version, licenses, prerequisites, unsigned status), not the user-facing guide; see [release notes](release-notes-2.0.0.md).

Changing either manifest string would require reassembly and a new candidate identity, so they are disclosed rather than rewritten in place.

## Source binding, snapshot and documentation drift

The candidate binds HEAD `a5e13717…` and the assembly-instant tree digest `7eb452354d39…`. The reviewed compile-affecting delta for this task is the single version owner `Directory.Build.props` (1.8.0 → 2.0.0) on top of the T1 checkpoint. This record, the release notes, migration guide, operations text, changelog and owner walkthrough were written **after** assembly and are not compiled into either asset; they change the live working-tree digest away from `7eb452354d39…`. That is documentation-only drift, not a different candidate. Do not reassemble silently or substitute assets; review and checkpoint use the exact hashes above.

The checkpointed docs commit `54d6cd27…` is a documentation checkpoint, not the tag target and not the build tree. The assembly-instant digest `7eb452354d39…` remains the only tree digest attributed to candidate `bc8ebc909aa8`; it cannot be recomputed from the current repository because it covered assembly-time working-tree bytes (including then-untracked fixture copies) that were not preserved. No correction here rebuilds, relabels, replaces or re-identifies either asset.

## Intended tag source and pre-checkpoint input measurement

The 2.0.0 assets stay frozen at the identity under “Exact assets”; every correction in this record is documentation-only. The intended annotated `v2.0.0` tag is **not** candidate source `a5e13717…` and **not** the already-checkpointed docs commit `54d6cd27…`. Tagging `a5e13717` would omit the 2.0.0 release delta entirely; tagging `54d6cd27` would publish a tree whose committed `docs/REQUIREMENTS.md` and `.agents/memory/decisions.md` do not contain the D114–D117 records these documents cite, and whose roadmap still contradicts the recorded T1/T2 completion. The intended target is a **separate documentation-integration commit** containing the five authoritative Main-owned records (`.agents/memory/decisions.md`, `docs/META-BRAIN.md`, `docs/PLAN.md`, `docs/REQUIREMENTS.md`, `docs/ROADMAP-2.0.md`), this corrected record, the release notes and the other synchronized worker docs, after its own independent review and exact-snapshot checkpoint. Its commit SHA and true tree ID do not exist at this evidence cut and are deliberately not stated; they may be recorded only after that checkpoint, in a separate evidence record.

### Pre-checkpoint input comparison (measured 2026-11-22)

No tag-tree digest is computed in this file: this file is part of the commit it would describe, so a digest placed here would be self-referential. Instead, the candidate-affecting input population was compared between the checkpointed HEAD `54d6cd27…` and the projected integration worktree.

Population: 309 tracked paths — the assembler population (`git ls-files --cached` minus its anchored exclusion regex for `artifacts/`, `runtime/`, `dist/`, `build/`, `out/`, `.git/`, `docs/PLAN.md`, `docs/sprint-plans/sprint-10.md` and `scripts/sprint-6/__pycache__/`), with documentation/marketing/agent-record paths (`docs/`, `artifacts/`, `website/`, `.agents/`, `README.md`, `CHANGELOG.md`, `AGENTS.md`) excluded.

| Measurement | Result |
|---|---|
| Build/package input population | 309 tracked paths (defined above) |
| HEAD `54d6cd2` committed blobs vs projected worktree | 268 byte-identical; 41 line-ending-only (LF in the committed blob, CRLF materialized on disk); **0 content differences** |
| Assembler-protocol digest, committed HEAD blobs | `a1cab689d0aabe720df1cc9e82b189aa73e1cba8a073d876d77061b5b2d15b86` |
| Assembler-protocol digest, line-ending-normalized HEAD blobs and worktree | `94a0a3edd7ff0d914b7cbe801040ff988766b411007edd00f4f207b9657713c2` (equal on both sides) |
| Version owner `Directory.Build.props` | `0c798a3686bb662491920a46f300008b974bebc722dcdc9b93fb535be3d6ed4d` at A1 handoff, R1 review manifest, CP1 committed blob and projection; base `a5e13717` carries `1.8.0`, HEAD and projection carry `2.0.0` |
| Build/package paths modified or newly untracked in the projection | **0** (only the five Main-owned docs and this correction are dirty) |
| Frozen assets rehashed at this cut | CLI `e08c03e6…` 234,654,938 B; MCP `cc733b1d…` 90,184,857 B; manifest `50d64d6c…` 439,258 B — unchanged |

Protocol: sorted-unique `<sha256>  <path>` lines joined by LF, UTF-8 SHA-256; path source `git ls-files --cached` and committed `HEAD` blobs for the checkpointed side, tracked worktree bytes for the projection. Materialization caveat: the assembly run hashed live worktree bytes (CRLF for 41 sources), the commit stores LF blobs, and 21 of the 309 committed blobs themselves contain CRLF bytes, so raw and normalized digests differ; the normalized equality above shows the difference is materialization only, not a source-content change. Exclusion caveat: 71 untracked non-ignored paths (the `cl.diff` capture and repository-local fixture copies under `src/**/runtime/`) were inside the assembly-time worktree digest but cannot enter a commit, so they are outside the proposed-tag population. Provenance limit: because the assembly-time worktree was not preserved, the full digest `7eb452354d39…` cannot be recomputed; this record therefore claims only that the projected commit changes **no** candidate-affecting input relative to checkpointed HEAD `54d6cd27…` and that the documented compile-affecting delta (`Directory.Build.props`) was present at assembly. It does not claim full assembly-tree equivalence.

### Required post-checkpoint measurement (separate record)

Immediately after the documentation-integration checkpoint, a separate evidence record — not this document — must measure the actual commit before any tag is bound:

1. Resolve the checkpointed commit and `git rev-parse <commit>^{tree}`.
2. Enumerate the committed population with the assembler algorithm and its anchored exclusion regex; a commit has no untracked members.
3. Recompute per-path SHA-256 over committed blobs for every candidate-affecting path and the assembler-protocol digest over the committed population; compare them with the table above, including `Directory.Build.props`.
4. Rehash the two assets and the manifest and compare them with “Exact assets”; assets are uploaded byte-exact and never replaced.
5. Record the tag target SHA, tree ID, digests, exclusions and materialization notes only in that record.

If any candidate-affecting input differs, publication stops. The later commit must not be described as the build tree, and candidate `bc8ebc909aa8` is never attributed to it as its assembly identity; only the frozen assets carry that identity.

## Feature and requirement mapping

The complete 2.0.0 mapping traces the roadmap outcomes P2-01–P2-12 and the R01–R40 outcomes to the accepted sprint evidence. Unchanged surfaces inherit their accepted evidence; changed surfaces append the exact-candidate evidence above. Evidence pointers are repository-relative to `F:/ai-ml/my-meta-brain`.

### P2 outcomes

| Outcome | Delivered in | Evidence pointers |
|---|---|---|
| P2-01 folder enrollment, raw/partial/unsupported honesty | 1.1.0 | `artifacts/sprint-11/task-2.md`, `reviews/S11-T2-CYCLE2-R1.md`, `checkpoints/S11-T2-C1.md`; `artifacts/sprint-11/task-3.md`, `reviews/S11-T3-CYCLE1-R1.md` |
| P2-02 encrypted originals, key/recovery/lock, identity, export | 1.2.0 | `artifacts/sprint-12/task-2.md`, `reviews/S12-T2-C1-R2.md`, `checkpoints/S12-T2-C1.md`; T3/T4 evidence |
| P2-03 CLI read/revise/search + optional browser document edit | 1.3.0 | `artifacts/sprint-13/task-2.md`–`task-5.md`, `reviews/S13-T2…T5-C1-R1.md`, `checkpoints/S13-T2…T5-C1.md` |
| P2-04 six memory facets, multi-label passages, owner corrections | 1.4.0 | `artifacts/sprint-14/task-1.md`–`task-5.md`, `reviews/S14-T1…T5-C1-R1.md`, `checkpoints/S14-T1…T5-C1.md` |
| P2-05 temporal/intention/forgetting and history | 1.4.0 / 1.8.0 | `artifacts/sprint-14/task-4.md`, `reviews/S14-T4-C1-R1.md`; `artifacts/sprint-18/task-4.md` |
| P2-06 exact/lexical/structural/semantic Việt–Anh recall | 1.5.0 | `artifacts/sprint-15/task-1.md`, `reviews/S15-T1-C1-R2.md`; `task-5`, `reviews/S15-T5-C1-R1.md` |
| P2-07 typed relations, provenance, stale evidence, bounded traversal | 1.6.0 | `artifacts/sprint-16/task-2.md`–`task-5.md`, `reviews/S16-T2…T5-C1-R1/R2.md`, `checkpoints/S16-T2…T5-C1.md` |
| P2-08 folder scope preview/revoke/lock/expiry | 1.7.0 | `artifacts/sprint-17/task-2.md`–`task-3.md`, `reviews/S17-T2-C1-R1.md`, `S17-T3-C1-R1.md` |
| P2-09 context pack with sources/budget/contradictions; normal clients | 1.7.0 | `artifacts/sprint-17/task-4.md`, `reviews/S17-T4-C1-R1.md`; normal-client evidence `artifacts/sprint-17/handoffs/S17-T5-IMPL-20261117-A9.md`/`A10.md`, `reviews/S17-T5-C1-R2.md` (historical evidence points, not a version range) |
| P2-10 bounded cache/responsiveness/quality | 1.3.0 / 1.5.0 / 1.8.0 | `artifacts/sprint-13/task-5.md`; `artifacts/sprint-15/task-5.md`; `artifacts/sprint-18/task-5.md`, `reviews/S18-T5-C1-R2-verified.md` |
| P2-11 lossless migration/recovery/backup/export/purge and clean cutover | 1.2.0 / 1.8.0 / 2.0.0 | `artifacts/sprint-12/task-5.md`; `artifacts/sprint-18/task-2.md`–`task-4.md`, `reviews/S18-T2-C1-R2.md`, `S18-T3-C1-R1.md`, `S18-T4-C1-R1.md`; 2.0.0 changed-path evidence in this record |
| P2-12 usable release every sprint; CLI installer + MCP ZIP; docs/integrity | 1.1.0–1.8.0 / 2.0.0 | Published release records `docs/release-1.1.0.md`…`docs/release-1.8.0.md`, public reviews `artifacts/sprint-12/reviews/S12-PUBLIC-C1-R1.md`, `S13-PUBLIC-C1-R1.md`, `S14-PUBLIC-C1-R1.md`, `S15-PUBLIC-C1-R1.md`, `S16-PUBLIC-C1-R2.md`, `S17-PUBLIC-C1-R2.md`, `S18-PUBLIC-C1-R1.md`; this exact 2.0.0 candidate |

### R outcomes (grouped as in the accepted S18-T1 trace)

| Requirement group | Inherited evidence | 2.0.0 status / changed-path note |
|---|---|---|
| R01–R09 assistance, owner authority, one-time tokens, encrypted vault, catalog, consent, injection | S1/S4/S6 gate records and reviews; `artifacts/sprint-18/task-1.md` §3.2 | No new authority, provider call, catalog expansion or declassification. `metabrain` adds no grant; owner/key boundary unchanged. |
| R10–R15 envelope, zones, attribution, relations, explicit links | S2/S4/S14/S16 evidence; `reviews/S16-T1-C1-R1.md` | Unchanged. Workspace format 1/schema 1 observed before and after the 1.8→2.0 upgrade. |
| R16–R19 sources, incremental ingestion, preservation | S3/S11 evidence; `reviews/S11-T2-CYCLE2-R1.md`, `S11-T4-CYCLE1-R1.md` | Unchanged; no source file was touched by 2.0.0 verification. |
| R20–R25 profiles, synthesis, recall, MCP, brief, jobs | S6/S15/S17 evidence | Unchanged; packaged MCP remains 26 tools. Direct wire proof plus bounded actual-client evidence: OMP executed the read-only catalog tool and the owner-operated interactive Codex session called it, while two authorized non-interactive `codex exec` runs initialized the server but called no tool (D117). No client-range or full Codex `exec` claim. |
| R26–R28 time, intentions, forgetting | S7/S14 evidence | Unchanged (D114 retains legacy lifecycle workflows). |
| R29 purge scope, R34 backup/restore/export, R36 private data, R33/R39 encrypted canonical and recovery | S2/S7/S8/S12/S18 evidence; `docs/REQUIREMENTS.md:43,47-53` | 2.0.0 exact-candidate backup/restore, wrong-key refusal, uninstall data preservation and version refusal observed; no format or key change. |
| R30/R31 CLI owner-complete and sharing | S12–S17 gate records | The workspace-default `metabrain` command and unchanged `MetaBrain.Cli.exe owner …` surface both exercised; legacy memory/source/relation/export/migrate paths remain reachable per D114. |
| R32 core/application/connections boundaries | S1/S12–S17 evidence | No second policy or engine added; D114 found no active redundant WPF packaging callers to remove. |
| R35 install/start/restart/upgrade/uninstall | S8/S12 evidence; `docs/sprint-plans/sprint-12.md` T5/T6 | 2.0.0 changed path exercised: 1.8→2.0 in-place preserve, 1.0/pre-1.2 refusal + remediation, owner-ui refusal, locked restart, data-preserving uninstall. Persistent user-PATH evidence is owner-run; D116 accepts the combined clean-install lifecycle/pre-existing-entry preservation plus fresh-PowerShell discovery after the opted-in 1.8.0 upgrade, and records that a distinct fresh-shell check after clean install was not observed. |
| R37 qualitative acceptance, R38 operational envelope | S8/S15/S17/S18 evidence; `docs/REQUIREMENTS.md:51-52`; D112/D113 | 1.8/S18 acceptance inherited only for unchanged surfaces. **D113’s report-only exception was 1.8.0-only and does not carry into 2.0.0; no new quantitative gate is invented here.** The approved baseline measurement obligation remains, and this task makes no new benchmark equivalence claim. |
| R40 honest requirements/planning/evidence | Every sprint gate record; `docs/REQUIREMENTS.md:53` | This record distinguishes candidate from published, exact bytes from historical ones, observed from inferred, and unmet/owner-only proofs below. |

Historical 1.0/1.1 acceptance, downloads, bytes and evidence remain unchanged and are not re-derived or relaxed by 2.0.0.

## Gate status and exclusions

- **Completed gates:** the owner accepted candidate `bc8ebc909aa8` for independent review, not for publication. Independent evidence review `G2-T2-C1` round 1 **PASSED** ([report](../artifacts/g2/reviews/G2-T2-C1-R1.md)), and the separate Bronze exact-snapshot checkpoint `G2-T2-CP1` ([record](../artifacts/g2/checkpoints/G2-T2-CP1.md)) committed the reviewed scope at `54d6cd27aadf674952bd92647f959b829496b469` (parent `a5e13717…`). Major differential `G2-DEEP-D1` ([report](../artifacts/g2/reviews/G2-DEEP-D1.md)) then returned **FAIL to advance** on the publication handoff: F1, no truthful tag binding and dangling normative references in the checkpointed tree; F2, stale release-doc gate status.
- **Current correction (not a gate result):** this documentation-only correction refreshes the status text and defines the tag strategy and measurement obligation above. It is submitted as `G2-T2-C1` round 2; it is not yet reviewed, not checkpointed and not a PASS.
- **Pending stages:** independent review of this correction; a separate exact-snapshot checkpoint of the documentation-integration commit; the post-checkpoint committed-blob measurement in a separate evidence record; a fresh major differential; and only then the separate G2-T3 owner publication authorization. No future PASS, commit SHA, tag SHA or date is claimed here.
- **Owner PATH evidence (D116):** the owner ran the persistent-PATH lifecycle. D116 accepts the owner-run clean-install add/keep/uninstall lifecycle and pre-existing-entry preservation together with owner-confirmed fresh-PowerShell command discovery after the opted-in 1.8.0 upgrade; a distinct fresh-shell observation after clean install was not required for this candidate and was not observed. D115 product and host-safety rules are unchanged, including the ban on agent-run persistent PATH changes. The walkthrough remains `artifacts/g2/owner-walkthrough-2.0.0.md`.
- **Normal-client evidence (D117):** OMP executed the read-only `metabrain_catalog_list` tool on the exact candidate; the owner-operated interactive Codex session called `metabrain.metabrain_catalog_list` and received `CATALOG no_match`; both authorized non-interactive `codex exec` runs initialized the server without calling a tool; direct stdio verified the same server advertises it. D117 accepts that bounded combination in place of a successful Codex tool call. This is not a Codex `exec` PASS, a compatibility guarantee or a supported version range.
- **Publication:** not authorized and not performed. Tag `v2.0.0`, Release, assets, Pages and public smoke belong to G2-T3 only after the stages above, including the measured tag binding; the assets remain the frozen candidate bytes.
- **Explicit gaps:** the worker ran no installed-host lifecycle (host mutation forbidden); the owner-run PATH/upgrade lifecycle is accepted only as recorded by D116. No WPF replay (no revival); no site build; no browser replay; no provider/model run beyond the bounded read-only catalog calls above; no semantic query or index-rebuild run; no new performance measurement; the assembled manifest’s `supportTransition`/`testedOhMyPiVersion` strings remain stale as disclosed above.

See [the 2.0.0 release notes](release-notes-2.0.0.md), [the 2.0.0 migration/compatibility guide](migration-2.0.0.md), [operations](OPERATIONS.md) and the [owner walkthrough](../artifacts/g2/owner-walkthrough-2.0.0.md).
