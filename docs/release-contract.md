# Meta Brain 1.0.0 — release contract

This is the release/install/site contract for Sprint 10. It does not change product behavior or re-run Sprint 9 acceptance. **Requirements** below are binding; proposed implementation choices are identified explicitly; unverified prerequisites remain unverified. Product versions, signing claims, release assets, publication, and support status must never be inferred from a plan or from the existing S9 bundle.

## Freeze identity and immutability

- Candidate identity is immutable provenance: candidate directories, `candidate-manifest.json`, and
  internal asset filenames bind the reviewed source, candidate ID, product version, file sizes and
  SHA-256. Public names are a separate output mapping and must not rewrite the manifest or internal
  candidate files.
- Public 1.0.0 download names are exactly `MetaBrain-CLI-1.0.0.exe`,
  `MetaBrain-UI-1.0.0.exe`, and `MetaBrain-MCP-1.0.0.zip`. Future release publication must use the
  same simple `MetaBrain-<product>-<version>.<ext>` mapping (UI uses `UI`); verify collision-free
  destination names before renaming existing assets, preserve bytes/tag, and stop on collision.
  Do not upload aliases or duplicates.
- The manifest/record includes source revision, candidate ID, product versions, exact asset layout, file sizes and SHA-256, dependency/license notices and redistribution findings, supported OS and known limits, release notes, compatibility and truthful signing status. SHA-256 verifies bytes, not publisher identity. Do not publish private worker/review/checkpoint evidence, vault data, source snapshots, credentials, tokens, host-specific paths or generated build/private records.

## Product and distribution matrix

| Product | Version / asset | Entry and lifecycle |
| --- | --- | --- |
| CLI | `1.0.0`; public download `MetaBrain-CLI-1.0.0.exe`; candidate filename remains provenance-bound in the manifest | Dedicated CLI installer; end user does not need Owner UI, SDK, repository or PowerShell glue. Installed `cli/MetaBrain.Cli.exe` remains the CLI entrypoint. |
| Owner UI | `1.0.0`; public download `MetaBrain-UI-1.0.0.exe`; candidate filename remains provenance-bound in the manifest | Separate genuinely graphical installer, not command-line setup with internal flags. UI setup allows the owner to select install and data locations and launch the UI. `owner-ui/MetaBrain.OwnerUi.exe` remains the application entrypoint. |
| MCP Server | `1.0.0`; public download `MetaBrain-MCP-1.0.0.zip`; candidate filename remains provenance-bound in the manifest | Standalone self-contained ZIP with `mcp/MetaBrain.Mcp.exe`, shared runtime under `service/`, `package-manifest.json`, notices and per-dependency license texts. The manifest records exact entrypoint, stdio transport, exclusive argument choices, help/version commands, provenance, file hashes, client compatibility evidence and known limits. |
| Website/guides | Static public landing page for exact candidate assets | Download links for all three products; CLI/UI quick starts, scenario guides, normal Codex CLI/OMP connection, security, FAQ and troubleshooting. Responsive rendering and every download link must resolve to the exact reviewed release. No product-flow walkthrough is required or permitted as a substitute for inherited Sprint 9 evidence. |
| Release record | Candidate manifest/release notes | Trace every download to exact candidate, provenance, dependencies/licenses, OS/limits, compatibility and signing disclosures. Public site/release content is sanitized. |

### Reuse and shared runtime

The S9 implementation is `scripts/release/publish-release-bundle.ps1`, one self-contained `win-x64` payload/zip with all five directories and a file manifest; `src/MetaBrain.Installer/Program.cs` is a command-line per-user installer, and `InstallCommand.cs`/`InstallLayout.cs` implement the shared payload and separate install/data roots. This is accepted S9 packaging evidence, not by itself evidence for standalone installers. S10-T3 adds the separate candidate-bound CLI setup EXE and component-aware shared installation state; S10-T4 adds a distinct WPF graphical Owner UI setup EXE over the same shared installer lifecycle.

