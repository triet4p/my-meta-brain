# Meta Brain 1.7.0 — exact local candidate record

**Status: published release `v1.7.0` (not a prepublication candidate).** The exact reviewed candidate `20af9d477aba` passed independent evidence review `S17-T6-C1` R1, was checkpointed at `fd8ad2f7d150f3f0cfd893ec51ad76158b0f860e`, and was published on 2026-11-18 as [GitHub Release `v1.7.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.7.0) (id `449509004`) with five byte-exact assets and a Pages site. 1.7.0 is the latest published CLI + MCP release. The public `S17-PUBLIC-C1` round 2 review and Sprint 17 Complete remain pending; a corrected site-only status-text delta is prepared but not yet deployed. See [Publication observed](#publication-observed-s17-publish-p1) and `artifacts/sprint-17/publication/S17-PUBLISH-P1.md`.

## Publication observed (S17-PUBLISH-P1)

The reviewed checkpoint was published; the frozen candidate identities and verification below are unchanged.

| Item | Exact public identity / link |
|---|---|
| Reviewed source / tag target | `fd8ad2f7d150f3f0cfd893ec51ad76158b0f860e` |
| Annotated tag | `v1.7.0` → tag object `3ca562288825cc120bc84769008f46ac90faa6a6` → commit `fd8ad2f…`; [tag page](https://github.com/triet4p/my-meta-brain/releases/tag/v1.7.0) |
| GitHub Release | `v1.7.0`, id `449509004`, published `2026-11-18T04:03:20Z`, not draft/prerelease, Latest; [release API](https://api.github.com/repos/triet4p/my-meta-brain/releases/tags/v1.7.0) |
| CLI asset | [MetaBrain-CLI-1.7.0.exe](https://github.com/triet4p/my-meta-brain/releases/download/v1.7.0/MetaBrain-CLI-1.7.0.exe) — 234,400,474 bytes, SHA-256 `b3a2c8b8b7d672b9a9638843b1967beea28a2751fcc50b6a8a3a3aff7bf15546` |
| MCP asset | [MetaBrain-MCP-1.7.0.zip](https://github.com/triet4p/my-meta-brain/releases/download/v1.7.0/MetaBrain-MCP-1.7.0.zip) — 90,115,562 bytes, SHA-256 `853d6364630963f218d4c65ab0b408c083bd7328d4254a925811d5531e2e364d` |
| Manifest | [candidate-manifest.json](https://github.com/triet4p/my-meta-brain/releases/download/v1.7.0/candidate-manifest.json) — 439,253 bytes, SHA-256 `fbabdad6d950b0f15521708bf6498f2d133b3b01cdeea1a8673a5ec6c55b7e5e` |
| Checksums | [SHA256SUMS.txt](https://github.com/triet4p/my-meta-brain/releases/download/v1.7.0/SHA256SUMS.txt) — 180 bytes, SHA-256 `ab4d0e539287a6fd15548fd6d1ecc699a582c8f07c6451647232b31a3eadd346` |
| License | [LICENSE](https://github.com/triet4p/my-meta-brain/releases/download/v1.7.0/LICENSE) — 1,064 bytes, SHA-256 `b623a3c47894f85a2a48029b580e6ee0fec70830401fcb39a2e1567b21322ec9` |
| Public site | [https://triet4p.github.io/my-meta-brain/](https://triet4p.github.io/my-meta-brain/) — `gh-pages ebf5f9f9de188fbe526ca71b537de4ca7c506d99`, Pages build `built`; corrected status-text delta prepared but not yet deployed |

**Closure pending (not a gate PASS):** `S17-PUBLIC-C1` R1 was FAIL (`advance: false`) for stale prepublication text on the live site; R2 is unconsumed. A site-only correction is prepared at `artifacts/sprint-17/handoffs/S17-PUBLIC-FIX-20261117-A1.md` for separate review and deployment before Complete. No public PASS, future commit or further publication is asserted here. Evidence: `artifacts/sprint-17/publication/S17-PUBLISH-P1.md`; `artifacts/sprint-17/reviews/S17-PUBLIC-C1-R1.md`.

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

- Observed complete (inherited, not re-run here): evidence review `S17-T6-C1` R1 PASS, exact-snapshot Bronze checkpoint at `fd8ad2f…`, final differential PASS, tag `v1.7.0`, GitHub Release `449509004` and Pages deployment of `ebf5f9f…`.
- Public release-boundary verification `S17-PUBLIC-C1` R1 = **FAIL** (`advance: false`) for stale prepublication text on the live site; round 2 is unconsumed. Sprint 17 Complete is not claimed. The site-only correction at `artifacts/sprint-17/handoffs/S17-PUBLIC-FIX-20261117-A1.md` is prepared for separate review and deployment.
- Candidate-level **normal Codex/OMP client proof is absent**. T5 R2 normal-client evidence applies only to unchanged T5 source behavior, not to the 1.7.0 bytes. Fresh T6-scoped owner consent (client scopes, provider execution, turn/cost caps) is required before any model-backed client session. A bounded proposal is in `artifacts/sprint-17/task-6.md`.
- No provider/model workflow, semantic query, index rebuild, local inference, browser render or public network call was run for T6; no T5 consent was reused. All behavior evidence is synthetic and fixture-specific. Publication, tag, asset upload, site deployment and public smoke were performed and observed by `S17-PUBLISH-P1` (recorded above); no gate PASS, future commit or further publication is asserted here.
