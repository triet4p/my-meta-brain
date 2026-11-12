# Meta Brain 1.5.0 — release record

**Status: published 2026-11-09 as `v1.5.0`.** The owner authorized publication after the reviewed
candidate, evidence review, exact-snapshot checkpoint and final differential; the public assets were
uploaded from the exact frozen candidate bytes under the public names below, and the sizes and
SHA-256 values are recorded here. The reviewed `main` through
`05ca71d1e3823b51edca298772f916fe7a98b76f` was pushed, the annotated tag `v1.5.0` and the
GitHub Release were created, the static site was rebuilt against the published downloads and
deployed, and the public assets and links were smoke-verified from the live URLs. The reviewed
candidate checkpoint remains `fe9e585e1aa02107ebd2311e913ffaade47740a1`; the publication tag points
at `05ca71d1e3823b51edca298772f916fe7a98b76f` and no candidate bytes were rebuilt or repacked.
Independent public-evidence review and the fresh Bronze checkpoint remain separate records. If a
file claimed to be a 1.5.0 download does not match the size and SHA-256 recorded here, treat it as
not verified and report it instead of guessing.


This is the prepublication release record for the two Meta Brain 1.5.0 products: the CLI setup and
the standalone MCP server package. Sprint 15 accepted the encrypted structure-aware passage index
(S15-T2), scoped hybrid ranking (S15-T3), purpose-aware recall and regions (S15-T4), the CLI search UX
with background refresh and exact-revision open (S15-T5), and this release task (S15-T6). The S15-T6
evidence review passed with zero actionable findings, the exact-snapshot checkpoint committed the
reviewed content as `fe9e585e`, and the final sprint differential passed with zero actionable
findings. No provider, model or official-client walkthrough is re-run or re-claimed here; no product
source changed for this publication delta.

## Frozen candidate identity

| Field | Value |
| --- | --- |
| Product and version | `Meta Brain 1.5.0` for both products |
| Candidate ID | `100079356d73` |
| Candidate identity | `c42911920f7d-tree1f1b68fec135` |
| Platform / runtime | Windows x64 (`win-x64`), self-contained |
| Assembly base HEAD recorded in the manifest | `c42911920f7d522532b98141588b05c05eba9dd8` |
| Assembled source-tree SHA-256 | `1f1b68fec13567718557907e7842d0958f545e6ec28d6796f914db114e0565fb` |
| Assembled at (UTC) | `2026-11-09T04:31:21.7993695Z` |
| Signing | Unsigned; SHA-256 verifies bytes, not publisher identity |
| Reviewed checkpoint commit | `fe9e585e1aa02107ebd2311e913ffaade47740a1` on `main`, parent `c4291192` |

The candidate was assembled from the reviewed working tree while the branch HEAD was still
`c4291192`, so the manifest records that pre-commit HEAD with `workingTreeIncluded: true` and the
tree digest above. The S15-T6 checkpoint then committed exactly the reviewed candidate scope (22
task-owned paths) as `fe9e585e`, and its record confirms the committed content matched the reviewed
manifest for all of them. Those two identities are kept distinct on purpose: the manifest identity
binds the frozen candidate bytes, while `fe9e585e` is the reviewed commit that carries the same
content plus its evidence-referenced documentation. The candidate is **not** rebuilt to make a tag
identity look cosmetic, and the publication tag is expected to point at the reviewed publication
commit carrying this delta, not at the assembly-instant HEAD.

## Assets and download-name mapping (published)

| Product | Planned public asset name | Internal candidate filename | Bytes | SHA-256 |
| --- | --- | --- | ---: | --- |
| CLI setup | `MetaBrain-CLI-1.5.0.exe` | `MetaBrain-CLI-1.5.0-win-x64-c42911920f7d-tree1f1b68fec135-100079356d73.exe` | 233,550,554 | `cc4c05d5d9bf73247dbca640f2b9c468c6f7b2d4340ea99f84664f7dad62aefd` |
| MCP package | `MetaBrain-MCP-1.5.0.zip` | `MetaBrain-MCP-1.5.0-win-x64-c42911920f7d-tree1f1b68fec135-100079356d73.zip` | 89,848,853 | `5ead1bc922596e719e158cf523eb6d4939bee9a32b368c7e03ecc28e117f9b94` |