**Chosen shared-runtime ownership/version contract:** the CLI and UI installers are two entry-specific front doors to one per-user Meta Brain installation identity and one authoritative shared runtime. They may install/update their own frontend files and register their installed component against that identity; they do not own duplicate host or data copies. All entrypoints resolve the same selected data root/settings/endpoints and the one shared locked host. A runtime update is atomic at that shared installation identity and is accepted only when the complete release set (CLI/UI/MCP/runtime/installer) matches one candidate manifest/version. If a different frontend/runtime version is already active or registered, refuse the conflicting update with a clear remediation rather than silently choosing whichever installer ran last. Do not replace runtime files while its process is live; use the existing drain/lock lifecycle and fail honestly if it cannot complete. Removing one frontend leaves the shared runtime while another registered frontend/MCP package depends on it. Removing the final installed frontend may remove app/runtime files only, never the data root. MCP can run from its self-contained package and select the same explicit instance without requiring either owner frontend to be open.

The shared installer state now records the registered frontend components and candidate identity. T3 installs/removes only the CLI frontend, while the dedicated graphical Owner UI setup uses the same state, candidate identity, shared runtime and recorded data root. Both frontends preserve the shared runtime while another registered component remains, keep the data root, stage app-directory replacement before swapping, refuse conflicting shared candidates/version, and refuse replacement when the host cannot drain cleanly. The Owner UI setup is a separate self-contained Windows x64 EXE with a real location-selection/install/update/remove window; it is not the Owner UI payload archive and does not bootstrap through CLI or PowerShell. It does not create a second instance, settings format, data root, policy, store or host registration.

The MCP archive is independently extracted to a stable directory; client `command` is the
absolute path to its extracted `mcp/MetaBrain.Mcp.exe`, with exactly one `args` pair:
`["--data-root", "<owner-data-root>"]` or `["--config", "<service-settings.json>"]`.
The sibling `service/MetaBrain.Connections.exe` is part of the self-contained package and is
resolved automatically. Accepted Sprint 9 T7 evidence observed Codex CLI 0.164.0 and Oh My Pi
18.12.6; these are evidence points, not a minimum/maximum compatibility range.

## Fresh, existing and removal cases

| Case | Required behavior |
| --- | --- |
| Fresh roots | Install root has app/runtime files and install state; data root is distinct and non-overlapping (documented sibling convention). Install creates no vault and does not unlock, provision, autostart or start unless explicitly requested. Startup is locked. No global PATH/profile, ACL, service registration, firewall or permission changes. |
| Existing root, either installation order | CLI then UI, UI then CLI, or MCP with either owner frontend selects the same existing root, service settings, endpoint names, host and authoritative state; it must not overwrite settings or create a second instance/writer. Closing an entrypoint does not stop a host used by another. |
| Upgrade | Use one complete source/candidate release. Drain/stop the shared host before changing binaries; if it is live or drain fails, refuse with remediation, not success. Replace app files atomically as feasible, retain selected data root, settings and endpoints, and never overwrite vault/key, original sources, snapshots, backups or recovery material. No implicit schema migration beyond existing explicit owner contract. |
| Uninstall one frontend | Remove only that frontend's files/registration. Keep shared runtime while any other frontend or installed MCP integration needs it; do not break it or force it to another version. Preserve every data-root byte. |
| Uninstall final frontend / reinstall | Remove application/runtime only; data root remains untouched. Reinstall/reselect opens the same data using the owner's key. Restart always starts locked; no sessions/tokens revive. |

The current one-payload installer establishes current install/data root separation, in-place application upgrade and data-preserving uninstall (`docs/OPERATIONS.md:10-23,52-110`; `src/MetaBrain.Installer/InstallCommand.cs`; S9-T6 evidence). The separate-frontend component ownership cases above are the required extension, not historical proof. No S10 product behavior or lifecycle verification is asserted here.

## Platform and runtime

