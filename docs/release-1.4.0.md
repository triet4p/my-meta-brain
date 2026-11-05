# Meta Brain 1.4.0 — release record

**Status: published 2026-11-05 as `v1.4.0`.** The owner authorized publication after the independent
evidence review, the exact-snapshot checkpoint and the final differential PASS. The reviewed `main`
through `4155997e1a02a20fab1b324c5a00ec02ec6965b7` was pushed, the annotated tag `v1.4.0` and the
GitHub Release were created, the exact candidate bytes were uploaded under the public names below,
the site was rebuilt against the published downloads and deployed, and the public assets and links
were smoke-verified from the live URLs. If a file claimed to be a 1.4.0 download does not match the
size and SHA-256 recorded here, treat it as not verified and report it instead of guessing.

This is the release record for the two Meta Brain 1.4.0 products: the CLI setup and
the standalone MCP server package. Product behavior for the workspace, search, document-session and
multi-source ingestion surfaces was accepted in Sprints 12–13 and is inherited unchanged; the S14
owner cognitive workflows (six-facet taxonomy, revision-pinned passage annotations, annotation
query/review, and cognitive-kind lifecycle/intention/recall) were accepted in S14-T1–T5. S14-T6
verifies release assembly, package integrity, the packaged CLI cognitive/lifecycle/review path, the
packaged MCP transport and agent surface, the 1.3.0 in-place upgrade, uninstall data preservation,
the 1.1.0 refusal, and this documentation/site. No provider, model or official-client walkthrough is
re-run or re-claimed here.

## Frozen candidate identity

| Field | Value |
| --- | --- |
| Product and version | `Meta Brain 1.4.0` for both products |
| Candidate ID | `03f772e2404d` |
| Candidate identity | `d0796b4f1458-treed1a503da70e1-03f772e2404d` |
| Platform / runtime | Windows x64 (`win-x64`), self-contained |
| Assembly base HEAD recorded in the manifest | `d0796b4f14584fef2167b5553f25da0bce73d23d` |
| Assembled source-tree SHA-256 | `d1a503da70e11a955b39e0e845acf1b0896148dfdb9c7d6a40bfa46234162331` |
| Assembled at (UTC) | `2026-11-05T13:33:42.6020095Z` |
| Signing | Unsigned; SHA-256 verifies bytes, not publisher identity |

The candidate's `sourceTreeSha256` is the **assembly-instant working-tree content digest** described
in the operations runbook §2.2: `git ls-files --cached --others --exclude-standard`, reduced by the
assembler's fixed exclusion set, sorted, then hashed as `<file SHA-256><two spaces><path>` lines.
It is not a Git tree object of the base HEAD. The manifest records `workingTreeIncluded=true`; this
value identifies the filtered contents at assembly, not the later working tree.

The release notes, this record, the runbook, the support matrix and the website were written after
assembly and are outside every packaged payload member, so they change neither asset bytes nor
hashes; no rebuild was performed to make documentation match the manifest. They form a later
documentation/site snapshot, not the assembly-instant tree.

## Assets and download-name mapping (published)

