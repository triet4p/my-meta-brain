# Meta Brain 1.3.0 — release record

**Status: published 2026-11-03 as `v1.3.0`.** The owner authorized publication after the reviewed
candidate, evidence review, exact-snapshot checkpoint and final differential, and the checklist
below was executed in order against the frozen candidate bytes. The tag, the GitHub Release and the
website are public. If a downloaded file does not match the size and SHA-256 listed here, treat it
as not verified and report it instead of guessing.

This is the prepublication release record for the two Meta Brain 1.3.0 products: the CLI setup and
the standalone MCP server package. Product behavior for the workspace, search and document-session
surfaces was accepted in Sprint 13 (T1–T5); S13-T6 verifies release assembly, package integrity,
the packaged CLI search/read/revise/restart/unlock path, the packaged CLI document session in a real
browser, the packaged MCP transport, the 1.2.0 in-place upgrade, 1.2.0-backup restore, uninstall
data preservation and this documentation/site. Unchanged surfaces inherit their accepted earlier
evidence; no provider or official-client walkthrough is re-run or re-claimed here.

## Frozen candidate identity

| Field | Value |
| --- | --- |
| Product and version | `Meta Brain 1.3.0` for both products |
| Candidate ID | `1184282a4204` |
| Candidate identity | `928e02d1c4f3-tree84fee97f04d9-1184282a4204` |
| Platform / runtime | Windows x64 (`win-x64`), self-contained |
| Assembly base HEAD recorded in the manifest | `928e02d1c4f3ed0a2009835a9d91e2ea26c533d9` |
| Assembled source-tree SHA-256 | `84fee97f04d9cddc7984c1d416aaa2d627e26ea8b82c28e52f7b341b00288a03` |
| Assembled at (UTC) | `2026-11-02T23:04:07.1091064Z` |
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

## Assets and public download mapping

| Product | Public asset name | Bytes | SHA-256 | Public URL |
| --- | --- | ---: | --- | --- |
| CLI setup | `MetaBrain-CLI-1.3.0.exe` | 187,729,298 | `1dae0fe0509c57081bf13ec81e7fe51ca88e95eecd61ba9375df135d34cc909b` | `https://github.com/triet4p/my-meta-brain/releases/download/v1.3.0/MetaBrain-CLI-1.3.0.exe` |
| MCP package | `MetaBrain-MCP-1.3.0.zip` | 73,739,482 | `246790377a07f739604e686bd5d1d91617bd6b4f7ba70fd3bc386a8bace94781` | `https://github.com/triet4p/my-meta-brain/releases/download/v1.3.0/MetaBrain-MCP-1.3.0.zip` |

The public URLs `https://github.com/triet4p/my-meta-brain/releases/download/v1.3.0/<name>` are live
for the two products and the three adjunct files and serve the exact candidate bytes above. There is
no 1.3.0 Owner UI asset, no `owner-ui` component and no separate document-console download; neither
public name may be reused for a UI build.

### Internal candidate filenames (provenance-bound; not public names)

```text
MetaBrain-CLI-1.3.0-win-x64-928e02d1c4f3-tree84fee97f04d9-1184282a4204.exe
MetaBrain-MCP-1.3.0-win-x64-928e02d1c4f3-tree84fee97f04d9-1184282a4204.zip
candidate-manifest.json
```

The git-ignored candidate directory `runtime/release-candidates/1.3.0-win-x64-928e02d1c4f3-tree84fee97f04d9-1184282a4204/`
holds those three files. The manifest is 402,963 bytes with SHA-256
`d42b7b676f14924608b658db59ce27302e6dfc3bbf3ebbf860107ced627a7683`; it records the source, candidate
ID, product version, exact asset layout, every file's size and SHA-256, dependency/license
inventories, supported-platform statement and truthful unsigned status.

| Internal asset / member | Bytes | SHA-256 |
| --- | ---: | --- |
| CLI setup EXE | 187,729,298 | `1dae0fe0509c57081bf13ec81e7fe51ca88e95eecd61ba9375df135d34cc909b` |
| Embedded CLI payload ZIP (592 members) | 110,632,001 | `dfdab23b49981ed5be4ba676b9b6917363550d5425b684626f6224c35c424fc4` |
| MCP ZIP (396 files incl. `package-manifest.json`) | 73,739,482 | `246790377a07f739604e686bd5d1d91617bd6b4f7ba70fd3bc386a8bace94781` |