- Product architecture is Windows x64, self-contained; Linux/macOS/ARM are out of scope. `net10.0-windows`, Windows named pipes, DPAPI and WPF establish Windows-only APIs, not a complete product-tested OS matrix. `.NET 10` is LTS through November 2028 (Microsoft Learn: [releases and support](https://learn.microsoft.com/en-us/dotnet/core/releases-and-support)). Microsoft's [.NET 10 supported OS matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) lists Windows x64 support for Windows 11 23H2 Enterprise, 24H2, 25H2 and 26H1; Windows 10 21H2 Enterprise/IoT, 1809 Enterprise LTSC/IoT, and 1607 Enterprise LTSC; and Windows Server 2012/R2 (active ESU required), 2016, 2019, 2022, 2025 and Server 23H2. Server Core is listed separately; do not imply the WPF Owner UI is supported there. OS publisher lifecycle and .NET support can change; re-check the official source at actual release.
- **Product support statement for this candidate:** claim compatibility only for 64-bit Windows client/server OS releases that Microsoft lists as supported for .NET 10 at release time and that satisfy the product's required desktop/WPF capabilities; exclude unsupported Windows 10 editions/builds, Server Core/Nano for the UI, Linux/macOS and ARM. This is a framework-supported candidate boundary, not an assertion that every listed Windows SKU was exercised. S9 evidence documents real Windows x64 package/entrypoint/lifecycle tests but does not name a complete OS matrix. The final release record must retain this distinction and state any product-specific verified OS build(s) actually measured in T2–T7.
- End users need no SDK, repository, installed .NET runtime or PowerShell for self-contained assets. The single-file CLI and Owner UI setup hosts may use the .NET host's per-user temporary bundle-extraction cache under `%TEMP%\.net` at launch; they do not edit PATH, profiles or environment settings. Build requires the existing .NET 10 SDK/release script; the current script itself uses PowerShell. Codex CLI/OMP are optional consumers of the stdio MCP server, not app runtime prerequisites.

## Repository-local website and owner-controlled hosting

**Chosen website format/tooling:** static HTML/CSS under `website/`; no JavaScript application framework, frontend package manager or third-party site dependency. `powershell.exe -NoProfile -File scripts/site/build.ps1` validates the accepted candidate's provenance, all three asset sizes and SHA-256 hashes, and emits sanitized site content plus byte-identical local candidate copies named with the public download contract into ignored `dist/site/`. Publication mode links to those short names at the GitHub Release URL and reflects published status; it is only deployed after the owner-authorized in-place asset rename. The default local preview remains honest and links only to local copies; preview server is `python -m http.server --bind 127.0.0.1 --directory dist/site 8000`. Candidate identity and checksums are rendered from its manifest. References to internal worker/review/checkpoint records are omitted from the served runbook copy. The public release record is `docs/release-1.0.0.md`. Local output is not a public deployment.

**Owner-selected hosting:** the actual public repository is `https://github.com/triet4p/my-meta-brain`. GitHub Release `v1.0.0` and GitHub Pages at `https://triet4p.github.io/my-meta-brain/` are published; records P1/P2 document their current state. The Release's existing long-name assets must be renamed in place, without duplicate upload, after the authorized evidence review/checkpoint gates. Target-name collisions stop the operation. Deploy the updated published-status site only after the rename; no repository workflow or source/site publication is performed by this implementation worker.

## Signing and third-party redistribution

- **Owner decision, 2026-10-26:** release 1.0.0 is **unsigned**; no certificate is required for this release. State this prominently and truthfully; SHA-256 is an integrity check only and not publisher identity. Never claim a signature, guide users to disable SmartScreen/Defender or weaken a security policy, or manufacture a publisher identity. No certificate is currently established/needed for the chosen unsigned 1.0.0 contract.
- Existing first-party product `.csproj` files inspected (CLI, Owner UI, MCP, Installer) have project references to repository Connections and no direct external `PackageReference`; the self-contained .NET runtime and dependency notices remain governed by their included terms. The release assembler inventories dependency license texts separately from the project MIT license. Project license: the owner selected MIT for Meta Brain, preserving the repository-owner attribution as `Copyright (c) 2026 triet4p`. Root `LICENSE` applies to project source and is included separately in each distributed product package; it does not replace or relicense third-party notices or dependency terms. This is not a complete transitive publish inventory; applicable dependency terms remain separate.

## Evidence and ownership boundaries

This contract is derived from the current sprint plan, S9-T6 source/runbook/evidence, project files, `.gitignore`, Microsoft .NET support sources and owner decisions received 2026-10-26. It is not a product test, installer/site implementation, support guarantee beyond the explicit boundary above, release approval, publication authorization, or evidence review PASS. Main owns sprint plans and status. Worker-owned task evidence is `artifacts/sprint-10/task-1.md` (ignored). No changelog entry is appropriate for this documentation-only planning contract. Do not overwrite historical release assets or prior evidence.