Published 2026-11-05 in the GitHub Release
[`v1.4.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.4.0). The public download URL for
each asset is `https://github.com/triet4p/my-meta-brain/releases/download/v1.4.0/<public asset name>`;
the public files are byte copies of the frozen candidate assets below.

| Product | Public asset name | Internal candidate filename | Bytes | SHA-256 |
| --- | --- | --- | ---: | --- |
| CLI setup | `MetaBrain-CLI-1.4.0.exe` | `MetaBrain-CLI-1.4.0-win-x64-d0796b4f1458-treed1a503da70e1-03f772e2404d.exe` | 187,940,754 | `d203c6628e3bcb62016378a2fdfef16209c5f1cff35a2a3b32b5b2227126a66d` |
| MCP package | `MetaBrain-MCP-1.4.0.zip` | `MetaBrain-MCP-1.4.0-win-x64-d0796b4f1458-treed1a503da70e1-03f772e2404d.zip` | 73,798,309 | `28875733584aacade16517f9c313994de21ba9effa0bafad83f67915b7893e00` |

The public names are the published download-name mapping; the internal candidate filenames stay the
frozen provenance-bound names. There is no 1.4.0 Owner UI asset, no `owner-ui` component and no
separate document-console download; neither public name may be reused for a UI build.

The git-ignored candidate directory
`runtime/release-candidates/1.4.0-win-x64-d0796b4f1458-treed1a503da70e1-03f772e2404d/` holds the two
assets and `candidate-manifest.json`. The manifest is 402,963 bytes with SHA-256
`88e96ca9e3a78223087e41adea683709469db5a6cb77d4034605e1c4b5b6ff35`; it records the source,
candidate ID, product version, exact asset layout, every file's size and SHA-256, embedded-payload
hashes, dependency/license inventories, supported-platform statement and truthful unsigned status.

| Internal asset / member | Bytes | SHA-256 |
| --- | ---: | --- |
| CLI setup EXE | 187,940,754 | `d203c6628e3bcb62016378a2fdfef16209c5f1cff35a2a3b32b5b2227126a66d` |
| Embedded CLI payload ZIP (592 members) | 110,720,205 | `d7c36fb94e44fe3677cf5d3dcf4f041ee1f63fcfdfb19cdf1537f27ff8fc9cc1` |
| MCP ZIP (396 files incl. `package-manifest.json`) | 73,798,309 | `28875733584aacade16517f9c313994de21ba9effa0bafad83f67915b7893e00` |
| Candidate manifest | 402,963 | `88e96ca9e3a78223087e41adea683709469db5a6cb77d4034605e1c4b5b6ff35` |

The MCP ZIP's own `package-manifest.json` lists 395 payload files plus itself (396 package files in
total). The MCP ZIP carries `mcp/MetaBrain.Mcp.exe`, the shared runtime under `service/`, its own
`package-manifest.json`, the project `LICENSE`, `RELEASE-NOTES.txt`, `THIRD-PARTY-NOTICES.txt` and
per-dependency license texts. The CLI setup carries the four payload directories (`cli/`, `mcp/`,
`service/`, `installer/`). The document bridge and its HTML/CSS/JS shell are compiled into
`service/MetaBrain.Connections.exe`; the packages contain no separate static site, web console or
plaintext document asset.

## Provenance, dependencies and licenses

- Source: this repository at base HEAD `d0796b4f14584fef2167b5553f25da0bce73d23d`, with the
  reviewed working tree at assembly; identity `d0796b4f1458-treed1a503da70e1-03f772e2404d`.
