# Meta Brain 1.6.0 — release record

**Status: published.** Meta Brain 1.6.0 was published on 2026-11-12 as the GitHub Release
[`v1.6.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.6.0) with the two byte-identical
candidate assets, and the public site was deployed from `gh-pages` commit `812ad24`. This is the
Sprint 16 release record: the assets were assembled from the Sprint 16 working tree, verified
locally, exercised on their actual packaged surfaces and previewed on the local site build, then
published after the independent evidence review PASS, exact-snapshot checkpoint, final differential
PASS and owner publication authorization. The public download, hashing, launch and live-site
observations are in
[`artifacts/sprint-16/publication/S16-PUBLISH-P1.md`](../artifacts/sprint-16/publication/S16-PUBLISH-P1.md).
If a file claimed to be a 1.6.0 download does not match the size and SHA-256 recorded here, treat it
as not verified and report it instead of guessing.

Sprint 16 delivered the owner-approved workspace relation contract (`D107`): typed relations between
stable file/folder/passage endpoints (T2), bounded relational candidate expansion (T3), relational
ranking and explanations (T4), the owner CLI relation workflow and exact endpoint filter (T5), and
this release task (T6). No provider, model or official-client walkthrough is re-claimed here; the
normal OMP client check below used only synthetic fixture data and a project-local configuration.

## Frozen candidate identity

| Field | Value |
| --- | --- |
| Product and version | `Meta Brain 1.6.0` for both products |
| Candidate ID | `a98342690266` |
| Candidate identity | `baee8fbbc2bc-tree21729ac0ac68` |
| Platform / runtime | Windows x64 (`win-x64`), self-contained |
| Assembly base HEAD recorded in the manifest | `baee8fbbc2bc57834875eacf5b71e40ab0c849f1` |
| Assembled source-tree SHA-256 | `21729ac0ac68772d809a1d5f9028f87b870a24f0a32e8d5f98f5c95ef14d172b` |
| Assembled at (UTC) | `2026-11-12T11:37:43.6957500Z` |
| Signing | Unsigned; SHA-256 verifies bytes, not publisher identity |
| Reviewed checkpoint commit | `d131e406ea03c388fbd4290b1bb25d06665336c8` (exact-snapshot checkpoint `S16-T6-CHECKPOINT-C1`) |
| Publication tag / commit | Annotated tag `v1.6.0` object `6ae93266dff8e70e1e5f092311daef0676842d7c` → commit `d131e406ea03c388fbd4290b1bb25d06665336c8` |
| GitHub Release | `v1.6.0`, title `Meta Brain 1.6.0`, id `442912886`, published `2026-11-12T12:26:56Z`, `Latest`, not draft/prerelease |
| Site deployment | `gh-pages` commit `812ad24362d46f006fa8e7b574efc0f44255d3b9`; Pages build `built` for that commit |

The candidate was assembled while the branch HEAD was `baee8fbb` and the working tree carried the
Sprint 16 source/docs changes, so the manifest records that HEAD with `workingTreeIncluded: true` and
the tree digest above. That manifest identity binds the frozen candidate bytes and the source snapshot
they were built from. It is distinct from the exact-snapshot checkpoint and the publication tag: the
source checkpoint and tag point at the reviewed `d131e406` commit carrying this delta, and the
candidate was **not** rebuilt to make the tag identity look cosmetic.

This release record and the site-builder candidate pin were completed after assembly; they are
documentation and tooling, are not compiled into either asset, and do not change the frozen
candidate bytes. The manifest source-tree digest therefore describes the assembly-instant
product/documentation tree, not later record-only edits. The same applies to this published-status
delta: the published tag `v1.6.0` points at `d131e406`, whose copies of these documents still carry
the prepared-candidate framing; the status correction below is a post-publication documentation edit
and changes no published byte.

The repository-local toolchain had advanced to SDK 10.0.404 / runtime 10.0.15 while the approved
repo-local NuGet feed still carried 10.0.13/10.0.14. To keep the accepted 10.0.14 runtime inventory
unchanged, the missing `Microsoft.NETCore.App.Host.win-x64/10.0.14` package was restored into that
existing repository-local feed (no system SDK, pack, profile or configuration was installed or
changed) and assembly ran with `RuntimeFrameworkVersion=10.0.14`. The resulting manifest reports
`Microsoft.NETCore.App.Runtime.win-x64 10.0.14`, matching the published 1.5.0 inventory.

## Assets and download-name mapping (published)

| Product | Public asset name | Internal candidate filename | Bytes | SHA-256 |
| --- | --- | --- | ---: | --- |
| CLI setup | `MetaBrain-CLI-1.6.0.exe` | `MetaBrain-CLI-1.6.0-win-x64-baee8fbbc2bc-tree21729ac0ac68-a98342690266.exe` | 233,687,770 | `3cd856e4f90cb6671e12f39c3577f8a6b1f3fc1103384c75dbe2713469870848` |
| MCP package | `MetaBrain-MCP-1.6.0.zip` | `MetaBrain-MCP-1.6.0-win-x64-baee8fbbc2bc-tree21729ac0ac68-a98342690266.zip` | 89,897,430 | `7f50bcf0c016373c0a6155b4978a5e72618c5b69d6aba74f7e0d4866cdaa539a` |

The published `v1.6.0` Release carries these five assets: the two product assets above,
`candidate-manifest.json` (439,253 bytes, SHA-256 `df6e886d…`), `SHA256SUMS.txt` (180 bytes, SHA-256
`be9341ff…`, the 1.4.0/1.5.0 two-line convention) and `LICENSE` (1,064 bytes, SHA-256 `b623a3c4…`).
Each uploaded product asset is a byte copy of the frozen candidate file; `candidate-manifest.json`
and `LICENSE` are byte copies of the candidate files of the same name, and `SHA256SUMS.txt` was
generated from the two asset hashes. The API asset digests matched those bytes before the Release
was published.

The setup embeds a 134,849,679-byte payload with SHA-256
`b82223de7dd716eca1767437d23bf4a98d8727b6836ae4e761b5708b3fc7cf0c` and 626 payload members; the
MCP ZIP has 421 members. The full per-file inventory, embedded-payload membership, dependency and
license inventories, native payload identity and prerequisite metadata are in the published
`candidate-manifest.json`.

`powershell.exe -NoProfile -NonInteractive -File scripts/release/assemble-candidate.ps1` produced the
candidate and printed `VERIFIED`; an independent
`-VerifyOnly -CandidateDirectory runtime/release-candidates/1.6.0-win-x64-baee8fbbc2bc-tree21729ac0ac68-a98342690266`
run printed `VERIFIED` again. The setup's own `--verify-payload` reported the exact payload size, hash
and member count, and the extracted MCP `package-manifest.json` matched the candidate identity.

The public download URLs are live under the `MetaBrain-<product>-<version>.<ext>` contract:

- `https://github.com/triet4p/my-meta-brain/releases/download/v1.6.0/MetaBrain-CLI-1.6.0.exe`
- `https://github.com/triet4p/my-meta-brain/releases/download/v1.6.0/MetaBrain-MCP-1.6.0.zip`

There is no 1.6.0 Owner UI asset, no `owner-ui` component and no separate document-console download.
The published 1.0.0–1.5.0 tags, Releases and assets are not rebuilt, renamed or withdrawn.

The git-ignored candidate directory
`runtime/release-candidates/1.6.0-win-x64-baee8fbbc2bc-tree21729ac0ac68-a98342690266/` holds the two
assets and the manifest; it is local build staging, not the distribution copy. The publication release
copied the two assets under the public names and added `candidate-manifest.json`, `SHA256SUMS.txt`
and `LICENSE`, matching the 1.4.0/1.5.0 release-asset convention. The public bytes are authoritative;
the local directory is not a download page.

## Product shape and included behavior

The release contract remains the CLI setup plus the standalone MCP ZIP over one shared runtime. The
frozen 1.1.0 WPF Owner UI is not a 1.6.0 product, no new WPF installer is built, and no separate
browser console exists. Product version metadata is single-sourced in `Directory.Build.props`
(1.6.0 / 1.6.0.0); the WPF source stays independently pinned at 1.1.0.

- **Typed workspace relations (S16-T2).** `owner relation-create`, `relation-read`,
  `relation-accept`, `relation-reject`, `relation-unlink` and `relation-relink` operate on stable
  workspace file/folder/passage endpoints in the existing seven-kind canonical relation family.
  Semantic workspace endpoints pin the exact retained content revision and full-file or byte-range
  digest; a changed pin reports `needsReview`. `related_to` remains identity-only; imported-file and
  AI origins stay `proposed`; `derived_from`/`supersedes` cycles are refused across stored states;
  attributed prospective history and revision preconditions gate every act. Encrypted backup/restore
  preserves edges, pins and history; scoped owner export includes only edges whose endpoints and
  evidence are selected.
- **Bounded relational candidate expansion (S16-T3).** Owner hybrid search returns a separate
  `relationCandidates` collection from the returned direct-hit page, up to two stored hops and the
  request limit, following accepted/current edges in stored direction. Every file/passage endpoint
  must be in folder and exact-revision scope; folder endpoints are identity-checked without
  descendant expansion. Candidates never merge into `hits`/`total` and disclose no relation reason,
  traversal path, text or out-of-scope identity.
- **Relational ranking and explanations (S16-T4).** The same response carries one deterministic
  `rankedEvidence` list: direct literal evidence first, relation-derived evidence next, direct
  semantic-only evidence last. Why/decision questions order supports/contradictions/derived/applied
  evidence ahead of identity links and retain contradictions. Each relation entry exposes stable
  reason codes and its hop-by-hop stored path; duplicate links or alternate paths to one source
  select one path and add no rank or confidence. No score, newest-wins rule or link-count promotion
  exists.
- **Owner relation workflow and exact endpoint filter (S16-T5).**
  `owner relation-list --workspace-node-id` selects only edges naming that exact stable file,
  passage-file or folder endpoint, preserving provenance, pins and service-computed
  `current|needsReview`. `workspace-read --revision` reads a pinned endpoint, and the documented
  lifecycle (add → inspect → revise source → `needsReview` → unlink/relink undo) is CLI-complete.
  Relations remain separate canonical records; no workspace node is duplicated.
- **MCP agent surface unchanged.** Stdio transport, exactly one of `--data-root` or `--config`, the
  existing 22-tool scoped agent API, and no owner workspace-search operation. Agent relation calls
  are proposals and cannot accept or share owner workspace relations. The packaged MCP manifest
  declares protocol versions `2025-11-25`, `2025-06-18`, `2025-03-26` and `2024-11-05`.

## Dependencies, native payload and external prerequisite

The .NET publish dependency inventory is unchanged from 1.5.0 and recorded per product in the
candidate manifest: `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT),
`Microsoft.ML.OnnxRuntime` and `Microsoft.ML.OnnxRuntime.Managed` 1.31.1,
`System.Numerics.Tensors` 10.0.14 and `Tokenizers.DotNet` 1.4.1, plus the first-party project
references. Each asset carries `LICENSE`, per-dependency license texts and third-party notices.
Project license: MIT, `Copyright (c) 2026 triet4p`. Nothing here is a legal conclusion.

The selected dynamic native tokenizer remains the source-built `hf_tokenizers.dll` (3,198,464 bytes;
SHA-256 `4595800ae02f9c7c96d583347e6cf1e2ddd17c26348ac8169c8314ca28b7480d`) from pinned upstream
commit `b8eaa77d72bf9d9c768d75599bfa6a602e467b93`. Versioned source carries the generated
`Cargo.lock` (86 packages; SHA-256
`7597e8861f7368d02ea5daded9b71cbc226286ffbf1bbe7ad57f3d719b53d48f`), the build provenance record and
the complete mapped third-party notices (219,767 bytes; SHA-256
`1d67c3d687bc74fcd4226ce4288896938a903542c16b02faaf8ae866d1f6f94e`). The exact native DLL and source
archive stay in ignored repository-local staging by design. The dynamic PE imports `VCRUNTIME140.dll`
and `VCRUNTIME140_1.dll` and does not include them, so the owner-installed Microsoft Visual C++ v14
x64 Redistributable at version **14.43.34808 or later** remains a separate prerequisite. Setup,
direct install and MCP startup check it read-only and refuse before extraction, data-root or service
changes when it is missing or too old. Meta Brain does not fetch, bundle or install it.

## Verification actually performed

All checks below ran on this candidate's own packaged bytes or on the installed candidate in
repository-local fixture roots. Synthetic data only; no owner corpus, provider credential, ACL,
registry, service registration, global environment/profile setting or host mutation was used.

- **Candidate assembly and independent verification:** assembler `VERIFIED`; `-VerifyOnly`
  `VERIFIED`; setup `--version` printed `MetaBrain CLI Setup 1.6.0`; setup `--verify-payload`
  reported payload bytes `134849679`, SHA-256 `b82223de…`, member count 626; extracted
  `mcp/MetaBrain.Mcp.exe --version` printed `MetaBrain.Mcp 1.6.0`.
- **Packaged CLI/service relation lifecycle:** the permanent `s16-t5` scenario ran against the
  installed candidate `cli/MetaBrain.Cli.exe` and `service/MetaBrain.Connections.exe` and passed:
  file/passage and folder neighborhoods, provenance/review, exact pinned endpoint revisions,
  cognitive filters, no duplicate tree rows, source revise → `needsReview`, unlink/relink undo and
  lock denial.
- **Packaged relational expansion and ranking:** `s16-t3` passed the two-hop supports/contradicts
  discovery, exact hybrid counts, direction/currentness/staleness, scope non-disclosure, cycle,
  fan-out and lock cases. `s16-t4` passed combined ranking/explanations, contradiction retention,
  duplicate-path dedupe, limit bounds, currentness and CLI text rendering; its frozen why-case
  observation was `baselineDirectRelevant=0/7`, `rankedRelevant=7/7`, `baselineRecall=0.000`,
  `rankedRecall=1.000`, `delta=1.000`, `contradictionRetained=true`, with no threshold or confidence
  score asserted. Its resource observation was 3 cases, 1,029.60 ms elapsed, service private bytes
  36,753,408 before / 29,835,264 after and working set 78,483,456 / 74,194,944 — host- and
  fixture-specific, not a latency or memory gate.
- **Packaged MCP and API negatives:** `s16-t2` ran the actual CLI/service, the Connections agent
  bridge and the packaged `MetaBrain.Mcp.exe` stdio entrypoint. Agent claims stayed `proposed`; a
  workspace relation attempt was generically denied without endpoint disclosure; cycle, stale-pin,
  origin, export-withholding, backup/restore and rebuild preservation checks passed.
- **Isolated 1.5.0 → 1.6.0 upgrade and data preservation:** the published 1.5.0 setup installed into
  fresh repository-local sibling roots, provisioned a synthetic vault, imported exact synthetic
  workspace bytes and stored a canonical memory item; the 1.6.0 setup upgraded the same roots in
  place. The driver reported 44 PASS lines and `LIFECYCLE_DONE`: only `data-root-owner.json` changed
  at install, the marker records the 1.6.0 candidate on the same install root, service settings and
  both pipes are byte-identical, every pre-upgrade canonical vault file is byte-identical, the host
  restarted locked, a wrong passphrase was refused, the preserved passphrase and recovery key
  unlocked, workspace identity/bytes/search and the memory item were preserved, a second
  installation sharing the data root was refused before any write, and the owner created, accepted
  and listed a typed relation on the upgraded vault. Post-upgrade relation/catalog acts changed
  canonical data only as expected.
- **Packaged MCP protocol surface:** the extracted `mcp/MetaBrain.Mcp.exe` on a fresh synthetic
  data root negotiated `protocolVersion` `2025-11-25`, reported `serverInfo` version `1.6.0`, listed
  exactly 22 tools and served `metabrain_catalog_list` as `CATALOG no_match`, then exited 0 and the
  isolated host stopped. The tool list contains no owner workspace-search operation; the valid
  agent relation-call denial is exercised by `s16-t2` above.
- **Normal OMP client:** OMP 18.19.0 ran with a project-local `.omp/mcp.json` containing only the
  absolute extracted `mcp/MetaBrain.Mcp.exe` path and `--data-root <isolated synthetic root>`, with
  `--no-session --no-extensions --no-skills --no-rules --no-context-files`. It exited 0 and returned
  the packaged MCP tool's raw result `CATALOG no_match`; the MCP-created isolated data root contains
  its settings, service log and pid. No global configuration, profile or permission was read as a
  credential source or changed, and no secret appears in commands or logs.
- **Local site preview and deployment:** `scripts/site/build.ps1` verified candidate version,
  provenance, both asset sizes and SHA-256 values, copied the two candidate bytes under the public
  names, and emitted the sanitized preview to ignored `dist/site/` with no unresolved placeholders.
  The publication build then deployed those files to `gh-pages` commit `812ad24`, whose `git
  hash-object` identities equal the local build outputs, and the Pages build for that commit
  reported `built`. The live site serves the published framing with both 1.6.0 download actions.

## Known limits and publication boundary

- The tests are synthetic and host-specific. Relation correctness/quality on owner corpora and very
  large relation graphs is not established; the disclosed why-case delta is one synthetic case, not
  an approved benchmark. Expansion is bounded to two stored hops and the request limit, and only
  accepted/current edges expand.
- Relation history is prospective: acts recorded by this implementation are attributed; earlier
  managed-relation states were not backfilled. A relation is attributed review state, not verified
  truth; no similarity score, confidence or newest-wins rule exists, and contradictions are retained
  rather than filtered.
- The MCP agent surface remains 22 tools; agent relation calls are proposals only. There is no owner
  workspace-search route, relation/graph workbench or new browser product.
- Hybrid retrieval still requires the owner-selected pinned local E5 model; the release bundles no
  model weights, downloads none and calls no cloud embedding provider. Semantic candidates remain
  unconfirmed neighbours, not evidence.
- The VC++ v14 x64 runtime is an external owner prerequisite; the interactive setup-GUI refusal on a
  runtime-absent machine was verified at code-path/registry level, not as a full clean-machine
  installation.
- Lifecycle evidence is limited to disposable repository-local roots. No ACL, registry, service
  registration, system/global environment, profile change or owner live root/service was used.
- The optional `open` document session needs an installed Chromium-based browser; it stays a
  document-only surface.
- The published two product assets are byte copies of the frozen candidate, so their evidence
  inheritance is exact; the ignored local candidate directory and site preview remain build staging,
  not a second distribution source. Release notes and site copy are presentation, not a legal
  conclusion about third-party terms.
- The reviewed site source listed historical release links only through `v1.4.0`, so the live site
  at `812ad24` omits the published `v1.5.0` link. `website/index.template.html` now lists
  `v1.0.0`–`v1.5.0`; this delta does not deploy the corrected source, and the public pages keep the
  gap until a scoped site republish.
- The published tag `v1.6.0` still carries the prepared-candidate framing in its copies of the
  documentation. This post-publication status correction is an uncommitted documentation delta and
  does not rewrite the tag, Release or any asset.

## Publication execution and public smoke (observed 2026-11-12)

The owner-authorized publication ran after the review PASS and checkpoint. The detailed observation
log is
[`artifacts/sprint-16/publication/S16-PUBLISH-P1.md`](../artifacts/sprint-16/publication/S16-PUBLISH-P1.md).

| Effect | Observed |
| --- | --- |
| `git push origin main` | `9780091..d131e40` (`main` → `d131e406ea03c388fbd4290b1bb25d06665336c8`) |
| Annotated tag | `v1.6.0` object `6ae93266dff8e70e1e5f092311daef0676842d7c` → commit `d131e406…`, pushed; remote `git ls-remote` dereferences to `d131e406…` |
| GitHub Release | `v1.6.0`, title `Meta Brain 1.6.0`, id `442912886`, published `2026-11-12T12:26:56Z`, `Latest`, not draft/prerelease |
| Release assets | the five files above; sizes and SHA-256 values matched the candidate record and the API asset digests before publish |
| `gh-pages` | `4d04c86..812ad24` → `812ad24362d46f006fa8e7b574efc0f44255d3b9`; Pages build `built` for that commit (`2026-11-12T12:27:16Z`) |
| Public HTTP | both download URLs `302 → 200` with the exact `Content-Length` of each asset |

Public smoke ran on the downloaded public bytes, not the local candidate path:

1. All five public files were downloaded and hashed; every size and SHA-256 value equals the
   candidate record and the GitHub asset digest.
2. Downloaded CLI setup: `--version` printed `MetaBrain CLI Setup 1.6.0`; `--verify-payload` reported
   payload bytes `134849679`, SHA-256 `b82223de…`, 626 members. No installation, registry, service,
   profile or data-root change was made.
3. Downloaded MCP ZIP extracted completely; `mcp/MetaBrain.Mcp.exe --version` printed
   `MetaBrain.Mcp 1.6.0`; `package-manifest.json` reports product `Meta Brain MCP`, version `1.6.0`,
   candidate `a98342690266`, source HEAD `baee8fbb…`, tree `21729ac0…`.
4. Public MCP stdio exchange on a fresh isolated synthetic data root: `initialize` negotiated
   `protocolVersion 2025-11-25` with `serverInfo` version `1.6.0`; `tools/list` returned exactly 22
   tools with no owner workspace-search tool; `metabrain_catalog_list` returned `CATALOG no_match`
   with `isError: false`; the server exited 0 on stdin EOF.
5. Live site: `index.html`, `scenarios.html`, `codex.html`, `omp.html` and `operations.html` all
   returned 200 with no unresolved placeholders; the index shows the published 1.6.0 framing, both
   download actions target the exact `v1.6.0` release URLs, and the pinned model and VC++ 14.43.34808+
   prerequisites are present.
6. Deployed file identity: `git hash-object` of all six live `gh-pages` paths at `812ad24` equals the
   corresponding `dist/site` build outputs.

The downloaded setup was not installed, so no public-bytes installation or upgrade replay is
claimed. The changed owner-relation flow is inherited exactly rather than re-run: the published
setup's embedded payload (`b82223de…`, 626 members) is byte-identical to the reviewed candidate
payload that the packaged `s16-t5` scenario exercised through the installed `cli/MetaBrain.Cli.exe`
and `service/MetaBrain.Connections.exe`, and the downloaded setup's own `--verify-payload` confirmed
that payload on the public copy.

Independent evidence review `S16-T6-C1-R1` (PASS), checkpoint `S16-T6-CHECKPOINT-C1` and the final
differential PASS are complete and remain their own records. This documentation delta is submitted
for the next independent assessment; a scoped site republish follows that acceptance.

