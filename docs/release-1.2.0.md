# Meta Brain 1.2.0 — release record

**Status: published 2026-11-01 as `v1.2.0`.** The owner authorized publication after this reviewed
candidate; tag, GitHub Release, the two assets and the website were published, and an independent
public smoke verified the public bytes against the sizes and SHA-256 listed here. The prepublication
verification text below is retained unchanged as the evidence that was assessed. If a downloaded
1.2.0 file does not match the size and SHA-256 listed here, treat 1.2.0 as not verified and report it
instead of guessing.

This is the prepublication release record for the two Meta Brain 1.2.0 products: the CLI setup and
the standalone MCP server package. Product behavior for the workspace surfaces was accepted in
Sprint 12 (T1–T5); S12-T6 verifies release assembly, package integrity, the packaged CLI lifecycle,
the 1.1 support transition, the packaged MCP transport, the local static site and this
documentation. Unchanged surfaces inherit their accepted earlier evidence; no provider or official
client walkthrough is re-run or re-claimed here.

## Frozen candidate identity

| Field | Value |
| --- | --- |
| Product and version | `Meta Brain 1.2.0` for both products |
| Candidate ID | `921141ec536c` |
| Platform / runtime | Windows x64 (`win-x64`), self-contained |
| Assembly base HEAD recorded in the manifest | `884dc6db58e5fb3b74a61ce00ffe1835d0fff5eb` |
| Assembled source-tree SHA-256 | `fb3c9c4bfe3617a0947b285f02ab2f4f12e43a1ed9966593614ae2b54ff23ad3` |
| Candidate identity | `884dc6db58e5-treefb3c9c4bfe36-921141ec536c` |
| Assembled at (UTC) | `2026-11-01T13:34:43.5291739Z` |
| Signing | Unsigned; SHA-256 verifies bytes, not publisher identity |

The candidate's `sourceTreeSha256` is the **assembly-instant working-tree content digest** described
in the operations runbook §2.2: `git ls-files --cached --others --exclude-standard`, reduced by the
assembler's fixed exclusion set, sorted, then hashed as `<file SHA-256><two spaces><path>` lines.
It is not a Git tree object of the base HEAD. The manifest records `workingTreeIncluded=true`; this
value identifies the filtered contents at assembly, not the later working tree.

The corrected CLI and MCP assets were assembled with the string-argument type guard and permanent
regression in the product/test source. The release record and the site's candidate pins below were
updated after assembly and are outside the packaged payloads. They form a later documentation/site
snapshot; there is no fabricated assembly commit, and the manifest records the actual base HEAD and
assembly-instant working-tree digest.

## Download matrix and publication URLs

The public filenames below are the download-name mapping the owner-controlled publication uses. They
do not rewrite the candidate manifest or the internal candidate filenames, and no 1.0.0/1.1.0 asset
is overwritten or renamed by this release.

| Product | Public asset name | Bytes | SHA-256 | Public download URL |
| --- | --- | ---: | --- | --- |
| CLI setup | `MetaBrain-CLI-1.2.0.exe` | 187,202,450 | `808d80fd52d34e704f57f1e12f42f00206b4bc0fc1d745fa96835da9230a08b2` | **published** — `https://github.com/triet4p/my-meta-brain/releases/download/v1.2.0/MetaBrain-CLI-1.2.0.exe` (public smoke matched this size and SHA-256) |
| MCP package | `MetaBrain-MCP-1.2.0.zip` | 73,598,029 | `f542ca7672211fb1bde57c37acc7592e7144b3d05ab388f118a5d797cb946e3b` | **published** — `https://github.com/triet4p/my-meta-brain/releases/download/v1.2.0/MetaBrain-MCP-1.2.0.zip` (public smoke matched this size and SHA-256) |

There is no 1.2.0 Owner UI asset, and neither public name may be reused for a UI build.

Internal candidate filenames (provenance-bound, unchanged):

```text
MetaBrain-CLI-1.2.0-win-x64-884dc6db58e5-treefb3c9c4bfe36-921141ec536c.exe
MetaBrain-MCP-1.2.0-win-x64-884dc6db58e5-treefb3c9c4bfe36-921141ec536c.zip
```

The git-ignored candidate directory `runtime/release-candidates/1.2.0-win-x64-884dc6db58e5-treefb3c9c4bfe36-921141ec536c/` holds those two assets and `candidate-manifest.json`:

| Candidate file | Bytes | SHA-256 |
| --- | ---: | --- |
| `candidate-manifest.json` | 402,963 | `c481968706a562685478ea1e34c10c83d58e377b67665d257eec09a58b4a5ae1` |
| CLI setup asset | 187,202,450 | `808d80fd52d34e704f57f1e12f42f00206b4bc0fc1d745fa96835da9230a08b2` |
| CLI setup embedded payload | 110,419,785 | `fda31f309c3b54ad779f8a61a71b5ad964988df2fd74e6ddcc00ee43dbffa048` |
| MCP package | 73,598,029 | `f542ca7672211fb1bde57c37acc7592e7144b3d05ab388f118a5d797cb946e3b` |

The MCP package carries 396 files including `package-manifest.json`; the CLI setup embeds a payload
whose every member is listed with its size and SHA-256 in the candidate manifest. The manifest
records the product version, runtime identifier, self-contained identity, source provenance,
per-asset size and SHA-256, the per-asset file inventory, dependency/license inventories with the
bundled license and notice texts, the supported-platform statement and the unsigned signing state.

The prior releases stay live and unchanged:
[`v1.1.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.1.0) (CLI, Owner UI and MCP 1.1.0;
publication authorized for 2026-11-01) and
[`v1.0.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.0.0).

## Release notes

The public release body is [`docs/release-notes-1.2.0.md`](release-notes-1.2.0.md). In summary:

- **CLI setup** installs `cli/MetaBrain.Cli.exe`, the shared runtime under `service/`, the shared
  installer under `installer/` and `install-state.json`, and registers the `cli` component only. It
  needs no Owner UI, SDK, repository or PowerShell glue, and it refuses an installed 1.1.0 or
  unknown/mixed layout in place.
- **MCP ZIP** is standalone and self-contained: extract it completely, then register the absolute
  `mcp/MetaBrain.Mcp.exe` path with exactly one of `--data-root <owner data root>` or
  `--config <service-settings.json>`. The sibling `service/` runtime is resolved automatically; the
  package starts or connects the same shared locked host.
- **Owner workspace** surfaces (create, revise, rename/move/copy, tombstone/restore, anchors,
  preview/export, purge, backup/restore) are CLI-complete in 1.2.0. Exports leave the vault as
  plaintext originals plus a versioned metadata document; the purge ledger makes an older backup
  unable to resurrect a purged identity.
- **Support transition**: 1.2 installs beside a 1.1 installation with its own install root, data root
  and control/agent endpoints. 1.2 never reads, converts, migrates or removes a 1.1 vault, and the
  frozen 1.1.0 Owner UI is not updated by 1.2.

Installation never creates or unlocks a vault; every start is locked. Default installs do not
autostart, register services or modify global PATH/environment.

## Support boundary

- Architecture: Windows x64 only. Linux, macOS and ARM are out of scope.
- Windows: 64-bit Windows releases that Microsoft lists as supported for .NET 10 at publication
  time. The CLI and the MCP server need no desktop/WPF capability, so Server Core/Nano Server is not
  excluded by a UI requirement. The product was not exercised on every supported Windows SKU; this
  is a framework-supported boundary, not a per-SKU test claim.
- The self-contained assets need no installed .NET runtime, SDK, source checkout or PowerShell. The
  single-file setup may use the normal .NET host bundle-extraction cache under `%TEMP%\.net`; this
  does not change global/profile settings.
- Observed client evidence points remain Codex CLI 0.164.0 and Oh My Pi 18.12.6. These are evidence
  points, not a minimum/maximum compatibility range.

## Signing, checksums and licensing

- The release is **unsigned**. SHA-256 detects byte changes only; it does not authenticate the
  publisher. Do not disable SmartScreen, Defender or a security policy to install an unsigned file.