- Dependency inventory: first-party Meta Brain projects (`Core`, `Application`, `Connections`,
  `Cli`, `Installer`, `Mcp` at `1.4.0`) plus `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT).
  No WindowsDesktop/WPF runtime, no external NuGet package and no third-party bundle is
  redistributed. Each asset includes the dependency's license text and the third-party notices.
- Project license: MIT, `Copyright (c) 2026 triet4p` (`LICENSE` at each package root).
- Both assets are **unsigned**. SHA-256 is an integrity check only and cannot authenticate the
  publisher. Never disable SmartScreen, Defender or a security policy to install.
- Supported platform: 64-bit Windows releases Microsoft lists as supported for .NET 10 at
  publication time; the CLI and MCP server need no desktop/WPF capability. The optional `open`
  document session needs an installed Chromium-based browser on the same machine. Linux, macOS and
  ARM are out of scope. The exact statement is the
  [1.4.0 support matrix](release-contract.md#product-and-support-matrix-for-140-s14-t6).

## Release-scope verification summary

Every result below was observed on the exact candidate bytes above, from the packaged assets, with
synthetic repository-local data. No provider, model, private transcript or host/browser policy change
was involved, and no real host installation was mutated.

- **Assembly and integrity.** The candidate was assembled from the reviewed working tree by
  `scripts/release/assemble-candidate.ps1` (initially `VERIFIED` + `CANDIDATE`). After the
  documentation was written, `-VerifyOnly -CandidateDirectory <candidate>` printed `VERIFIED` again,
  re-checking the manifest, both asset sizes and SHA-256, the setup's embedded payload members and
  the MCP member hashes. No payload byte changed.
- **Packaged CLI cognitive, review and lifecycle path (no browser).** From the candidate setup EXE
  into a synthetic install/data root: setup/CLI reported `1.4.0`; `install-state.json` recorded
  `d0796b4f1458-treed1a503da70e1-03f772e2404d` and components `cli`; no `owner-ui/` existed; the
  first start was locked and `vault=uninitialized`. Provisioning created the vault and a one-time
  recovery key. One synthetic source was imported as content revision 1 and pinned by an exact
  byte-range anchor. Owner-authored World/Question and Intention annotations were confirmed
  `origin=Owner`/`review=Accepted`; an assistant draft stayed `origin=AiSuggestion`/`review=Pending`
  with `author=Assistant`; an owner note with no facets or kind stayed `kind=Unknown`/`FACETS none`.
  Facet/kind/review/origin filters and the review queue returned the expected records with
  `availability=Available` and `contentRevision=1`; a no-match query reported an empty result;
  `cognitive-annotation-read` plus `workspace-read --revision 1` returned the exact source bytes.
  A revision-gated correction appended revision 2 while preserving AI origin, author, executor and
  pending review; a stale correction failed with `revision_conflict`; accept and reject recorded the
  owner decision without dropping AI origin. `lifecycle-create` resolved the intention kind, an
  unclassified annotation was refused and stayed unclassified, a due trigger notified exactly once
  across repeated schedule passes, acknowledgement stayed distinct from completion, and the
  lifecycle history retained the notify act. `recall-apply` demote/restore recorded the policy
  without rewriting annotation origin, review or anchor revision. Lock refused a private read with
  `vault_locked`; restart came back locked; explicit unlock with the original passphrase restored
  the accepted annotations; `index-rebuild` did not replace canonical annotation state. Uninstall
  removed application files, kept the data root and settings bytes, and reinstall reopened the same
  vault, annotation and anchor revision. The packaged proof run passed all eleven stages.
- **Packaged MCP transport and agent surface.** The candidate ZIP's size and SHA-256 matched the
  manifest; 396 ZIP file members, the 395-file package inventory, dependency inventory, release
  notes and third-party notices were re-verified. `MetaBrain.Mcp 1.4.0` printed on stderr with
  stdout reserved. A real line-delimited stdio exchange returned `metabrain-agent 1.4.0` at
  `initialize`, an empty `ping` result and 22 agent tools with no owner tool; an owner-authority tool
  name was refused as unknown, and a non-string tool argument returned JSON-RPC `-32602` invalid
  params. The shared host the MCP started was stopped through the packaged shared runtime.
- **1.3.0 in-place upgrade, uninstall/reinstall and 1.1.0 refusal.** The published 1.3.0 candidate
  (hash-matched to `1dae0fe0…`) installed first; the vault was provisioned and one workspace file
  imported. The 1.4.0 setup then upgraded that installation while the 1.3 host was running: it
  reported `CLI installed at`, recorded the 1.4.0 candidate identity and the same data root, kept
  `service-settings.json` byte-identical and created no `owner-ui/`. The upgraded CLI reported
  `1.4.0`, started locked, unlocked the preserved vault, returned the exact 1.3 bytes, then pinned
  an anchor and created and queried a new cognitive annotation on the preserved data. Uninstall kept
  the data root/settings bytes and reinstall reopened the preserved annotation. A synthetic 1.1 WPF
  layout was refused with the named boundary and separate-root remediation before any file or data
  change.
- **Local static site (prepublication).** `scripts/site/build.ps1` verified the 1.4.0 manifest
  provenance, both asset sizes and SHA-256 and built the ignored local preview; the download actions
  resolved only to those verified candidate copies, the framing stated the candidate was not a public
  release (the site was later rebuilt against the published downloads; see the publication section),
  and the historical v1.0.0/v1.1.0/v1.2.0/v1.3.0 release links remained present and unchanged.
- **Not exercised.** Fresh Windows SKU coverage, real-host install/ACL/registry/service changes,
  WPF behavior beyond the 1.1 refusal, a provider/model call on this candidate, official Codex/OMP
  client walkthroughs on this candidate, and the optional browser document session. Sprint 14
  changed no document preview/edit code, so the release contract does not require a new browser run;
  the inherited S13 browser evidence is not re-claimed as 1.4.0-candidate proof. The T3 live-provider
  result (`5/10` requests, `$0.002741448` settled) was produced on its pre-T6 build and is **not**
  1.4.0-candidate provider proof.

## Compatibility and upgrade contract

- Vault envelope format, workspace format and metadata schema are unchanged from 1.3.0 (workspace
  format version 1). No automatic migration or conversion runs; existing 1.3 data opens exactly.
  The S14 cognitive annotations are additional encrypted records under the existing vault and do not
  change the vault envelope or workspace format.
- A 1.2.0 or 1.3.0 CLI/MCP installation upgrades in place with the data root, vault, settings and
  endpoints preserved. A pre-1.2 (1.1.0 WPF) or unknown/mixed installation is refused before the
  host is stopped or any file changes; install this release beside it with separate roots and
  endpoints.
- Uninstall removes application files and keeps the data root, vault, backups and recovery material.
  Reinstall/reselect opens the same data with the owner's credential; every start is locked.
- Downgrade: stop the 1.4 host, take an encrypted `owner backup`, and run the published 1.3.0 CLI
  setup over the same install root. The application version changes; the vault/workspace is not
  rewritten by either version. A 1.3 build does not provide the 1.4 cognitive annotation workflows,
  so take and verify the backup before downgrading. The [1.3.0 record](release-1.3.0.md) remains the
  authority for 1.3 behavior.

## Known limits

1. Windows x64 only, on the framework-supported boundary in the support matrix; not every listed
   SKU was exercised.
2. Both assets are unsigned; SHA-256 does not authenticate the publisher.
3. There is no 1.4.0 Owner UI installer. The frozen 1.1.0 WPF console is never built, packaged,
   upgraded or revived; a 1.1 installation is only refused and remediated with a separate root.
4. The six facets and kinds are owner review labels, not truth levels or a ranked relevance model.
   Classification, correction and review run fully through the CLI and need no provider or model.
   AI suggestions remain distinct and pending until the owner accepts or rejects them.
5. A structuring job may create pending suggestions for an exact workspace revision, but that is
   provider egress under the existing job consent/budget, not part of this local release evidence.
   No provider call is claimed for the 1.4.0 candidate.
6. An annotation anchor pins one exact file revision, byte range and digest. A revised or deleted
   source leaves the annotation `stale`/`Unavailable` at its original revision; it is never silently
   rebound or dropped.
7. Annotation query is structured filtering over stored facets, kinds, review state, origin and
   availability; it is not semantic search and does not rank by model. Workspace search remains the
   lexical search described in the 1.3.0 record.
8. One workspace act carries at most 1 MiB of original bytes; larger files are refused, never
   truncated. Source-session JSONL records remain read-only and are never served.
9. The optional `open` document session needs an installed Chromium-based browser; non-Chromium
   engines are not covered. The S14 changes do not touch that surface.
10. The MCP agent tool surface is unchanged at 22 tools with no owner-authority tool; the document
    bridge and the owner cognitive workflows are not agent tools. The packaged MCP was exercised
    with raw stdio JSON-RPC, not with every supported client version.
11. The documentation/site snapshot in the working tree is newer than the assembly-instant source
    snapshot and is deliberately not presented as the frozen tree.

## Publication executed (2026-11-05)

The owner authorized publication on 2026-11-05. Every step of the retained procedure was executed in
order on that date with no collision and no byte mismatch.

1. **Candidate re-verified, not rebuilt.** The two internal candidate assets and the manifest were
   re-hashed before upload: 187,940,754 bytes / `d203c662…`, 73,798,309 bytes / `28875733…`, and
   402,963 bytes / `88e96ca9…`, all matching the frozen values above, with `-VerifyOnly` `VERIFIED`
   when the candidate was assembled. No rebuild, no manifest change and no historical asset name was
   reused.
2. **No collision.** No `v1.4.0` tag, Release or target asset existed immediately before the writes;
   the 1.0.0–1.3.0 tags, Releases and assets were not modified.
3. **Reviewed main pushed, annotated tag created.** The reviewed `main` fast-forwarded to
   `4155997e1a02a20fab1b324c5a00ec02ec6965b7` (seven reviewed Sprint 14 commits on top of
   `5b1c726…`; no divergence, no force push). The annotated tag `v1.4.0` was created on
   `4155997e1a02a20fab1b324c5a00ec02ec6965b7` and pushed; GitHub reports
   `refs/tags/v1.4.0^{}` = `4155997e1a02a20fab1b324c5a00ec02ec6965b7`.
4. **Release and exact bytes.** The GitHub Release
   [`v1.4.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.4.0) was created and published
   with `MetaBrain-CLI-1.4.0.exe`, `MetaBrain-MCP-1.4.0.zip`, `candidate-manifest.json`,
   `SHA256SUMS.txt` and `LICENSE`, copied (never renamed) from the candidate directory. GitHub's own
   asset digests reported `sha256:d203c662…`, `sha256:28875733…`, `sha256:88e96ca9…` and
   `sha256:b623a3c4…` for the four byte-identical files.