The MCP ZIP carries `mcp/MetaBrain.Mcp.exe`, the shared runtime under `service/`, its own
`package-manifest.json`, the project `LICENSE`, `RELEASE-NOTES.txt`, `THIRD-PARTY-NOTICES.txt` and
per-dependency license texts. The CLI setup carries the four payload directories (`cli/`, `mcp/`,
`service/`, `installer/`). The document bridge and its HTML/CSS/JS shell are compiled into
`service/MetaBrain.Connections.exe`; the packages contain no separate static site, web console or
plaintext document asset.

## Provenance, dependencies and licenses

- Source: this repository at base HEAD `928e02d1c4f3ed0a2009835a9d91e2ea26c533d9`, with the
  reviewed working tree at assembly; identity `928e02d1c4f3-tree84fee97f04d9-1184282a4204`.
- Dependency inventory: first-party Meta Brain projects (`Core`, `Application`, `Connections`,
  `Cli`, `Installer`, `Mcp` at `1.3.0`) plus `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT).
  No WindowsDesktop/WPF runtime, no external NuGet package and no third-party bundle is
  redistributed. Each asset includes the dependency's license text and the third-party notices.
- Project license: MIT, `Copyright (c) 2026 triet4p` (`LICENSE` at each package root).
- Both assets are **unsigned**. SHA-256 is an integrity check only and cannot authenticate the
  publisher. Never disable SmartScreen, Defender or a security policy to install.
- Supported platform: 64-bit Windows releases Microsoft lists as supported for .NET 10 at
  publication time; the CLI and MCP server need no desktop/WPF capability. The optional `open`
  document session needs an installed Chromium-based browser on the same machine. Linux, macOS and
  ARM are out of scope. The exact statement is the [1.3.0 support matrix](release-contract.md#product-and-support-matrix-for-130-s13-t6).

## Release-scope verification summary

Every result below was observed on the exact candidate bytes above, from the packaged assets, with
synthetic project-local data. No provider, model, private transcript or host/browser policy change
was involved, and no real host installation was mutated.

- **Assembly and integrity.** `scripts/release/assemble-candidate.ps1` printed `VERIFIED` and
  `CANDIDATE` for the frozen directory (208 s). A later
  `-VerifyOnly -CandidateDirectory <candidate>` printed `VERIFIED` again after the documentation was
  written, re-checking the manifest, both asset sizes and SHA-256, the setup's embedded payload
  members, the MCP member hashes and `package-manifest.json`. No payload byte changed afterward.
- **Packaged CLI lifecycle, search, read, revise, restart, unlock (no browser).** From the candidate
  setup EXE into a synthetic install/data root:
  setup/CLI/installer all reported `1.3.0`; `install-state.json` recorded
  `928e02d1c4f3-tree84fee97f04d9-1184282a4204` and components `cli`; no `owner-ui/` existed; the
  first start was locked and `vault=uninitialized`. Provisioning created the vault and a one-time
  recovery key. One folder plus markdown/CSV/JSONL/UTF-16 files were imported as content revision 1.
  `owner workspace-search` returned the exact content hit with line, byte offset/length, file and
  range SHA-256; the JSON form reported `field=name` with the file digest; the heading query matched
  `field=heading`; the in-identifier substring `able` did **not** match `table.csv`; a no-hit query
  returned `total=0`; `--folder-id` narrowed to the folder while a file id and an unknown id were
  refused (`workspace_filter_not_folder`, `workspace_folder_not_found`). `workspace-read` returned
  the exact original bytes; `workspace-revise` advanced one content revision and a stale expected
  revision was refused; `open` with the now-stale revision/byte range/anchor digest was refused with
  `workspace_anchor_stale`.
- **Packaged CLI document session and its authority negatives.** `MetaBrain.Cli.exe open <csv> ...
  --no-browser` announced `name="table.csv" revision=1 format=csv editable=true` and served the
  shell with `Cache-Control: no-store` and `default-src 'none'`. The shell carried no document text
  and put the capability only in the served page. A missing capability returned `401`, a wrong one
  `403`, a foreign `Origin`, a foreign `Host` and a query string returned `403`/`403`/`400`. The
  bound read returned the exact UTF-8/LF CSV text at revision 1. A save with a stale expected
  revision returned `409 workspace_conflict`; an explicit save with the bound revision returned
  `saved` revision 2 and the CLI read back the exact new bytes. A UTF-16 file opened as
  `format=text encoding=utf-16le state=read_only_encoding editable=false` and its save was refused
  with `422`. After `installer restart` the vault was locked, a private read was refused with
  `vault_locked`, and `unlock` returned the saved revision and search again. The host was locked and
  stopped at the end.
- **Packaged CLI document session in an actual browser.** The installed CLI opened a markdown
  document with `--no-browser`, an installed Chromium browser loaded the bound page, rendered the
  preview, switched to the explicit editor, saved the edited text (committed revision verified
  byte-exactly through the CLI), received a conflict after a concurrent external revision while the
  draft stayed in the page, and — after `owner lock` — lost its capability and in-page content/draft.
  One installed browser was used; no non-Chromium engine was exercised.
- **Packaged MCP transport and agent surface.** The candidate ZIP was extracted and its manifest,
  395 member hashes, license/notice texts and unsigned identity re-verified. `MetaBrain.Mcp 1.3.0`
  and the standalone layout were confirmed; a real stdio exchange returned `metabrain-agent 1.3.0`
  at `initialize`, an empty `ping` result and 22 agent tools with no `metabrain_owner_unlock`.
  A scoped read without a session was refused (`session_unreadable`), a raw bearer argument was
  denied (`bridge_raw_bearer_denied`), an owner-authority tool name was refused
  (`bridge_unknown_tool`), number/bool/null/object/array arguments returned `-32602
  bridge_invalid_params`, bearer denial kept precedence, and the same process answered a later
  `ping` and valid catalog call. The shared host the MCP started was stopped through the installed
  CLI.
- **1.2.0 in-place upgrade, backup restore and uninstall/reinstall.** The published 1.2.0 candidate
  setup installed first; the vault was provisioned, one workspace file imported and an encrypted
  backup written. The 1.3.0 setup then upgraded that installation **while the 1.2 host was running**:
  it reported `INSTALLED`/`product version=1.3.0`, recorded the new candidate identity and the same
  data root, kept `service-settings.json` byte-identical (same control/agent endpoints) and created
  no `owner-ui/`. The upgraded CLI reported `1.3.0`; a locked private read was refused; `unlock`
  with the original passphrase returned the exact 1.2 bytes and search found them. The 1.2.0
  encrypted backup was restored with the 1.3.0 runtime into a new vault, which unlocked and reported
  `state=ready format=1 schema=1`. Uninstalling the CLI preserved the data root and settings bytes
  exactly, and reinstalling the same candidate reopened the same vault/workspace and read the same
  bytes.
- **Local static site.** `scripts/site/build.ps1` verified the 1.3.0 manifest provenance, both asset
  sizes and SHA-256 and built the ignored local preview; in that preview the download actions
  resolve only to those verified candidate copies, the framing presented the candidate as not yet a
  public release, and the historical 1.0.0/1.1.0/1.2.0 links stayed unchanged. The local pages were
  opened in a real browser and checked for unresolved placeholders, wrong download bytes and stale
  product labels.
- **Not exercised.** WPF behavior beyond the 1.1 refusal, provider/model flows, official-client
  model calls, non-Chromium browsers and every supported Windows SKU remain outside this evidence.
  This evidence ends at the prepublication candidate; the tag, upload, deployment and public smoke
  are recorded under [Publication result](#publication-result) below.

## Publication result (2026-11-03)

Publication was executed after the owner's explicit authorization and the completed prepublication
gates (task evidence, independent evidence reviews, exact-snapshot checkpoints and the final
differential PASS). Nothing was rebuilt, renamed or patched.

| Item | Value |
| --- | --- |
| Source commit pushed to `origin/main` | `35c764c74e0f50a251614a1c8c0b0a72ae85c412` (fast-forward from `dec95431d96bfdd55ead880e53f1a781a30ebdf2`) |
| Tag | annotated `v1.3.0` (tag object `b95f7722ce91b8e70f090b208281dd3555380982`) bound to the reviewed commit above |
| Release | `https://github.com/triet4p/my-meta-brain/releases/tag/v1.3.0`, published 2026-11-03, marked latest |
| CLI setup | `https://github.com/triet4p/my-meta-brain/releases/download/v1.3.0/MetaBrain-CLI-1.3.0.exe` — 187,729,298 bytes, SHA-256 `1dae0fe0509c57081bf13ec81e7fe51ca88e95eecd61ba9375df135d34cc909b` |
| MCP package | `https://github.com/triet4p/my-meta-brain/releases/download/v1.3.0/MetaBrain-MCP-1.3.0.zip` — 73,739,482 bytes, SHA-256 `246790377a07f739604e686bd5d1d91617bd6b4f7ba70fd3bc386a8bace94781` |
| Manifest | `https://github.com/triet4p/my-meta-brain/releases/download/v1.3.0/candidate-manifest.json` — 402,963 bytes, SHA-256 `d42b7b676f14924608b658db59ce27302e6dfc3bbf3ebbf860107ced627a7683` |
| Adjuncts | `SHA256SUMS.txt` (180 bytes, two lines) and `LICENSE` (1,064 bytes, SHA-256 `b623a3c47894f85a2a48029b580e6ee0fec70830401fcb39a2e1567b21322ec9`) |
| Website | `https://triet4p.github.io/my-meta-brain/` deployed from `gh-pages` commit `e6625011e1906bd827a2cf04eb495f713692d192` (`chore(site): correct published runbook status for Meta Brain 1.3.0`), which carries the published site and the corrected runbook |

