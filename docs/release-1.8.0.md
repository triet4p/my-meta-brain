# Meta Brain 1.8.0 — exact local candidate record

> **Post-publication note (2026-11-21):** the exact candidate below was published unchanged as [`v1.8.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.8.0) on 2026-11-20 (release id `452370096`, Latest; CLI `f977aada…` 234,638,554 B, MCP `53080c42…` 90,183,118 B). The candidate-time status and “pending” statements in the rest of this page are preserved historical text, not the current state; 1.8.0 is the latest published release.

**Status at candidate freeze (historical):** exact local candidate assembled and verified; review, checkpoint and differential gates complete; publication was pending separate owner authorization at freeze. 1.7.0 remains the latest published release (`v1.7.0`, 2026-11-18). The two 1.8.0 assets below were produced once by the unchanged guarded assembler and independently checked. No installed-host upgrade, tag, upload, deployment or publication was performed. Independent evidence review `S18-T6-C1/R2` returned PASS, the exact-snapshot checkpoint `S18-T6-CP1` was created at `829110c3…`, and the final differential `S18-DEEP-D3` returned PASS; only owner-authorized publication and the post-publication steps remain. This record proposes the already-reviewed commit `829110c33ba20b9a78d0eb6cb1b3580c7e1016bb` as the `v1.8.0` tag target and does not create the tag.

## Candidate identity

| Field | Value |
|---|---|
| Product / version / RID | Meta Brain `1.8.0`, `win-x64`, self-contained |
| Source HEAD | `e149a34a30c5646129b1df401780305a7a530ec1` (S18-T5 Bronze checkpoint commit) |
| Candidate directory | `runtime/release-candidates/1.8.0-win-x64-e149a34a30c5-tree2ce06fb85f6a-2bc84743d74c` |
| Candidate ID / source identity | `2bc84743d74c` / `e149a34a30c5-tree2ce06fb85f6a` |
| Assembly-instant tree digest | `2ce06fb85f6adabdae7923a3535fcaade81acfadcbfbea554aae9da042367b2f` (433 files, assembler algorithm) |
| Assembled at (UTC) | `2026-11-20T06:07:23.1765504Z` |
| CLI setup | `MetaBrain-CLI-1.8.0-win-x64-e149a34a30c5-tree2ce06fb85f6a-2bc84743d74c.exe` — 234,638,554 bytes / SHA-256 `f977aada1933a908b126af26cae81035d271d17d6bc4d87a1fcf61e2b71837a8` |
| Embedded setup payload | 135,278,061 bytes / SHA-256 `1d8c76fa46b4e2b309a8d2be28efd3a66c501080dc180f828dbd44d2c2c4a960` (626 members) |
| MCP ZIP | `MetaBrain-MCP-1.8.0-win-x64-e149a34a30c5-tree2ce06fb85f6a-2bc84743d74c.zip` — 90,183,118 bytes / SHA-256 `53080c422c3815d4d8b00fcb99c84fceb5f8c3f14195e143da156fc89aa0550b` (421 members) |
| Signing | unsigned; SHA-256 detects byte changes but does not authenticate the publisher |
| Assets | exactly two: the dedicated CLI setup and the standalone MCP ZIP; no WPF/UI installer |

The complete member, dependency, license and provenance inventory is in `candidate-manifest.json` beside the assets. No 1.7.0 bytes were retagged or substituted.

## Pinned inputs and provenance

All seven git-ignored pinned inputs were admitted only after exact byte-size and SHA-256 equality with the assembler's constants; the assembler's guards were not changed, bypassed or relaxed.

- Restored byte-exact from retained repo-local evidence in `runtime/s15-public-evidence-r1/mcp-extract/licenses/`: `dotnet-vmr-LICENSE.TXT` (1,115 bytes / `ae48df11a335dc1a615f4f938b69cba73bcf4485c4f97af49b38efb0f216353b`) and `System.Numerics.Tensors-10.0.14-COPYRIGHT.txt` (46 bytes / `30bedab2f6c25a864a0c7ceac40f5c4ce1816684db188e87644e72acf6a2b8c3`).
- Downloaded from the recorded original public URLs into ignored staging, verified there, then copied into the pinned directory: `dotnet-vmr-THIRD-PARTY-NOTICES.txt` (265,586 / `5a90be90…`) from `https://raw.githubusercontent.com/dotnet/dotnet/0427b9abbead3fa64c6655eb4290c537397131fa/THIRD-PARTY-NOTICES.txt`; `System.Numerics.Tensors-README.md` (122 / `2ab3b3eb…`) from the same commit's `src/runtime/src/libraries/System.Numerics.Tensors/README.md`; `licenses.nuget.org-MIT.html` (2,442 / `0ff20dda…`) from `https://licenses.nuget.org/MIT`; `spdx-MIT.html` (13,452 / `b7eaaa2c…`) from `https://spdx.org/licenses/MIT.html`. The first README attempt at `src/libraries/…` returned HTTP 404; the corrected VMR path was identified from read-only directory inspection of the same immutable commit and produced the exact pinned hash.
- Downloaded the exact upstream source archive from `https://github.com/sappho192/Tokenizers.DotNet/archive/b8eaa77d72bf9d9c768d75599bfa6a602e467b93.zip`: 1,236,124 bytes / SHA-256 `2ee9c2d81aba55396ca67544765bd5cf2438ff35b5a959fe820915e449f77ded`. It was hash-checked only, never unpacked or executed.
- Already present and re-verified: `Cargo.lock` (`7597e886…`) and the native third-party notices (`1d67c3d6…`).

No repository content or credentials were transmitted, no fetched code was executed, no other dependency was fetched, and no host/profile/registry/certificate mutation occurred.

## Verification actually observed

One unchanged assembler invocation logged at `runtime/s18-t6-a2-staging/assembly.log` completed offline restores, four Release publishes, an 817-file stage and `VERIFIED` / `CANDIDATE`. The assembler's own guarded `-VerifyOnly` returned `VERIFIED` against the candidate directory (`runtime/s18-t6-a2-staging/verify-only.log`), checking every asset and member hash, the embedded payload, Tensor licence metadata and the MCP contract. Independently observed:

- CLI setup and MCP ZIP sizes and SHA-256 equal the manifest declarations.
- `MetaBrain-CLI-…exe --version` → `MetaBrain CLI Setup 1.8.0`; `--verify-payload` → 135,278,061 bytes / `1d8c76fa…`, 626 files, product `Meta Brain CLI 1.8.0`.
- MCP ZIP has 421 members; `mcp/MetaBrain.Mcp.exe` and `service/MetaBrain.Connections.exe` member hashes match the manifest and are byte-identical to the published Release win-x64 inputs (the service hash is `7cba3c84f89c239128d35912dd51886ac5d873721f306d3a328af1ed0cfc8bbd`, also the CLI payload member).
- Real opt-in migration fixtures against that exact candidate-byte service on fresh synthetic fixtures: `s18-t2` EXIT 0 (write-free preview, verified backup, typed execute, refusal paths); `s18-t3` EXIT 0 (resume completed every persisted boundary with exact bytes, rollback restored only the verified pre-migration vault); `s18-t4` EXIT 0 (closed-impact preview, both purges without resurrection, exact-byte labelled export, owner correction retained).
- MCP stdio probe against the exact candidate MCP member: `initialize` negotiated `2025-11-25`, `serverInfo {name: metabrain-agent, version: 1.8.0}`, `tools/list` returned 26 tools; the spawned test service was terminated and no MetaBrain process remained.

Not executed and not claimed: an installed-host upgrade or isolated lifecycle test (no separate authorization), normal Codex/OMP sessions, provider calls, browser/WPF replay, site build, network publication, or any assembler bypass. The `s18-t4` first-run `.tmp` race observed in attempt A1 was a harness TOCTOU; the corrected candidate-byte run above passed without a race.

## Source binding and the later snapshot

The candidate binds HEAD and the assembly-instant tree digest `2ce06fb…`, reproduced with the assembler's exact PowerShell `ls-files`/`Sort-Object`/SHA-256 algorithm. The compiled inputs are the reviewed T1–T5 commits; the only compile-affecting T6 change is the `Directory.Build.props` version, and the assembler edit is label-only. Git-ignored pinned inputs are outside the source digest.

Documentation recorded after assembly (this record, the task artifact and corrected status sentences) is not compiled into either asset; it changes the live tree digest away from `2ce06fb…`. That is expected documentation-only drift, not a different candidate. An ordinal-sorted recomputation yields a different digest and is not the assembler's algorithm.

## Proposed tag source — measured provenance (local post-checkpoint record)

This section was recorded after checkpoint `S18-T6-CP1` as a **local, uncommitted record**. It is deliberately **not part of the proposed tag**: every digest below is computed from the blobs of commit `829110c3…`, not from this edited file, so the record cannot make its own digest self-referential.

| Measurement | Value |
|---|---|
| Proposed tag target (not created) | `829110c33ba20b9a78d0eb6cb1b3580c7e1016bb` (parent `e149a34a30c5646129b1df401780305a7a530ec1`) |
| True Git tree ID | `fae08c96f18542a0207bfb39af42efecd92b095e` |
| Assembler-equivalent digest, committed blob bytes | `2979723f87b67482add704cebf51bbab9ff7486cd61af21aec3772fc837b11ea` (363 paths) |
| Path population | 365 tracked files in the tree minus the assembler's two excluded tracked paths (`docs/PLAN.md`, `docs/sprint-plans/sprint-10.md`) = 363 |
| Current worktree, full assembler algorithm | 434 paths / `f1916d561abe632ff98bf526e48e5b6b22533edebc447746d42206d98a2cdce4` |
| Current worktree restricted to the 363 proposed-tag paths | `1e82cae3db64c508b6d414f9be9dd10c8a516298d2bb7924f2a28c02d4651310` |

Algorithm: the assembler's exact `git ls-files --cached --others --exclude-standard`, anchored exclusion regex `^(artifacts/|runtime/|dist/|build/|out/|\.git/|docs/PLAN\.md$|docs/sprint-plans/sprint-10\.md$|scripts/sprint-6/__pycache__/)`, `Sort-Object -Unique`, `<sha256>  <path>` lines joined by `\n`, UTF-8 SHA-256; for the proposed tag the path source is `git ls-tree -r --name-only 829110c3` and each file's bytes are the committed blob (`git archive` materialization, byte-identical to `git cat-file blob`), not the live worktree.

### Exclusions from the proposed tag (all preserved byte-for-byte)

- Seven uncommitted tracked hunks: `.agents/memory/decisions.md`, `AGENTS.md`, `docs/META-BRAIN.md`, `docs/PLAN.md`, `docs/REQUIREMENTS.md`, `docs/ROADMAP-2.0.md`, `docs/sprint-plans/sprint-18.md`. All are documentation/planning records, not build inputs; none was staged, committed or altered.
- 71 untracked paths included in the assembler digest: `cl.diff` (read-only worktree-vs-HEAD capture of `CHANGELOG.md`, created 13:23), `scripts/sprint-11/__pycache__/S11-T1-Corpus.cpython-314.pyc`, and 69 nested `runtime/fixtures/.../a11-final-build/` DLL/EXE/PDB/JSON verification copies under `src/` and `tests/` (owner fixture `S14-T3-owner-b23ce28e30ff468a8faf2028b89256c7`). A 72nd untracked file, `scripts/sprint-6/__pycache__/S6-T4-FixtureCustodian.cpython-313.pyc`, is excluded by the assembler regex itself. None is tracked by the proposed commit or is a build/package input to the 1.8.0 candidate.

### Build-input comparison — no blocker

- `2ce06fb8…` (assembly) → `6fa0f946…` (stabilized): recorded by `S18-T6-C1/R2` as exactly six A2 documentation edits (`docs/release-1.8.0.md`, `docs/release-notes-1.8.0.md`, `README.md`, `CHANGELOG.md`, `docs/OPERATIONS.md`, `docs/release-contract.md`); no compiled input changed.
- All 13 paths in the R2 reviewed manifest match the proposed commit's committed blob hashes 13/13, including both T6 build-affecting paths (`Directory.Build.props` version, `scripts/release/assemble-candidate.ps1` label-only edit).
- Proposed commit tree vs live worktree on the 363 shared paths: 313 byte-identical, 44 line-ending-only (LF in blobs, CRLF materialized on disk — all pre-existing C# sources plus three old sprint docs and two `scripts/s8-t3` scripts), six foreign hunks (docs/memory), **zero real content differences**. Git reports these files clean (`i/lf w/crlf`, no attributes, `core.autocrlf=false` locally); the assembler consumed the CRLF worktree bytes, which are unchanged.
- After `6fa0f946…` only two non-build files changed: `cl.diff` (new at 13:23) and `docs/sprint-plans/sprint-18.md` (gate-record edit at 13:52). Git-ignored pinned inputs keep their recorded hashes and are outside the digest.
- Conclusion: compiled/package-affecting inputs are equivalent to both recorded snapshots at the worktree-materialized byte level; line-ending differences are checkout materialization, not a source change. No rebuild, reassembly or substitution was performed, and no evidence was inferred where a measurement was available.

## Gate status and publication sequence

Completed: `S18-T6-C1/R2` PASS, checkpoint `S18-T6-CP1` at `829110c3…`, final differential `S18-DEEP-D3` PASS. Publication is **pending separate owner authorization**; 1.7.0 stays the latest public release and the state remains `release_pending`.

Owner-authorized sequence (S17 pattern, `artifacts/sprint-17/publication/S17-PUBLISH-P1.md`; nothing below was executed here):

1. Preflight: confirm `origin` `https://github.com/triet4p/my-meta-brain.git`, owner account, no `v1.8.0` tag/release and no asset-name collision, fast-forward of `refs/heads/main` to `829110c3…`, and `-VerifyOnly` `VERIFIED` against the frozen candidate.
2. Create annotated tag `v1.8.0` → `829110c33ba20b9a78d0eb6cb1b3580c7e1016bb` (message `Meta Brain 1.8.0`), push tag and push `main` if needed.
3. Create GitHub Release `v1.8.0` at `https://github.com/triet4p/my-meta-brain/releases/tag/v1.8.0` (not draft/prerelease, Latest), body from `docs/release-notes-1.8.0.md` in published framing.
4. Upload exactly the two candidate files unchanged: `MetaBrain-CLI-1.8.0-win-x64-e149a34a30c5-tree2ce06fb85f6a-2bc84743d74c.exe` (234,638,554 B / `f977aada…`) and `MetaBrain-MCP-1.8.0-win-x64-e149a34a30c5-tree2ce06fb85f6a-2bc84743d74c.zip` (90,183,118 B / `53080c42…`), to `https://github.com/triet4p/my-meta-brain/releases/download/v1.8.0/`. Any shortened public filename (S17 used `MetaBrain-CLI-1.7.0.exe` via a separate asset-name patch) is a separate owner decision, not assumed here; bytes and identity `2bc84743d74c` = `e149a34a30c5-tree2ce06fb85f6a` must never be relabelled or substituted.
5. Download the public URLs and verify exact `Content-Length` and SHA-256, plus links/version. Then re-pin `scripts/site/build.ps1` from the 1.7.0 candidate directory and download base URL to the 1.8.0 identity, build the site, deploy to `gh-pages`, and run the public byte/link/marker smoke (`https://triet4p.github.io/my-meta-brain/`).
6. Public release-boundary review (S18-PUBLIC-C1 pattern) before Main marks Sprint 18 Complete.

Proof boundaries: an installed-host 1.7.0 → 1.8.0 upgrade mutates an owner install/data root (forbidden host mutation); the owner-only alternative is the isolated synthetic-vault lifecycle under repository-local ignored roots with explicit authorization, never the owner installation, profile, registry or service. Normal Codex/OMP client/provider sessions need fresh owner consent and network access; browser/WPF replay, tag/release/upload and `gh-pages` deployment are owner-only actions, not assumed or performed here.

See [the 1.8.0 migration guide](migration-1.8.0.md) for opt-in eligibility, compatibility, known limits, deferred G2 removals and the T1–T5 evidence map, and [the release notes](release-notes-1.8.0.md) for the user-facing scope.
