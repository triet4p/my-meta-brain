# Meta Brain 1.7.0 — exact local candidate record

**Status: exact local candidate assembled and verified; NOT independently reviewed, checkpointed, published or deployed.** No review PASS, frozen-snapshot manifest, Bronze checkpoint, public download, Release, tag, external smoke or publication authorization exists for 1.7.0. 1.6.0 remains the latest published release. This is a prepublication record of the verified candidate bytes, not release approval.

## Exact candidate identity

| Field | Value |
|---|---|
| Candidate directory | `runtime/release-candidates/1.7.0-win-x64-55d35ec31a8c-treec47629d2bcde-20af9d477aba/` |
| Candidate ID | `20af9d477aba` |
| Source identity | `55d35ec31a8c-treec47629d2bcde` |
| Candidate identity | `55d35ec31a8c-treec47629d2bcde-20af9d477aba` |
| Source HEAD | `55d35ec31a8c4e9c8df41bb98b4b96d68776d47a` |
| Assembly-instant source-tree SHA-256 | `c47629d2bcdef045f9c189c12fc195248959521d607e532af98d7ba761608a89` |
| Product / version / RID | Meta Brain `1.7.0`, `win-x64`, self-contained |
| Assembled (UTC) | `2026-11-18T02:43:25.6063076Z` |
| Signing | `unsigned; SHA-256 does not authenticate publisher` |
| Assets | exactly two: dedicated CLI setup EXE and standalone MCP ZIP; no WPF/UI installer |

`workingTreeIncluded: true`. The source-tree digest is the assembly-instant digest of the tracked/non-ignored source population enumerated by `git ls-files --cached --others --exclude-standard` minus the assembler's documented exclusions (`artifacts/`, `runtime/`, `dist/`, `build/`, `out/`, `.git/`, `docs/PLAN.md`, `docs/sprint-plans/sprint-10.md`, `scripts/sprint-6/__pycache__/`). It binds the frozen candidate bytes to the snapshot they were built from; it is distinct from the later review frozen snapshot and any eventual publication tag. The candidate was not rebuilt or byte-renamed to make an identity look cosmetic.

**Post-assembly drift (explicit):** this record, `docs/release-notes-1.7.0.md`, the compatibility/operations/guide status text and the `scripts/site/build.ps1` 1.7.0 pin were completed **after** assembly. They are documentation and tooling, not compiled into the assets. They change the live working-tree digest away from `c47629d2bcde…`; the exact post-edit digest is recorded in `artifacts/sprint-17/task-6.md`. Do not treat the changed tree as the same snapshot, do not silently reassemble, and do not weaken the site-builder identity checks to accommodate the drift. A future rebuild is a new candidate with a new identity and must be verified and reviewed again.

## Exact assets

| Product | Candidate filename | Bytes | SHA-256 |
|---|---|---|---|
| CLI setup | `MetaBrain-CLI-1.7.0-win-x64-55d35ec31a8c-treec47629d2bcde-20af9d477aba.exe` | 234,400,474 | `b3a2c8b8b7d672b9a9638843b1967beea28a2751fcc50b6a8a3a3aff7bf15546` |
| MCP package | `MetaBrain-MCP-1.7.0-win-x64-55d35ec31a8c-treec47629d2bcde-20af9d477aba.zip` | 90,115,562 | `853d6364630963f218d4c65ab0b408c083bd7328d4254a925811d5531e2e364d` |

The CLI setup embeds a payload of 135,176,713 bytes / SHA-256 `ae506dce10ebd2d0b2e04383a4bb3adcafe73f4e873ddf6bdfb95fcb8beb3643` with 626 members (including `LICENSE`, `RELEASE-NOTES.txt`, `THIRD-PARTY-NOTICES.txt`, `cli/MetaBrain.Cli.exe`, `cli/MetaBrain.Connections.exe`, `service/MetaBrain.Connections.exe`). The MCP ZIP holds 421 members including `LICENSE`, `RELEASE-NOTES.txt`, `THIRD-PARTY-NOTICES.txt`, `package-manifest.json`, `mcp/MetaBrain.Mcp.exe` and `service/MetaBrain.Connections.exe`. The embedded/extracted package manifests carry the same candidate ID, HEAD and source-tree digest as the candidate manifest. Dependency/license/signing inventories are in `candidate-manifest.json`.

## Assembly provenance

