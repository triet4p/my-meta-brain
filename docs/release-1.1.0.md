# Meta Brain 1.1.0 — release record

**Status: publication authorized by the owner for 2026-11-01.** This record binds the exact public
assets below. If tag `v1.1.0`, the Release or any download URL does not exist, or a downloaded file
does not match the size and SHA-256 listed here, treat 1.1.0 as not published and this record as
unverified — report it instead of guessing.

This is the public release record for the three Meta Brain 1.1.0 products: the CLI, the Owner UI and
the standalone MCP server. Product behavior for the folder/partial surfaces was accepted in Sprint
11 (T1–T5); Sprint 11 T6 verifies release assembly, package integrity, the 1.0.0→1.1.0 upgrade, the
website and this documentation. Unchanged surfaces inherit their accepted earlier evidence; no
provider or client walkthrough is re-run or re-claimed here.

## Frozen candidate identity

| Field | Value |
| --- | --- |
| Product and version | `Meta Brain 1.1.0` for all three products |
| Candidate ID | `573e00d784cd` |
| Platform / runtime | Windows x64 (`win-x64`), self-contained |
| Assembly base HEAD recorded in the manifest | `b4a3bbbce0f0c90d32dabddef058eb2b7848f508` |
| Assembled source-tree SHA-256 | `3183d48f66d934c4d164b237b76a1d18e8c994c80040c195911251129ee87021` |
| Candidate identity | `b4a3bbbce0f0-tree3183d48f66d9-573e00d784cd` |
| Assembled at (UTC) | `2026-10-31T18:49:39.8902378Z` |
| Signing | Unsigned; SHA-256 verifies bytes, not publisher identity |

The candidate's source-tree digest is the **assembly-instant working-tree content digest** described
in the operations runbook §2.2: `git ls-files --cached --others --exclude-standard` reduced by the
assembler's fixed exclusion set, sorted, and hashed as `<file SHA-256><two spaces><path>` lines. It
is not a Git tree object of the base HEAD and it cannot be reconstructed after the fact, because the
population and the bytes changed. At assembly time the filtered untracked population included
`docs/ROADMAP-2.0.md`, `docs/POST-1.0-DIRECTION.md`, `sprint-plans/sprint-11..18.md` and
`scripts/sprint-11/__pycache__/` — content that is intentionally not part of this release's source
history. `workingTreeIncluded=true` records that honestly.

The three payload assets were assembled from that instant. Documentation, website and release-record
changes made after assembly are outside every packaged payload (payload members come only from the
`cli`/`service`/`installer`, `owner-ui`/`service`/`installer`, `mcp`/`service` and license/notice
sources), so they change neither the payload bytes nor the asset hashes. Git tag `v1.1.0` points at
the final reviewed publication source commit, which is later than the assembly head; there is no
fabricated assembly commit, and the manifest records the assembly head, not the tag target.

## Download matrix and publication URLs

The public filenames are a download-name mapping. They do not rewrite the candidate manifest or the
internal candidate filenames, and no 1.0.0 asset is overwritten or renamed by this release.

| Product | Public asset name | Bytes | SHA-256 | Public download URL |
| --- | --- | ---: | --- | --- |
| CLI setup | `MetaBrain-CLI-1.1.0.exe` | 186,594,706 | `18b7b5e7648e0d3def37d79af6369c7ed02474a7f7dfe8f732b2a947194a4f73` | `https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0/MetaBrain-CLI-1.1.0.exe` |
| Owner UI setup | `MetaBrain-UI-1.1.0.exe` | 277,562,464 | `f15b9edad127b3019573d01549c8e5e3d19aa232cb7a7eaec476a64528f2cc20` | `https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0/MetaBrain-UI-1.1.0.exe` |
| MCP package | `MetaBrain-MCP-1.1.0.zip` | 73,418,367 | `3f0a921368eb4a5494182941ff9945dd6d2e2e46a9baa7d1b06e82538e03987e` | `https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0/MetaBrain-MCP-1.1.0.zip` |

Internal candidate filenames (provenance-bound, unchanged):

```text
MetaBrain-CLI-1.1.0-win-x64-b4a3bbbce0f0-tree3183d48f66d9-573e00d784cd.exe
MetaBrain-OwnerUI-1.1.0-win-x64-b4a3bbbce0f0-tree3183d48f66d9-573e00d784cd.exe
MetaBrain-MCP-1.1.0-win-x64-b4a3bbbce0f0-tree3183d48f66d9-573e00d784cd.zip
```