- Dependency inventory per product (from the candidate manifest): first-party Meta Brain projects
  plus `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT). No external NuGet package dependency
  and no WindowsDesktop/WPF runtime is redistributed by either 1.2.0 asset.
- Each setup/package carries `RELEASE-NOTES.txt`, `THIRD-PARTY-NOTICES.txt`, the project `LICENSE`
  and the package-provided dependency license texts; the MCP ZIP also carries
  `package-manifest.json`.
- **Project license:** MIT in the repository-root `LICENSE`, with `Copyright (c) 2026 triet4p`.

## Known limits

1. **Prepublication only.** The candidate exists under the git-ignored
   `runtime/release-candidates/` tree. No public link, release page or website deployment for 1.2.0
   exists, and none may be claimed before the owner-authorized checklist completes.
2. **Unsigned distribution.** Verify the two SHA-256 values from a trusted copy of this record or the
   release page; a match does not establish publisher identity.
3. **One workspace act carries at most 1 MiB** of original bytes in 1.2.0 (`workspace_too_large`).
4. **Exports and purges are honest about copies.** A plaintext export cannot be revoked; a purge
   cannot recall backups or exports taken before it. The purge preview discloses both.
5. **No 1.2 Owner UI.** The frozen 1.1.0 console is not guaranteed to read 1.2 runtime or workspace
   data, and 1.2 does not install, upgrade or remove it.
6. **Installer banner.** The installer prints a static `vault state=uninitialized` banner even when
   upgrading an existing locked data root; this is pre-existing messaging, not a data-state report.
7. **Inherited functional acceptance.** Unchanged CLI/agent, sharing, source and lifecycle behavior
   inherits its accepted earlier evidence. This record claims only the release-scope verification
   below.
8. **Assembly-instant provenance.** The source-tree digest identifies the assembler's filtered
   working-tree contents at assembly, not a Git tree object; see §Frozen candidate identity and
   runbook §2.2.
9. **Docs/site snapshot is newer than the asset source snapshot.** The digest
   `fb3c9c4bfe36…` binds the corrected product source and other filtered working-tree contents at
   assembly. This release record and the site's candidate pins were updated afterward; the current
   working tree therefore does **not** hash to that digest and must not be presented as the exact
   asset-source snapshot.

## Release-scope verification summary

**Attempt `S12-T6-A5` completed the final narrow documentation/link/whitespace checks on the corrected
candidate. Candidate `921141ec536c` uses identity
`884dc6db58e5-treefb3c9c4bfe36-921141ec536c` and the source-tree digest above; the old candidate
`4647402d0b23` remains historical and was not overwritten. The post-docs candidate `-VerifyOnly`
passed, and the candidate is ready for `ready_for_review` submission; no reviewer verdict, checkpoint
or publication is claimed.**

- **Assembly and integrity.** `scripts/release/assemble-candidate.ps1` printed `VERIFIED` and
  `CANDIDATE` for the new CLI setup and MCP ZIP. A separate `-VerifyOnly -CandidateDirectory
  <candidate>` run printed `VERIFIED`, checking the manifest, asset sizes/hashes, embedded CLI
  payload members and MCP package manifest/member inventory. The exact frozen asset values are in
  the download matrix above.
- **Permanent typed-input regression.** The focused `s1-t9` smoke passed against the changed
  `MetaBrain.Connections.exe`: number, boolean, null, object and array values for string arguments
  returned JSON-RPC `-32602 bridge_invalid_params`; a raw bearer plus a mistyped value still returned
  `bridge_raw_bearer_denied`; the same process then answered `ping` and a valid catalog-list call.
  The advertised tool schemas and existing valid bridge flows were unchanged.
- **Packaged CLI lifecycle — passed.** The exact candidate setup installed as CLI-only, and setup,
  installed CLI and installer all reported 1.2.0. The proof started the shared host locked, provisioned
  the vault, imported markdown/empty/UTF-16/binary originals, revised the note and refused a stale
  revision, restarted locked and refused a private read, unlocked, read originals and retained
  revision 1 byte-exactly, exported a pinned revision with matching metadata, refused mismatched
  tokens, encrypted backups without plaintext markers, purged with the ledger and token gates, and
  restored both post-purge and pre-purge backups with the purged identity withheld. It ended with
  `S12-T6 PACKAGED CLI PROOF PASSED` and the installed host locked and stopped.
- **Packaged-byte harnesses and 1.1 transition — passed.** `s12-t4`, `s12-t5` and
  `s12-t5-legacy` passed against the installed packaged service. `s12-t5-install` passed against the
  local Release installer because its fixture requires the local build-output layout; the packaged
  setup's transition was separately exercised against a real released 1.1.0 installation and live
  host. The packaged setup refused the 1.1 layout without changing its host, files or data; a
  separate CLI-only candidate install used distinct roots/endpoints, started, stopped and uninstalled
  while preserving its data root. The corrected driver ended with
  `S12-T6 SUPPORT TRANSITION PROOF PASSED`.
- **Packaged MCP stdio — passed.** The candidate ZIP verified its `package-manifest.json`, all 395
  member hashes, license/notices, unsigned identity, 1.2.0 version and standalone layout. One real
  stdio process returned `initialize`, `ping` and 22 tools without owner authority; refused the
  no-session scoped read, raw bearer and owner-tool name; returned `-32602` for all five malformed
  JSON types; preserved bearer-denial precedence; then answered another `ping` and a valid
  catalog-list call. The shared host was stopped through the packaged CLI.
- **Local site preview — passed.** `scripts/site/build.ps1` verified and copied the exact new asset
  bytes into `dist/site/downloads/`. The preview renders the new candidate/source identities,
  checksums, sizes and local CLI/MCP download paths; no published `releases/download/v1.2.0` link or
  unresolved placeholder exists, historical v1.1.0/v1.0.0 links remain, and Owner UI is labelled
  legacy. The A3 real-browser visual pass is inherited: A4 changed only candidate pins and values,
  not the HTML template, stylesheet or guide layout; current dynamic labels/links and both local
  asset sizes/hashes were checked against the new manifest.
- **Normal-client boundary.** Codex CLI `0.164.0` resolved the process-scoped `-c` override to the
  newly extracted packaged MCP entrypoint and its `--data-root` argument in `codex mcp list`. No
  profile/global config was edited, no provider call or model-driven client request was made, and no
  OMP client call is claimed.
- **Not exercised in this prepublication evidence.** WPF behaviour beyond the 1.1 legacy refusal and
  provider/model flows remain outside this evidence. Publication was authorized and executed
  separately after the reviewed candidate; its actions and public smoke are recorded in
  `artifacts/sprint-12/publication/S12-PUBLISH-P1.md`, not claimed by this prepublication section.

## Publication checklist and exact commands

This checklist was executed after the owner's explicit authorization on 2026-11-01; it is retained
as the ordered procedure that was run. The actions are ordered so the public claim becomes true only
at the end:

1. Re-verify the two internal candidate assets and the manifest against the frozen sizes and SHA-256
   values above. Do not rebuild the candidate, change its manifest or reuse a 1.0.0/1.1.0 name.
2. Collision-check tag `v1.2.0`, the Release and both target asset names. Any existing target stops
   the operation; nothing is overwritten.
3. Create immutable tag `v1.2.0` on the reviewed publication source commit and push `main` plus the
   tag.
4. Create the GitHub Release with the reviewed body and upload the two exact candidate bytes under
   the public names, plus `candidate-manifest.json`, `SHA256SUMS.txt` and `LICENSE`.
5. Build and deploy the published-status site only after the assets exist:

    ```powershell
    powershell.exe -NoProfile -File scripts/site/build.ps1 -ReleaseDownloadBaseUrl https://github.com/triet4p/my-meta-brain/releases/download/v1.2.0
    ```

6. Public smoke from outside the checkout: open the site, the release page and both download URLs;
   verify HTTP success, byte size and SHA-256 after download, the CLI `--version` output and the MCP
   package's `--version`/manifest, and confirm the 1.0.0/1.1.0 links still resolve.

## Rollback and withdrawal

- **Before assets exist:** stop; the published 1.0.0/1.1.0 Releases and the current Pages deployment
  are unchanged.
- **Website rollback:** redeploy the previous known-good Pages artifact, or disable Pages in the
  repository settings. This affects only the static site.
- **Release withdrawal:** if bytes or notes are wrong, stop distribution and issue a corrected
  release under a new version/candidate. Do not overwrite `v1.2.0` assets, move the tag or reuse
  names; keep the published hashes visible so earlier downloads can be checked.
- **Security correction:** withdraw, preserve the evidence and hashes, and follow the owner's
  disclosure decision. Never silently replace bytes under the same version.
- **Local rollback:** an installed 1.2 CLI is removed by its own setup (CLI component only) while the
  shared runtime required by another registered component and the selected data root, vault,
  snapshots, backups and recovery material remain.

## Current status

- Corrected candidate `921141ec536c` and its manifest/assets were frozen at the sizes and hashes above;
  the final documentation/link/whitespace checks passed, and the post-docs `-VerifyOnly` run printed
  `VERIFIED`. No payload byte changed afterward.
- The product fix, permanent regression and all runtime/package proofs passed on the exact new
  candidate. The candidate was complete prepublication candidate evidence and was published after
  the owner's authorization; no `gatePASS` is claimed by this record.
- Packaged CLI lifecycle, `s12-t4`/`s12-t5`/`s12-t5-legacy`, `s12-t5-install` on its supported local
  Release fixture, the packaged 1.1 transition, the typed-input packaged MCP stdio proof and local
  site asset integrity checks all passed.
- Tag `v1.2.0`, the GitHub Release, both assets and the website are published; the public smoke
  matched the sizes and SHA-256 above and confirmed the legacy 1.0.0/1.1.0 links still resolve.
- The documentation/site snapshot in the working tree is newer than the assembly-instant source
  snapshot and is deliberately not presented as the frozen tree (Known limits 9).
- No product, client, provider, security or performance flow beyond the verification actually
  listed above is claimed for this record.