Public smoke, performed from outside the checkout and without authentication:

1. The site returned HTTP 200 with the published-status framing, the two `v1.3.0` download actions and
   the 1.3.0 candidate identity; no preview banner, local `downloads/` link or unresolved placeholder
   remained, and the guide, Codex, OMP and runbook pages returned HTTP 200.
2. The Release page returned HTTP 200 and the five asset URLs were publicly reachable. The public
   downloads returned HTTP 200 with exactly the recorded sizes and SHA-256 values; the GitHub asset
   digests recorded at upload were identical before publication.
3. The downloaded CLI setup printed `MetaBrain CLI Setup 1.3.0`. The downloaded MCP ZIP carried
   `package-manifest.json` with `productVersion 1.3.0` and 395 files, and its `mcp\MetaBrain.Mcp.exe`
   printed `MetaBrain.Mcp 1.3.0` (stderr, stdout reserved for JSON-RPC).
4. The historical `v1.0.0`, `v1.1.0` and `v1.2.0` release pages returned HTTP 200, and every recorded
   1.0.0/1.1.0/1.2.0 asset kept its published name, size and SHA-256; the 1.1.0 WPF Owner UI asset was
   not rebuilt, renamed or withdrawn.

No candidate byte changed, no historical asset was overwritten, and no host, browser, provider or
permission state was touched. `gh-pages` received only the generated site files.

