# Meta Brain 1.0.0 — release record and publication handoff

**Status: candidate frozen; not published.** Public release assets, the tag `v1.0.0`, GitHub
Pages deployment and any external publication remain pending the owner's separate authorization and
a public smoke test. This record does not authorize a push, tag, upload, deployment or host change.

This is the public release record for the three Meta Brain 1.0.0 products: the CLI, the Owner UI
and the standalone MCP server. Product behavior was accepted in Sprint 9; Sprint 10 verifies only
release assembly, package integrity, installer distribution boundaries, the website and the
release documentation. No product workflow, client flow, security scenario or owner walkthrough is
re-verified or re-claimed here.

## Frozen candidate identity

| Field | Value |
| --- | --- |
| Product and version | `Meta Brain 1.0.0` for all three products |
| Candidate ID | `1e2953ae6bf4` |
| Platform / runtime | Windows x64 (`win-x64`), self-contained |
| Assembly base HEAD recorded in the manifest | `86626bda2908235c02392d421f2117b36c3d11cb` |
| Assembled source-tree SHA-256 | `1edb06ca92549af2e26bd713b120f45b6f4c34affeb2d8d67ec581ecd008a7c6` |
| Binary source revision (committed content) | `35e01ec1c6d80bac41d8d2fe7aeef8144f3d34f7` |
| Signing | Unsigned; SHA-256 verifies bytes, not publisher identity |

The candidate was reassembled on 2026-10-26 from the working tree whose base HEAD was
`86626bda2908235c02392d421f2117b36c3d11cb`. Its source-tree digest binds the generated release
payload source and project MIT license; later A2 changes to release record/site text and builder
candidate pinning are docs/site-only and did not alter the three built assets. The binary source
revision remains `35e01ec1c6d80bac41d8d2fe7aeef8144f3d34f7`, as product source was not changed.
Candidate `80576c073f06` is superseded because its distributable packages lacked the project
license; use only this replacement candidate. Any future rebuild is another candidate with new
filenames and hashes.

## Download matrix and planned publication URLs

All three assets come from this one candidate and share the same product version and runtime.
The exact planned locations are the owner-controlled repository
`https://github.com/triet4p/my-meta-brain`:

| Product | Asset | Bytes | SHA-256 | Planned download URL |
| --- | --- | ---: | --- | --- |
| CLI setup | `MetaBrain-CLI-1.0.0-win-x64-86626bda2908-tree1edb06ca9254-1e2953ae6bf4.exe` | 186,187,154 | `dbfc8a13eefcd65060f4cd52e1d2b5d127be274f9b311162636a8b2bc6837e0a` | `https://github.com/triet4p/my-meta-brain/releases/download/v1.0.0/MetaBrain-CLI-1.0.0-win-x64-86626bda2908-tree1edb06ca9254-1e2953ae6bf4.exe` |
| Owner UI setup | `MetaBrain-OwnerUI-1.0.0-win-x64-86626bda2908-tree1edb06ca9254-1e2953ae6bf4.exe` | 277,357,664 | `2a32a44d6beceb03bab195265bb2950b31961dadda396eff68f593a4491951a3` | `https://github.com/triet4p/my-meta-brain/releases/download/v1.0.0/MetaBrain-OwnerUI-1.0.0-win-x64-86626bda2908-tree1edb06ca9254-1e2953ae6bf4.exe` |
| MCP package | `MetaBrain-MCP-1.0.0-win-x64-86626bda2908-tree1edb06ca9254-1e2953ae6bf4.zip` | 73,290,820 | `0a475496977b7f6ff4db187093869aa788e9fb52088ec49dfe6b9b3f844ab857` | `https://github.com/triet4p/my-meta-brain/releases/download/v1.0.0/MetaBrain-MCP-1.0.0-win-x64-86626bda2908-tree1edb06ca9254-1e2953ae6bf4.zip` |

Planned website: `https://triet4p.github.io/my-meta-brain/`.
Planned release page: `https://github.com/triet4p/my-meta-brain/releases/tag/v1.0.0`.
Website downloads are required to resolve to the exact Release URLs above; the local preview
never claims that the public links are live.

## Release notes

The public release body used by the publication checklist is
[`docs/release-notes-1.0.0.md`](release-notes-1.0.0.md). The summary below is retained in this
record for traceability.

- **CLI — dedicated Windows x64 setup.** Installs the installed `cli/MetaBrain.Cli.exe` entrypoint
  without the Owner UI, SDK, repository or PowerShell glue. Setup supports interactive install,
  explicit install/data roots, same-candidate upgrade/reinstall, component-scoped removal,
  `--help` and `--version`. It does not add PATH, profile, service, registry, autostart or ACL
  entries. The shared runtime and owner data are preserved while another frontend remains.
- **Owner UI — dedicated graphical Windows x64 setup.** A WPF setup lets the owner choose separate
  application and data locations, browse for them, install/update and optionally open the console.
  It is a genuine graphical installer, not a command-line installer with hidden flags or a CLI
  bootstrap. Remove Owner UI is component-scoped; data stays untouched.
