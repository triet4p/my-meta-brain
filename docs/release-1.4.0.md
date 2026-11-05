# Meta Brain 1.4.0 — release record

**Status: `release_pending` — prepared local candidate, not published.** Nothing in this record has
been uploaded, tagged or deployed. The owner has not authorized publication; publication is a
separate, explicitly authorized step after the independent evidence review, the exact-snapshot
checkpoint and the final differential PASS. If a file ever claimed to be a 1.4.0 download does not
match the size and SHA-256 recorded here, treat it as not verified and report it instead of guessing.

This is the prepublication release record for the two Meta Brain 1.4.0 products: the CLI setup and
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

## Assets and download-name mapping (not published)

| Product | Planned public asset name | Internal candidate filename | Bytes | SHA-256 |
| --- | --- | --- | ---: | --- |
| CLI setup | `MetaBrain-CLI-1.4.0.exe` | `MetaBrain-CLI-1.4.0-win-x64-d0796b4f1458-treed1a503da70e1-03f772e2404d.exe` | 187,940,754 | `d203c6628e3bcb62016378a2fdfef16209c5f1cff35a2a3b32b5b2227126a66d` |
| MCP package | `MetaBrain-MCP-1.4.0.zip` | `MetaBrain-MCP-1.4.0-win-x64-d0796b4f1458-treed1a503da70e1-03f772e2404d.zip` | 73,798,309 | `28875733584aacade16517f9c313994de21ba9effa0bafad83f67915b7893e00` |

The public names are the download-name mapping a later owner-authorized publication would use. No
`https://github.com/triet4p/my-meta-brain/releases/download/v1.4.0/<name>` URL is live for 1.4.0 and
none may be described as live before the owner publishes it. There is no 1.4.0 Owner UI asset, no
`owner-ui` component and no separate document-console download; neither public name may be reused
for a UI build.

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
- **Local static site.** `scripts/site/build.ps1` verified the 1.4.0 manifest provenance, both asset
  sizes and SHA-256 and built the ignored local preview; the download actions resolve only to those
  verified candidate copies, the framing states the candidate is not a public release, and the
  historical v1.0.0/v1.1.0/v1.2.0/v1.3.0 release links remain present and unchanged.
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

## Publication checklist (not executed)

This checklist is retained as the ordered procedure for a later owner-authorized publication. None
of its steps has been performed; until then the status is `release_pending` and no 1.4.0 URL is live.

1. Re-verify the two internal candidate assets and the manifest against the frozen sizes and
   SHA-256 values above; do not rebuild the candidate, change its manifest or reuse a historical
   asset name.
2. Collision-check tag `v1.4.0`, the Release, `MetaBrain-CLI-1.4.0.exe`,
   `MetaBrain-MCP-1.4.0.zip`, `candidate-manifest.json`, `SHA256SUMS.txt` and `LICENSE`. Any
   existing target stops the operation.
3. After the reviewed commit is pushed, create the annotated tag `v1.4.0`, create the GitHub
   Release and upload the exact candidate bytes under the public names (copy, never rename the
   candidate files).
4. In this record, change the status block to published, fill the public URL cells and the
   publication date, and record the public smoke result. In `README.md`, change the "1.4.0 is a
   prepared local candidate" paragraph to published with the release link. In `CHANGELOG.md`, insert
   `## [1.4.0] - <publication date>` under `[Unreleased]` and move only the 1.4.0 entries under it.
5. Build the published site with
   `powershell.exe -NoProfile -File scripts/site/build.ps1 -ReleaseDownloadBaseUrl https://github.com/triet4p/my-meta-brain/releases/download/v1.4.0`
   and deploy the result through the existing owner-controlled Pages flow; verify the historical
   links still resolve.
6. Public smoke: HTTP 200 for site and Release, downloaded bytes match the sizes and SHA-256 above,
   downloaded CLI setup prints its 1.4.0 version, extracted MCP `--version` prints
   `MetaBrain.Mcp 1.4.0`, and the manifest hash matches. Any mismatch stops the claim and is
   reported, not guessed.

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

- Candidate `03f772e2404d` and its manifest/assets are frozen at the sizes and hashes above and were
  re-verified with `-VerifyOnly` after the documentation was written. No payload byte changed before
  or after the proof runs.
- The packaged CLI cognitive/review/lifecycle path, packaged MCP stdio transport and agent surface,
  1.3.0 in-place upgrade, uninstall data preservation and 1.1.0 refusal passed on those bytes and
  remain the prepublication evidence.
- The release is **not published**: no commit, push, tag, upload, deployment or public smoke has
  been performed for 1.4.0, and no public-release claim is made. Publication awaits the owner's
  separate authorization after the assigned review, checkpoint and differential gates.
- No provider/model call and no host/browser policy change was made by this release task.