## Compatibility and upgrade contract

- Vault format, workspace format and metadata schema are unchanged from 1.2.0 (workspace format
  version 1). No automatic migration or conversion runs; existing 1.2 data opens exactly.
- A 1.2.0 CLI/MCP installation upgrades in place with the data root, vault, settings and endpoints
  preserved. A pre-1.2 (1.1.0 WPF) or unknown/mixed installation is refused before the host is
  stopped or any file changes; install this release beside it with separate roots and endpoints.
- Uninstall removes application files and keeps the data root, vault, backups and recovery material.
  Reinstall/reselect opens the same data with the owner's credential; every start is locked.
- Downgrade: stop the 1.3 host, take an encrypted `owner backup`, and run the published 1.2.0 CLI
  setup over the same install root. The application version changes; the vault/workspace is not
  rewritten by either version. The [1.2.0 record](release-1.2.0.md) remains the authority for 1.2
  behavior.

## Known limits

1. Windows x64 only, on the framework-supported boundary in the support matrix; not every listed
   SKU was exercised.
2. Both assets are unsigned; SHA-256 does not authenticate the publisher.
3. The optional `open` document session requires an installed Chromium-based browser; non-Chromium
   engines are not covered. Search/read/revise/restart/unlock work without any browser.
4. The document session serves exactly one live file at one content revision; it is not a folder
   browser, a tab set, a search console or a general web UI, and the browser receives no vault key,
   filesystem handle, private path or general owner API.
5. No plaintext HTML, draft, cache, temp file, cookie, browser storage or service worker is created
   by the product; browser-managed memory/cache, extensions, DevTools, OS swap and copies made
   outside the product cannot be erased by it, and no host/browser policy is changed.