- **MCP — standalone Windows x64 package.** Extract the complete ZIP preserving directories; the
  stdio command is the absolute extracted `mcp/MetaBrain.Mcp.exe` with exactly one argument pair:
  `--data-root <owner-data-root>` or `--config <service-settings.json>`. The sibling `service/`
  runtime is resolved automatically. The package includes `package-manifest.json`, release notes,
  third-party notices and per-dependency license texts. Codex CLI and Oh My Pi are optional
  consumers; no client profile is edited by this project.

All three entrypoints resolve one shared per-user installation identity and one authoritative data
root. Installation never creates or unlocks a vault; every startup is locked. Upgrade refuses to
replace a live shared host instead of reporting false success, and uninstall/reinstall preserves
the vault, source snapshots, backups and recovery material. Default installs do not autostart,
register services or modify global PATH/environment.

## Support boundary

- Architecture: Windows x64 only. Linux, macOS and ARM are out of scope.
- Windows: 64-bit Windows releases that Microsoft lists as supported for .NET 10 at release time
  and that can run the required desktop/WPF UI. Server Core/Nano Server cannot run the Owner UI.
  The product was not exercised on every supported Windows SKU; this is a framework-supported
  boundary, not a per-SKU test claim.
- The self-contained setups need no installed .NET runtime, SDK, source checkout or PowerShell.
  A single-file setup may use the normal .NET host bundle-extraction cache under `%TEMP%\.net`;
  this does not change global/profile settings.
- Observed client evidence points are Codex CLI 0.164.0 and Oh My Pi 18.12.6. These are observed
  versions, not a minimum/maximum support range.

## Signing, checksums and licensing

- The release is **unsigned**. SHA-256 detects byte changes only; it does not authenticate the
  publisher. Do not disable SmartScreen, Defender or a security policy to install an unsigned
  file, and do not claim that a checksum proves who published it.