The candidate manifest itself is `candidate-manifest.json` (754,634 bytes, SHA-256
`79bbd5a1840079e8b9bbb79091926de8a3592cba54b70b1b354134f95ae314cf`). It records the product version,
runtime identifier, self-contained identity, source provenance, per-asset size and SHA-256, per-asset
file inventory, dependency/license inventories with the bundled license and notice texts, the
supported-platform statement and the unsigned signing state. The release also publishes
`SHA256SUMS.txt` covering the three public assets, and the project `LICENSE`.

Public website: `https://triet4p.github.io/my-meta-brain/`.
Public release page: `https://github.com/triet4p/my-meta-brain/releases/tag/v1.1.0`.
The prior release stays live and unchanged: `https://github.com/triet4p/my-meta-brain/releases/tag/v1.0.0`.

## Release notes

The public release body is [`docs/release-notes-1.1.0.md`](release-notes-1.1.0.md). In summary:

- **CLI setup** installs `cli/MetaBrain.Cli.exe` without the Owner UI, SDK, repository or PowerShell
  glue, and gains the owner folder-enrollment, raw-preservation, folder-refresh and retention
  commands described in the runbook §3.2.3–§3.2.5.
- **Owner UI setup** is a genuine graphical installer with selectable application and data
  locations; the console gains the Sources folder preview/enroll controls and the four-facet source
  status view (runbook §3.2.6). Component-scoped removal keeps the shared runtime and owner data.
- **MCP ZIP** is standalone and self-contained: extract it completely, then register the absolute
  `mcp/MetaBrain.Mcp.exe` path with exactly one of `--data-root <owner-data-root>` or
  `--config <service-settings.json>`. The sibling `service/` runtime is resolved automatically. MCP
  exposes only the existing agent-scoped operations and never unlocks, publishes, approves or purges.

All three entrypoints resolve one shared per-user installation identity and one authoritative data
root. Installation never creates or unlocks a vault; every startup is locked. Upgrade refuses to
replace a live shared host instead of reporting false success, and uninstall/reinstall preserves the
vault, source snapshots, backups and recovery material. Default installs do not autostart, register
services or modify global PATH/environment.

## Support boundary

- Architecture: Windows x64 only. Linux, macOS and ARM are out of scope.
- Windows: 64-bit Windows releases that Microsoft lists as supported for .NET 10 at release time and
  that can run the required desktop/WPF UI. Server Core/Nano Server cannot run the Owner UI. The
  product was not exercised on every supported Windows SKU; this is a framework-supported boundary,
  not a per-SKU test claim.
- The self-contained setups need no installed .NET runtime, SDK, source checkout or PowerShell. A
  single-file setup may use the normal .NET host bundle-extraction cache under `%TEMP%\.net`; this
  does not change global/profile settings.
- Observed client evidence points remain Codex CLI 0.164.0 and Oh My Pi 18.12.6.

## Signing, checksums and licensing

- The release is **unsigned**. SHA-256 detects byte changes only; it does not authenticate the
  publisher. Do not disable SmartScreen, Defender or a security policy to install an unsigned file.