6. Search is lexical and deterministic (exact name/path/heading/text); no stemming, synonyms,
   semantic retrieval or model ranking.
7. One workspace act carries at most 1 MiB of original bytes; larger files are refused, never
   truncated. Source-session JSONL records remain read-only and are never served.
8. The MCP agent tool list is unchanged; the document bridge is not an agent tool.
9. The documentation/site snapshot in the working tree is newer than the assembly-instant source
   snapshot and is deliberately not presented as the frozen tree.
10. `owner workspace-import/revise` hints name the current product version from the single version
    source; no behavior or byte envelope changed in 1.3.0.

## Publication checklist and exact delta

This checklist was executed after the owner's explicit authorization on 2026-11-03; it is retained
as the ordered procedure that was run. It performs exactly this ordered delta:

1. Re-verify the two internal candidate assets and the manifest against the frozen sizes and
   SHA-256 values above; do not rebuild the candidate, change its manifest or reuse a historical
   asset name.
2. Collision-check tag `v1.3.0`, the Release, `MetaBrain-CLI-1.3.0.exe`,
   `MetaBrain-MCP-1.3.0.zip`, `candidate-manifest.json`, `SHA256SUMS.txt` and `LICENSE`. Any
   existing target stops the operation.
3. Push the reviewed commit, create the annotated tag `v1.3.0` on it, create the GitHub Release and
   upload the exact candidate bytes under the public names (copy, never rename the candidate
   files).
4. In this record, change the status block to published, fill the two public URL cells and the
   publication date, and record the public smoke result. In `README.md`, change the "1.3.0 is a
   prepared candidate" paragraph to published with the release link. In `CHANGELOG.md`, insert
   `## [1.3.0] - <publication date>` under `[Unreleased]` and move only the 1.3.0 entries (the
   release-candidate and release-notes entries, the S13-T1–T5 behavior entries, and the 1.3.0
   version `Changed` entry) under it.
5. Build the published site with
   `powershell.exe -NoProfile -File scripts/site/build.ps1 -ReleaseDownloadBaseUrl https://github.com/triet4p/my-meta-brain/releases/download/v1.3.0`
   and deploy the result through the existing owner-controlled Pages flow; verify the historical
   links still resolve.
6. Public smoke: HTTP 200 for site and Release, downloaded bytes match the sizes and SHA-256 above,
   downloaded CLI setup `--version` prints `MetaBrain CLI Setup 1.3.0`, extracted MCP
   `--version` prints `MetaBrain.Mcp 1.3.0`, and the manifest hash matches. Any mismatch stops the
   claim and is reported, not guessed.

## Rollback

- **Website rollback:** redeploy the previous known-good Pages artifact, or disable Pages in the
  owner's repository settings.
- **Release/tag rollback:** GitHub does not support deleting and reusing a public tag invisibly;
  mark the Release as withdrawn and keep the tag/bytes for traceability unless the owner explicitly
  decides otherwise.
- **Local rollback:** stop the 1.3 host, take an encrypted backup, and run the published 1.2.0 CLI
  setup over the same install root. The data root is preserved; no vault/workspace rewrite is
  implied.
- **Candidate defect:** delete only the git-ignored candidate directory and rebuild from a corrected
  reviewed source; never patch an asset in place or re-point the manifest.

## Current status

- Candidate `1184282a4204` and its manifest/assets are frozen at the sizes and hashes above and were
  re-verified with `-VerifyOnly` after the documentation was written. No payload byte changed before
  or after publication.
- The packaged CLI, real-browser document session, packaged MCP, 1.2.0 in-place upgrade,
  1.2.0-backup restore, uninstall data preservation and local site checks passed on those bytes and
  remain the prepublication evidence.
- The release is published as `v1.3.0` on 2026-11-03: the reviewed source commit and the annotated
  tag are pushed, the Release carries the exact two assets plus `candidate-manifest.json`,
  `SHA256SUMS.txt` and `LICENSE`, and the published site links those public URLs.
- The public smoke in [Publication result](#publication-result) re-downloaded the public bytes and
  matched their sizes, SHA-256 values, packaged versions and manifest. The historical
  1.0.0/1.1.0/1.2.0 assets were not changed.
- No provider call and no host/browser policy change was made by this release task.