Published 2026-11-09 in the GitHub Release
[`v1.5.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.5.0). The public download URL for
each asset is `https://github.com/triet4p/my-meta-brain/releases/download/v1.5.0/<public asset name>`;
the public files are byte copies of the frozen candidate assets below.

The setup embeds a 134,776,841-byte payload with SHA-256
`66efd1e3cf7054f9f4a02c84b10c1604dc4dbc23e63c8aa40d4ba0377d12e532` and 626 payload members; the
MCP ZIP has 421 members. The full per-file inventory, embedded-payload membership, dependency and
license inventories, native payload identity and prerequisite metadata are in the candidate
`candidate-manifest.json`. Candidate assembly ran
`powershell.exe -NoProfile -NonInteractive -File scripts/release/assemble-candidate.ps1` (exit 0,
`VERIFIED`/`CANDIDATE`), and `-VerifyOnly` re-verified the same candidate directory with exit 0.
The setup's own `--verify-payload` reported the exact payload size, hash and member count; the MCP
ZIP members and the native DLL were independently checked against the manifest.

The public names above are the download-name mapping a later owner-authorized publication would
use, following the `MetaBrain-<product>-<version>.<ext>` contract. **No `v1.5.0` tag, Release or
`https://github.com/triet4p/my-meta-brain/releases/download/v1.5.0/<name>` URL is live, and none may
be described as live before the owner publishes it.** The public files must be byte copies of the
frozen candidate assets. There is no 1.5.0 Owner UI asset, no `owner-ui` component and no separate
document-console download; neither public name may be reused for a UI build. The published
1.0.0–1.4.0 tags, Releases and assets are not rebuilt, renamed or withdrawn by this release.

The git-ignored candidate directory
`runtime/release-candidates/1.5.0-win-x64-c42911920f7d-tree1f1b68fec135-100079356d73/` holds the two
assets and the manifest. A publication release additionally carries `candidate-manifest.json`, a
`SHA256SUMS.txt` generated from the two asset hashes and the repository `LICENSE`, matching the
1.4.0 release-asset convention. None of these local files is a download page or publication
approval.

## Product shape and included behavior

The release contract remains the CLI setup plus the standalone MCP ZIP over one shared runtime. The
frozen 1.1.0 WPF Owner UI is not a 1.5.0 product, no new WPF installer is built, and no separate
browser console exists. Product version metadata is single-sourced in `Directory.Build.props`
(1.5.0 / 1.5.0.0); the WPF source stays independently pinned at 1.1.0.

- **Encrypted structure-aware passage index (S15-T2).** `owner workspace-index-status`,
  `workspace-index-rebuild` and `workspace-index-anchors` build and read one authenticated
  AES-GCM-encrypted posting/semantic generation from the unlocked workspace. Anchors cite the exact
  file, content revision and byte-range digest; Markdown headings/fenced code, CSV headers and
  complete quoted records, known/raw JSONL records and plain-text passages are indexed. Rebuild
  revalidates every revision and publishes atomically; failures preserve the prior generation.
- **Scoped hybrid ranking (S15-T3).** `owner workspace-search --hybrid true` combines retained
  exact/lexical/heading evidence with local semantic candidates over the authenticated generation.
  Folder, field, currentness and content-date filters apply before totals are disclosed. Output
  separates `literal_match` from `semantic_candidate`; a semantic candidate is an unconfirmed
  neighbour, **not supporting evidence or an answer**. The lexical command remains a separate
  explicit path and `--hybrid true` never silently degrades to literal-only output.
- **Purpose-aware recall and regions (S15-T4).** `--purpose` accepts `auto`, `experience`, `temporal`,
  `intention`, `action-effect` and `general`; it applies deterministic rule-first routing after the
  existing filters and joins annotations, lifecycle and recall state on exact source revisions,
  returning region representatives, scoped counts and selection reasons. Action/effect associations
  do not establish causality; intention mentions and acknowledgements do not establish completion.
- **Search UX, background refresh and exact open (S15-T5).** Successful owner content mutations
  queue one coalesced background passage refresh inside the unlocked epoch; lock cancels it, clears
  the model selection and decoded cache, and publishes nothing partial. `--open-rank N` opens a
  returned hit at its exact file, content revision and byte anchor through the existing document
  bridge, refusing stale or incomplete locations. The browser remains an optional document-only
  preview/edit surface, not a search console.
- **MCP agent surface unchanged.** Stdio transport, exactly one of `--data-root` or `--config`, the
  existing 22-tool scoped agent API, and no owner workspace-search operation. The packaged manifest
  declares protocol versions `2025-11-25`, `2025-06-18`, `2025-03-26` and `2024-11-05`; the packaged
  consumer run observed `initialize` negotiating `2025-11-25`.

## Explicit local model and external runtime prerequisites

Hybrid indexing and search require an owner-selected local model directory for the pinned E5 revision
`614241f622f53c4eeff9890bdc4f31cfecc418b3`, ending in `model/`. The two verified runtime inputs are
`onnx/model.onnx` (470,268,510 bytes; SHA-256
`ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665`) and `onnx/tokenizer.json`
(17,082,730 bytes; SHA-256
`0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39`). The seven approved
model/tokenizer/config assets total 492,421,754 bytes in the evaluation staging set — a measured
staged size, not an installed-product footprint. The product does not bundle model weights, download
them, call a cloud embedding provider, persist the selected path or choose a model implicitly. Missing
or invalid assets fail with `workspace_index_model_missing` / `workspace_index_model_invalid`, never a
lexical-only substitute; the model directory and decoded passage reuse are process-memory-only for the
unlocked epoch.

The selected dynamic native tokenizer is the source-built `hf_tokenizers.dll` (3,198,464 bytes;
SHA-256 `4595800ae02f9c7c96d583347e6cf1e2ddd17c26348ac8169c8314ca28b7480d`) from pinned upstream
commit `b8eaa77d72bf9d9c768d75599bfa6a602e467b93` of
`https://github.com/sappho192/Tokenizers.DotNet`. Versioned product source carries the generated
`Cargo.lock` (86 packages: upstream wrapper + 85 registry crates; SHA-256
`7597e8861f7368d02ea5daded9b71cbc226286ffbf1bbe7ad57f3d719b53d48f`), the build provenance record
and the complete mapped third-party notices (172 license documents, including vendored Oniguruma and
mimalloc and the build-only `csbindgen` MIT text; 219,767 bytes; SHA-256
`1d67c3d687bc74fcd4226ce4288896938a903542c16b02faaf8ae866d1f6f94e`). The exact native DLL, its
source archive and generated inventories stay in ignored repository-local staging by design. Notice
coverage is evidence, not a legal conclusion or proof that every attribution obligation is closed.

The dynamic PE imports `VCRUNTIME140.dll` and `VCRUNTIME140_1.dll` and does not include them. The
package therefore requires an **owner-installed** Microsoft Visual C++ v14 x64 Redistributable at
version **14.43.34808 or later**. CLI setup, direct install and MCP startup read only the installed
x64 version and refuse before payload extraction, application/data-root changes or service changes
when it is missing, unreadable, malformed or too old, pointing the owner to Microsoft's
[latest supported downloads](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170).
Windows 10+ includes the OS Universal CRT, which does not supply these VCRUNTIME imports. Meta Brain
does not fetch, bundle, install or chain the prerequisite; it is a separate owner action, and the
`System.Numerics.Tensors/10.0.14` notice mapping (pinned MIT `LICENSE.TXT`, separate package
copyright and packaged `THIRD-PARTY-NOTICES.TXT`) is part of candidate assembly.

## Accepted evidence and measured limits

The inherited S15-T1–T5 observations are accepted only on their assigned surfaces; S15-T6 results are
worker evidence reviewed by the independent evidence gate, not a legal conclusion.

- **S15-T1 standalone model screen:** 25/27 expected sources at rank 5; seven pinned
  model/tokenizer/config assets totaling 492,421,754 bytes; process-private boundary delta
  559.1953 MiB; warm in-process p95 17.8471 ms after 10 warmups and 100 searches. These passed the
  approved evaluator-specific screen and do not set integrated CLI/service limits.
- **S15-T3 actual owner CLI/service:** 35 held-out synthetic queries over 114 files returned all 27
  labeled expected-evidence cases (13 literal-baseline, 14 paraphrase/near-miss; two paraphrase
  results ranked second, the rest first). Four exact/no-evidence and four folder-scope negatives
  returned no results; unrelated prose still returns explicit semantic candidates. See
  [S15-T3 evidence](../artifacts/sprint-15/task-3.md).
- **S15-T5 integrated observations:** 112 synthetic files, 10 warmups and 100 fresh CLI-to-service
  queries measured nearest-rank p50/p95 291.65/379.81 ms; the first post-locked-restart query took
  2,365.89 ms including model reload. Query-boundary private bytes were 810,008,576 before and
  826,810,368 maximum/after; working set 851,689,472 before and 863,821,824 maximum/after. These are
  observations under the recorded conditions, not integrated SLAs. See
  [S15-T5 evidence](../artifacts/sprint-15/task-5.md).
- **S15-T6 selected-payload source CLI/service smoke:** the Debug smoke project rebuilt with no
  restore (0 warnings/errors), then the frozen `s15-t3` scenario ran against the Debug CLI and shared
  host with the selected dynamic tokenizer. All 35 frozen queries passed, including 27/27 labeled
  evidence, the four exact/no-evidence and four folder-scope negatives,
  filters/ties/dedup/currentness/anchors/lock/cancellation/stale-generation checks, and
  missing/invalid-model refusal with no silent fallback. First post-locked-restart query 6,438.13 ms;
  after 10 warmups and 100 timed searches p50/p95 519.10/837.26 ms; private bytes 842,612,736 before
  and 844,554,240 maximum; working set 873,529,344 before and 876,802,048 maximum; lifetime peak
  working set 1,254,805,504. Synthetic host observations, not SLAs or package proof.
- **Packaged consumer and lifecycle proof (A10, reviewed):** the extracted candidate MCP negotiated
  `protocolVersion: 2025-11-25`, reported `serverInfo` version 1.5.0, listed 22 tools and served
  catalog operations; the committed `s4-t1` smoke passed after the restored initialize field. An
  isolated 1.4.0 → 1.5.0 upgrade changed only `data-root-owner.json`, kept every pre-upgrade canonical
  vault file, settings and pipe endpoints byte-identical, preserved the 162-byte synthetic workspace
  file, its anchor, memory item and vault unlock (passphrase and recovery key), refused a second
  installation sharing the data root before any write, and stopped cleanly. No host mutation or owner
  data was used.

Unchanged accepted gates are inherited by trace to their prior task/review/checkpoint records; they
are not a blanket 1.0.0 replay, and the T1 numbers stay confined to their evaluator surface.

## Known limits and publication boundary

- The tests are synthetic and host-specific. Recall quality on owner data, large workspaces and
  every query family is not established, and hybrid output remains a candidate list rather than
  verified facts; there is no approved cosine cutoff or general natural-language abstention claim.
- Latency/RAM figures above are observations, not support promises. The first query after a locked
  restart reloads the model. Model acquisition and placement are owner-controlled and are not
  product downloads.
- The VC++ v14 x64 runtime is an external owner prerequisite; the interactive setup-GUI refusal on a
  runtime-absent machine was verified at code-path/registry level, not as a full clean-machine
  installation.
- Lifecycle evidence is limited to disposable repository-local roots. No ACL, registry, service
  registration, system/global environment, profile change or owner live root/service was used.
- The optional `open` document session needs an installed Chromium-based browser; it stays a
  document-only surface and this release adds no search console.
- Candidate setup/MCP bytes, manifests and site previews are ignored local build outputs. They are
  not distribution approval, a published Release or a legal conclusion about third-party terms.

## Publication checklist (not executed)

Retained as the ordered procedure for the owner-authorized publication, which follows a fresh
independent review of this delta, a fresh Bronze exact-snapshot checkpoint and Main's explicit
execution release. Publication was executed on 2026-11-09 as `v1.5.0`; see
`artifacts/sprint-15/publication/S15-PUBLISH-P2.md`. The status is now `published`; independent
public-evidence review [`artifacts/sprint-15/reviews/S15-PUBLIC-C1-R1.md`](../artifacts/sprint-15/reviews/S15-PUBLIC-C1-R1.md) (PASS) and the fresh Bronze checkpoint [`artifacts/sprint-15/checkpoints/S15-PUBLIC-C1.md`](../artifacts/sprint-15/checkpoints/S15-PUBLIC-C1.md) (`9780091f`) are complete.

1. Re-verify the candidate directory, both asset sizes/SHA-256 and the manifest, and re-run
   `powershell.exe -NoProfile -NonInteractive -File scripts/release/assemble-candidate.ps1
   -VerifyOnly -CandidateDirectory runtime/release-candidates/1.5.0-win-x64-c42911920f7d-tree1f1b68fec135-100079356d73`
   (expect `VERIFIED`). Do not rebuild, change the manifest or reuse a historical asset name.
2. Collision-check tag `v1.5.0`, its Release, `MetaBrain-CLI-1.5.0.exe`,
   `MetaBrain-MCP-1.5.0.zip`, `candidate-manifest.json`, `SHA256SUMS.txt` and `LICENSE`. Any
   existing target stops the operation; never overwrite an old tag or asset and never force.
3. Push the reviewed `main` (fast-forward from `origin/main` `bf6e8bc` through the reviewed Sprint 15
   commits and the reviewed publication commit), create annotated tag `v1.5.0` on that reviewed
   commit, create the GitHub Release, upload the exact candidate bytes under the public names (copy,
   never rename the candidate files) and set the release body from `docs/release-notes-1.5.0.md`.
4. In this record, change the status block to published, fill the public URL cells and the
   publication date, and record the observed public smoke. In `README.md`, change the 1.5.0
   prepared-candidate paragraph to published with the release link. In `CHANGELOG.md`, insert
   `## [1.5.0] - <publication date>` under `[Unreleased]` and move only the 1.5.0 entries under it.
5. Build the published site with
   `powershell.exe -NoProfile -File scripts/site/build.ps1 -ReleaseDownloadBaseUrl https://github.com/triet4p/my-meta-brain/releases/download/v1.5.0`,
   deploy the six site files to the `gh-pages` branch (Pages is legacy `gh-pages /`, HTTPS
   enforced), and confirm Pages builds that commit.
6. Public smoke: download both public assets and compare exact sizes and SHA-256 (independent
   hashing plus GitHub asset digests), run the downloaded setup's `--version` without installing,
   extract the downloaded MCP ZIP completely and run `mcp\MetaBrain.Mcp.exe --version`, confirm the
   site pages return 200 with the 1.5.0 published framing, both 1.5.0 download actions, the
   documented model/runtime prerequisites and the unchanged historical 1.0.0–1.4.0 links, and confirm
   the MCP protocol/version behavior is unchanged on the public bytes. Record the observed results,
   not a local candidate claim; the public proof is distinct from the local build evidence.

## Record ownership

This record, the candidate manifest and task evidence are the authority for candidate bytes and
behavior. It is not an independent review, checkpoint record, publication execution or public
proof. Evidence review, checkpoint ownership and Main's status remain separate records.