The owner ran the repository-local assembler after resolving `NU1102`: `dotnet nuget verify --all` reported Microsoft author and NuGet.org repository signatures on the three Microsoft `10.0.15` `win-x64` runtime packs. Copying them into the global-packages directory did not satisfy restore. The signed packages were instead staged in a repository-local directory used as a distinct offline source; restore and assembly then completed with `VERIFIED` and `CANDIDATE`. No network download, cache/global fix, profile or host mutation was used. The worker did not rerun assembly; it independently re-verified the resulting exact bytes.

## Verification actually observed on the exact candidate

All runs used the candidate bytes or binaries installed/extracted from them under ignored repository-local roots, with `TEMP`/`TMP`/`DOTNET_BUNDLE_EXTRACT_BASE_DIR` redirected to repository-local paths. No owner corpus, credential, profile, registry, service registration or global setting was touched.

1. **Integrity:** both asset SHA-256 values and byte counts independently recomputed and matched the manifest. `scripts/release/assemble-candidate.ps1 -VerifyOnly -CandidateDirectory <exact directory>` returned `VERIFIED …` with exit 0, confirming frozen manifest/package-manifest identity, asset and member hashes.
2. **CLI setup:** `--version` → `MetaBrain CLI Setup 1.7.0`; `--verify-payload` → 135,176,713 bytes / `ae506dce…`, 626 members, project `LICENSE`, release notes and notices present; exit 0.
3. **Packaged MCP:** extracted ZIP `mcp/MetaBrain.Mcp.exe --version` → `MetaBrain.Mcp 1.7.0`. Real stdio JSON-RPC `initialize` negotiated `2025-11-25` and `2025-06-18`, returned `serverInfo` version `1.7.0`; `notifications/initialized` silent; `tools/list` returned 26 tools; `metabrain_catalog_list` returned `CATALOG no_match`; exit 0 and the shared host stopped cleanly.
4. **Packaged CLI/service changed surfaces:** the unchanged T5 smoke harness built from the same source (0 warnings/0 errors) ran `s17-t3` and `s17-t4` against the installed packaged CLI/service and the extracted packaged MCP. Both exited 0: agent/owner channel and grant boundaries, protected handoff/redeem/revoke, D110 context-pack budget/citation/verbatim/drift/expiry/revoke/lock/restart negatives all PASS.
5. **Isolated 1.6.0 → 1.7.0 upgrade/recovery:** 1.6.0 setup installed into fresh sibling roots, provisioned a synthetic vault, imported exact bytes, stored a canonical memory item; 1.7.0 setup upgraded in place. 36 PASS lines + `LIFECYCLE_DONE`, exit 0: only `data-root-owner.json` changed at install; settings and both pipe endpoints byte-identical; host restarted locked; wrong passphrase refused; passphrase and recovery key unlocked; workspace identity/bytes/search and memory preserved; a second install sharing the data root refused before writes; host stopped.
6. **Installer host-mutation inspection:** static review of `src/MetaBrain.Installer` and the prerequisite reader found explicit install/data roots, temporary payload extraction, no PATH/profile/autostart/service/registry/permission changes, and a read-only registry check (`writable: false`). No installer action was run against an owner installation.

## Known finding (reviewer-actionable)

The candidate manifest's `compatibility.mcp.testedOhMyPiVersion` is `18.12.6`, while the same manifest's dependency-inventory evidence and the assembler source record the observed Sprint 17 T5 OMP version `18.20.0`. This is a stale documentation field in the frozen candidate, not a behavior defect; it is disclosed rather than silently edited, because changing it would require reassembly and a new candidate identity.

## Remaining gates and limits

- Independent evidence review `S17-T6-C1` R1 and the exact-snapshot Bronze checkpoint are pending and Main/reviewer-owned.
- Candidate-level **normal Codex/OMP client proof is absent**. T5 R2 normal-client evidence applies only to unchanged T5 source behavior, not to the 1.7.0 bytes. Fresh T6-scoped owner consent (client scopes, provider execution, turn/cost caps) is required before any model-backed client session. A bounded proposal is in `artifacts/sprint-17/task-6.md`.
- No provider/model workflow, semantic query, index rebuild, local inference, browser render or public network call was run for T6; no T5 consent was reused. All behavior evidence is synthetic and fixture-specific. No publication, tag, upload, deployment or public smoke was performed.
