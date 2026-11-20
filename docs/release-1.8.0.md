# Meta Brain 1.8.0 — exact local candidate record

**Status: exact local candidate assembled and verified; not published.** 1.7.0 remains the latest published release (`v1.7.0`, 2026-11-18). The two 1.8.0 assets below were produced once by the unchanged guarded assembler and independently checked. No installed-host upgrade, tag, upload, deployment or publication was performed; independent evidence review, the exact-snapshot checkpoint and the final differential remain separate gates.

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

Documentation recorded after assembly (this record, the task artifact and corrected status sentences) is not compiled into either asset; it changes the live tree digest away from `2ce06fb…`. That is expected documentation-only drift, not a different candidate. The final Bronze exact-snapshot checkpoint must bind the stabilized final working tree, whose exact digest is reported in `artifacts/sprint-18/task-6.md`; it must not be confused with the assembly-instant identity. An ordinal-sorted recomputation yields a different digest and is not the assembler's algorithm.

## Next gates

Independent evidence review `S18-T6-C1/R2`, a fresh Bronze exact-snapshot checkpoint, the end-of-sprint differential and separate owner publication authorization all remain outstanding. 1.7.0 stays the latest public release and the state is `release_pending` until then. See [the 1.8.0 migration guide](migration-1.8.0.md) for opt-in eligibility, compatibility, known limits, deferred G2 removals and the T1–T5 evidence map, and [the release notes](release-notes-1.8.0.md) for the user-facing scope.