- The packaged dependency graph contains only first-party Meta Brain project references plus the
  self-contained .NET runtime packages `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT, with
  third-party notices) and, for the Owner UI, `Microsoft.WindowsDesktop.App.Runtime.win-x64
  10.0.14` (MIT). No external NuGet package dependency was found. Each setup/package carries
  `RELEASE-NOTES.txt`, `THIRD-PARTY-NOTICES.txt`, the project `LICENSE`, and package-provided
  dependency license texts; the MCP ZIP also carries `package-manifest.json`.
- **Project license:** Meta Brain is licensed under the MIT License in the repository-root
  `LICENSE`, with the factual repository-owner attribution `Copyright (c) 2026 triet4p`.
  The project license does not replace third-party license texts or notices. Historical owner
  decision records choosing no project license are superseded by the owner's later explicit MIT
  instruction; no EULA was created.

## Known limits

1. **Unsigned distribution.** Verify the three SHA-256 values from a trusted copy of this record or
   the release page. A checksum match does not establish publisher identity.
2. **Cosmetic text encoding in packaged notices.** The generated `RELEASE-NOTES.txt` and
   `THIRD-PARTY-NOTICES.txt` headings display a mojibake em dash (`â€”` instead of `—`) in the
   frozen assets. The machine-readable manifest/JSON is ASCII, all hashes verify, and no license
   or provenance statement is missing. Correcting the bytes requires a new candidate and fresh
   verification; it is recorded here rather than silently changed in a published candidate.
3. **CLI proposal undo.** The shipped CLI dispatcher has proposal list/read/publish/reject but no
   dedicated proposal-undo verb. Undoing a published proposal is available through the Owner UI
   Review Queue; do not invent a CLI command for it.
4. **Local runbook page.** The local site serves the runbook as preformatted text without rendered
   Markdown section anchors; references to internal evidence records are omitted from the served
   copy. The repository runbook remains the source.
5. **Functional acceptance is inherited.** Sprint 10 does not re-run CLI/UI/MCP product flows,
   Codex/OMP client flows, security/lock/revoke scenarios or an owner functional walkthrough.
   Product behavior acceptance and client observations trace to accepted Sprint 9 evidence.
6. **MCP extraction and configuration.** Keep the ZIP directory layout; do not copy only the EXE.
   Pass exactly one of `--data-root` or `--config`. Keys/bearers do not belong in configuration
   files, and this project does not edit Codex/OMP profiles.
7. **Owner distribution sign-off.** The owner accepted the documented product/payload delta on
   2026-10-26 without requiring another install. The A2 package-manifest comparison against A1
   found only an added project `LICENSE`, updated release/third-party notices, and updated MCP
   package metadata; executable/runtime payload members did not change. The new candidate setup
   bytes were not owner-run; prior A1 install observations are not claimed for these exact bytes.

## Release-only verification summary

- Historical A1 staged CLI and graphical UI distribution/coexistence proofs apply to superseded
  candidate `80576c073f06` only. The exact A1 observations and limits are retained in the private
  task evidence `artifacts/sprint-10/task-8.md`; they are not represented as execution on this
  replacement candidate. No product or installer flow was rerun for A2.
- The candidate's three asset byte sizes and SHA-256 hashes match its manifest; setup payloads
  include the project MIT `LICENSE` and are member-hash verified; the MCP archive includes the same
  license plus 395 further members (396 total) with matching sizes/hashes, and a valid
  `package-manifest.json` stdio command/argument contract.
- A2 reassembled candidate `1e2953ae6bf4` and the site builder verified candidate provenance plus
  all three asset hashes. The local preview was generated; publication mode was exercised offline
  and points to the exact Release URLs while stating publication remains pending. Neither site was
  deployed. A1 CLI/UI staged distribution observations are inherited evidence for candidate
  `80576c073f06`; A2 did not rerun product or installer flows on the new licensed candidate.
- No product, client, provider, security or performance flow was executed for this record.

## Publication prerequisites (owner-controlled)

- [ ] Owner authorizes publication (push/tag, Release upload, Pages deployment) separately.
- [x] Owner accepted the documented source/payload delta on 2026-10-26; the exact final setup bytes
      were not owner-run. Staged proof informs but does not authorize publication.
- [ ] No `v1.0.0` tag or release already exists with different bytes. If it does, stop and decide
      version/distribution; never overwrite a published version or asset name.
- [x] Owner terms decision recorded: MIT License for Meta Brain, with copyright retained; third-party terms remain separate.
- [ ] Release notes, download matrix and the three hashes above are copied from this record without
      alteration.

## Publication checklist and exact commands

These commands are for the owner or an executor the owner explicitly authorizes. They require Git
and GitHub CLI authentication and change the public repository, Releases and Pages; an agent must
not run them without that separate authorization.

1. Verify the local files before upload:

    ```powershell
    Get-FileHash -Algorithm SHA256 -LiteralPath <the three assets>
    ```

2. Check collisions, then create and push the tag only after the hash check:

    ```powershell
    git ls-remote --tags origin v1.0.0
    gh release view v1.0.0
    git tag -a v1.0.0 -m "Meta Brain 1.0.0"
    git push origin v1.0.0
    ```

3. Upload exactly the three candidate files with these notes:

    ```powershell
    gh release create v1.0.0 `
      "MetaBrain-CLI-1.0.0-win-x64-86626bda2908-tree1edb06ca9254-1e2953ae6bf4.exe" `
      "MetaBrain-OwnerUI-1.0.0-win-x64-86626bda2908-tree1edb06ca9254-1e2953ae6bf4.exe" `
      "MetaBrain-MCP-1.0.0-win-x64-86626bda2908-tree1edb06ca9254-1e2953ae6bf4.zip" `
      --title "Meta Brain 1.0.0" --notes-file docs/release-notes-1.0.0.md --verify-tag
    ```

4. Build the publication site with the exact owner-controlled download base, inspect that the
   three actions point at the Release URLs and the status text says published, then deploy
   `dist/site` to GitHub Pages using the owner's chosen Pages source (no deployment workflow is
   committed in this repository):

    ```powershell
    powershell.exe -NoProfile -File scripts/site/build.ps1 -ReleaseDownloadBaseUrl https://github.com/triet4p/my-meta-brain/releases/download/v1.0.0
    ```

5. Public smoke from outside the checkout: open the website and each of the three download URLs,
   confirm HTTP success and byte sizes, compare SHA-256 after download, check the CLI/UI/MCP guides
   and the release record link, and record the deployed site revision and release timestamps.

## Rollback and withdrawal

- **Before publication:** stop; nothing public has changed.
- **Website rollback:** redeploy the previous known-good Pages artifact/commit, or disable Pages in
  the repository settings. This affects only the static site; it does not change release assets.
- **Release withdrawal:** if the bytes or notes are wrong, stop distribution and create a corrected
  release under a new version/candidate. Do not overwrite `v1.0.0` assets or reuse their names. A
  published GitHub Release can be deleted (or its assets removed) with owner authorization; the tag
  can be deleted separately. Downloads already taken cannot be recalled, so announce the
  withdrawal and keep the old hashes visible for verification.
- **Security correction:** withdraw the release if a security defect is confirmed, preserve the
  evidence and hashes, and follow the owner's disclosure decision. Do not silently replace bytes
  under the same version.
- **Local rollback:** an installed CLI/Owner UI can be removed with its own setup (component-scoped
  removal) while the data root, vault, snapshots, backups and recovery material remain.

## Current gates

- Release assets are frozen at the candidate identified above; no actionable in-scope release/install/site defect is
  known. The owner selected MIT with copyright retained; the exact final setup bytes were not owner-run.
- Publication remains subject to the owner-controlled publication gate: final release review gates,
  `v1.0.0` tag/asset collision checks, the owner's final go-ahead and public smoke.
- Public publication is **not** authorized or performed by this record. Release 1.0.0 must not be
  described as published until those gates complete.