5. **Record and site.** This record, the release notes, `README.md`, `CHANGELOG.md`, the
   `docs/META-BRAIN.md` status line and the `docs/OPERATIONS.md` limits were updated to the observed
   publication. The site was rebuilt with
   `powershell.exe -NoProfile -File scripts/site/build.ps1 -ReleaseDownloadBaseUrl https://github.com/triet4p/my-meta-brain/releases/download/v1.4.0`
   and deployed to the existing `gh-pages` Pages branch.
6. **Public smoke.** Recorded in the next section from the live URLs.

## Public smoke (2026-11-05)

All results below were observed on the public URLs after publication, from downloaded bytes, not from
the local candidate directory.

- Release page `https://github.com/triet4p/my-meta-brain/releases/tag/v1.4.0` — HTTP 200; GitHub
  reports the release published at `2026-11-05T15:18:56Z` and not a draft or prerelease.
- Public downloads — HTTP 200 for `MetaBrain-CLI-1.4.0.exe` (187,940,754 bytes),
  `MetaBrain-MCP-1.4.0.zip` (73,798,309 bytes), `candidate-manifest.json` (402,963 bytes),
  `SHA256SUMS.txt` (180 bytes) and `LICENSE` (1,064 bytes). Downloaded SHA-256 values matched the
  frozen values above exactly for the two assets, the manifest and `LICENSE`
  (`d203c662…`, `28875733…`, `88e96ca9…`, `b623a3c4…`).