- Dependency inventory per product (from the candidate manifest): first-party Meta Brain projects
  plus `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT) and, for the Owner UI,
  `Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.14` (MIT). No external NuGet package dependency
  is redistributed. Each setup/package carries `RELEASE-NOTES.txt`, `THIRD-PARTY-NOTICES.txt`, the
  project `LICENSE` and the package-provided dependency license texts; the MCP ZIP also carries
  `package-manifest.json`.
- **Project license:** MIT in the repository-root `LICENSE`, with `Copyright (c) 2026 triet4p`.

## Known limits

1. **Unsigned distribution.** Verify the three SHA-256 values from a trusted copy of this record or
   the release page; a match does not establish publisher identity.
2. **Upgrade does not enable features.** Raw preservation, folder refresh and folder enrollment each
   need their own explicit owner consent and a future UTC retention deadline where applicable.
3. **Journal v2 downgrade.** A source whose journal is v2 fails closed on a 1.0.0 binary (no silent
   rebuild or partial read), and 1.1.0 never downgrades a v2 journal to v1.
4. **Retention expiry is not a purge.** After the deadline, reading a preserved span is refused with
   `source_preserved_record_expired`; the span stays listed with its deadline and is only removed by
   an explicit owner action.
5. **Installer banner.** The installer prints a static `vault state=uninitialized` banner even when
   upgrading an existing locked data root; this is pre-existing messaging, not a data-state report.
6. **Inherited functional acceptance.** Unchanged CLI/UI/MCP, sharing and lifecycle behavior inherits
   its accepted earlier evidence. This record claims only the release-scope verification below.
7. **Assembly-instant provenance.** The source-tree digest binds the assembly instant, so it is not
   recomputable from the published source commit and includes the intentionally untracked planning
   files listed above (runbook §2.2 states the algorithm and population).

## Release-scope verification summary

At the accepted prepublication gate (evidence review PASS, then a fresh differential PASS with no
actionable findings), the following was verified on the exact frozen assets (internal candidate
names):

- `assemble-candidate.ps1 -VerifyOnly` re-verified the manifest and all three assets (size, SHA-256,
  embedded payload members, MCP internal `package-manifest.json`) as `VERIFIED`.
- Installed CLI component and Owner UI component file sets, and the extracted MCP component, matched
  the candidate manifest byte-for-byte.
- Lifecycle on synthetic repository-local roots: fresh install, coexistence, start/stop/restart,
  component uninstall and final uninstall preserved the data root; the built packages were exercised
  from their own installed paths.
- 1.0.0 → 1.1.0 upgrade on synthetic roots: canonical vault bytes unchanged by the upgrade, restart
  locked, unlock/read exact, index rebuild `schema=3 rows=1`, pre-upgrade encrypted backup restored,
  uninstall preserved data.
- Changed folder/partial flow: synthetic Codex corpus with one reference-only source and one
  `partially_interpreted` source with a preserved span; the installed Owner UI listed the same
  sources and lock cleared them. The MCP package answered `initialize` with
  `serverInfo {name: metabrain-agent, version: 1.1.0}` and served its tool list over stdio.
- Local static site build: provenance pins, public-name mapping, sizes and SHA-256 verified before
  rendering; every download action resolved to the exact release URL and no placeholder remained.

These proofs used repository-local synthetic roots and the packaged entrypoints. They are not a
production host install, a provider call, or a public download smoke; the public byte verification
for the URLs above is a separate publication step.

## Publication checklist and exact commands

The owner authorized this publication. Actions are ordered so the public claim becomes true at the
end of the same operation:

1. Verify the three internal candidate assets against the frozen sizes and SHA-256 values above. Do
   not rebuild the candidate, change its manifest, or reuse a 1.0.0 name.
2. Collision-check tag `v1.1.0`, the Release and all target asset names. Any existing target stops
   the operation; nothing is overwritten.
3. Create immutable tag `v1.1.0` on the reviewed source commit and push `main` plus the tag.
4. Create the GitHub Release with the reviewed body and upload the three exact candidate asset bytes
   under the public names, plus `candidate-manifest.json`, `SHA256SUMS.txt` and `LICENSE`.
5. Build and deploy the published-status site only after the assets exist:

    ```powershell
    powershell.exe -NoProfile -File scripts/site/build.ps1 -ReleaseDownloadBaseUrl https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0
    ```

6. Public smoke from outside the checkout: open the site, the release page and each download URL;
   verify HTTP success, byte size and SHA-256 after download, the CLI/MCP `--version` output and the
   Owner UI title from the downloaded packages, and confirm the 1.0.0 links still resolve.

## Rollback and withdrawal

- **Before assets exist:** stop; the published 1.0.0 Release and current Pages deployment are
  unchanged.
- **Website rollback:** redeploy the previous known-good Pages artifact, or disable Pages in the
  repository settings. This affects only the static site.
- **Release withdrawal:** if bytes or notes are wrong, stop distribution and issue a corrected
  release under a new version/candidate. Do not overwrite `v1.1.0` assets, move the tag or reuse
  names; keep the published hashes visible so earlier downloads can be checked.
- **Security correction:** withdraw, preserve the evidence and hashes, and follow the owner's
  disclosure decision. Never silently replace bytes under the same version.
- **Local rollback:** an installed CLI/Owner UI is removed by its own setup (component-scoped) while
  the data root, vault, snapshots, backups and recovery material remain.

## Current status

- Candidate `573e00d784cd`, its manifest and the three asset hashes remain frozen and unmodified by
  this record.
- Release notes, download matrix and hashes were copied without alteration from the reviewed
  candidate and the accepted release runbook.
- No product, client, provider, security or performance flow beyond the release-scope verification
  above is claimed for this record.