- Downloaded CLI setup — `MetaBrain-CLI-1.4.0.exe --version` exited 0 and printed
  `MetaBrain CLI Setup 1.4.0` on stdout. Only the version was requested; no install, upgrade or host
  change ran.
- Downloaded MCP package — the extracted ZIP has 396 file members and passed a full archive integrity
  check; `mcp\MetaBrain.Mcp.exe --version` exited 0 and printed `MetaBrain.Mcp 1.4.0` on stderr with
  stdout empty.
- Site and links — the `gh-pages` deployment commit `613a86f3…` was built by GitHub Pages
  (`status: built` for that commit). `https://triet4p.github.io/my-meta-brain/`, `index.html`,
  `scenarios.html`, `codex.html`, `omp.html`, `operations.html` and `styles.css` all returned
  HTTP 200. The live page carries the published `BẢN PHÁT HÀNH 1.4.0 · ĐÃ CÔNG BỐ` banner, contains
  no preview banner, and its download actions point at the two `v1.4.0` release assets; the
  historical `v1.0.0`–`v1.3.0` links remain present. The `v1.0.0`–`v1.3.0` release pages, the
  published `v1.3.0` CLI/MCP download URLs and every earlier asset stay HTTP 200 and byte-unchanged
  (their asset digests are unchanged; the 1.3.0 CLI remains
  `1dae0fe0509c57081bf13ec81e7fe51ca88e95eecd61ba9375df135d34cc909b`).
- Verification tooling note — on the Windows Git Bash coreutils in this environment,
  `sha256sum -c SHA256SUMS.txt` reports `FAILED` for both binary assets because that build opens
  checksum targets in text mode; the per-file SHA-256 values, an independent hashlib digest and
  GitHub's own asset digests all matched. The shipped `SHA256SUMS.txt` uses the same standard
  `hash  name` format as the 1.3.0 release.

## Rollback

- **Website rollback:** redeploy the previous known-good Pages artifact, or disable Pages in the
  owner's repository settings.
- **Release/tag rollback:** GitHub does not support deleting and reusing a public tag invisibly;
  mark the Release as withdrawn and keep the tag/bytes for traceability unless the owner explicitly
  decides otherwise.
- **Local rollback:** stop the 1.4 host, take an encrypted backup, and run the published 1.3.0 CLI
  setup over the same install root. The data root is preserved; no vault/workspace rewrite is
  implied, and 1.4 annotation records remain opaque to 1.3.
- **Candidate defect:** delete only the git-ignored candidate directory and rebuild from a corrected
  reviewed source; never patch an asset in place or re-point the manifest.

## Current status

- **Published 2026-11-05 as `v1.4.0`.** The reviewed `main` through
  `4155997e1a02a20fab1b324c5a00ec02ec6965b7` is on `origin/main`, the annotated `v1.4.0` tag points
  at it, and the GitHub Release carries the exact candidate bytes under the public names. The public
  smoke above was observed from the live download URLs.
- Candidate `03f772e2404d` and its manifest/assets remain frozen at the sizes and hashes above and
  were re-verified with `-VerifyOnly` after the documentation was written. No payload byte changed
  before or after the proof runs, and publication copied the bytes without modifying them.
- The packaged CLI cognitive/review/lifecycle path, packaged MCP stdio transport and agent surface,
  1.3.0 in-place upgrade, uninstall data preservation and 1.1.0 refusal passed on those bytes and
  remain the release evidence.
- The site was rebuilt against the published download URLs and deployed to the existing `gh-pages`
  Pages branch as commit `613a86f309816cb4e4df34481d3a69f6aa34a4d9`; GitHub Pages built it and was
  observed serving the 1.4.0 content. During the build window the previous 1.3.0 page stayed live.
  The deployed page was built from the working-tree publication documentation snapshot described
  here, not from the `v1.4.0` tag tree: the tag points at the reviewed source commit
  `4155997e…`, and this publication record/notes/README/CHANGELOG delta reaches `main` through the
  separate reviewed checkpoint.
- No provider/model call and no host/browser policy change was made by this release task.
