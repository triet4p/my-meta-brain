# Meta Brain 1.1.0

Windows x64, self-contained, **unsigned**. SHA-256 verifies downloaded bytes only; it does not
authenticate the publisher.

## Downloads

- [`MetaBrain-CLI-1.1.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0/MetaBrain-CLI-1.1.0.exe)
  — CLI setup.
- [`MetaBrain-UI-1.1.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0/MetaBrain-UI-1.1.0.exe)
  — graphical Owner UI setup.
- [`MetaBrain-MCP-1.1.0.zip`](https://github.com/triet4p/my-meta-brain/releases/download/v1.1.0/MetaBrain-MCP-1.1.0.zip)
  — standalone MCP server package.

The public names are a download mapping over the frozen candidate assets; the internal candidate
filenames, manifest and byte provenance are unchanged. Byte sizes, SHA-256 values, source
provenance, installation details, licensing and known limits are in the
[`1.1.0 release record`](https://github.com/triet4p/my-meta-brain/blob/main/docs/release-1.1.0.md).
The published 1.0.0 release and its assets are unchanged and remain available at
[`v1.0.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.0.0).

## What is new in 1.1.0

- **Owner folder enrollment.** The owner selects one absolute folder, chooses recursion and
  include/exclude globs, inspects a bounded metadata-only preview and confirms by typing `ENROLL`.
  Only the selected root is examined; reparse points below it and excluded paths are never read.
  Enrollment registers encrypted source metadata, not source bodies.
- **Raw-preserved partial ingestion.** Reference-only stays the default. With a separate explicit
  opt-in and a future UTC retention deadline, complete unsupported or encoding-invalid records are
  kept byte-for-byte in the encrypted journal together with the checkpoint, instead of blocking at
  their byte offset. Source status reports availability, preservation, interpretation and
  derivation as four independent facets.
- **Folder membership refresh.** Manual and background refresh can discover current and future
  matching regular files under an opted-in folder. Consent is off by default, per source pause,
  revoke and opt-out remain vetoes, and folder opt-out or vault lock cancels an active read without
  advancing the checkpoint. Deleted members stay enrolled and report missing; nothing is
  auto-revoked, auto-deleted or auto-purged.
- **Owner console Sources view.** The console offers the same folder preview/enroll flow, shows the
  saved/preserved counts, interpretation, derivation and retention metadata a source reports, and
  never renders transcript or preserved bytes.
- **Retention is a read deadline, not a purge.** After `retainUntilUtc` passes, reading that span is
  refused with `source_preserved_record_expired`; the span and its deadline stay listed, the four
  facets and the journal revision do not change, and deletion still needs an explicit owner action.

The CLI, Owner UI and MCP entrypoints, the shared runtime, the scope/permission model, the sharing
review flow and the installer lifecycle are the ones accepted for 1.0.0, with the version raised to
1.1.0. No new provider call, telemetry, sync, service registration, autostart or PATH change is
introduced.

## Compatibility

64-bit Windows releases that Microsoft lists as supported for .NET 10 at release time and that can
run the required desktop/WPF UI. Server Core/Nano Server cannot run the Owner UI. Linux, macOS and
ARM are out of scope. The product was not exercised on every supported Windows SKU. No installed
.NET runtime, SDK, source checkout or PowerShell is required.

Observed client evidence points: Codex CLI 0.164.0 and Oh My Pi 18.12.6. These are evidence points,
not a minimum/maximum support range.

Upgrading from 1.0.0 uses the 1.1.0 setup on the same install and data roots after the shared host
drains/stops; the vault, key envelope, settings, endpoints and backups are preserved. A 1.1.0
journal v2 source fails closed on a 1.0.0 binary instead of being read or rebuilt, and 1.1.0 never
downgrades a v2 journal back to v1.

## Signing and licensing

- The release is **unsigned**. Do not disable SmartScreen, Defender or a security policy to install
  it, and do not treat a checksum as publisher authentication.
- Each setup/package carries the project `LICENSE` (MIT, `Copyright (c) 2026 triet4p`),
  `RELEASE-NOTES.txt`, `THIRD-PARTY-NOTICES.txt` and the bundled dependency license texts. The MCP
  ZIP also carries `package-manifest.json`. The only redistributed third-party components are the
  self-contained .NET runtime packages `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` and, for the
  Owner UI, `Microsoft.WindowsDesktop.App.Runtime.win-x64 10.0.14` (both MIT, with notices).

## Known limits

- An installed 1.0.0 binary does not gain the 1.1.0 folder/raw surfaces; install the 1.1.0 setups
  on the same data root to use them.
- Raw preservation, folder refresh and folder enrollment each need their own explicit owner consent.
  Nothing is enabled by upgrading.
- The installer prints a static `vault state=uninitialized` banner even when it upgrades an existing
  locked data root; this is existing 1.0.0/1.1.0 messaging, not a data state report.
- Preserved spans are private encrypted data: they are covered by vault retention, purge, backup and
  export semantics and never grant an agent new access. Diagnostics carry only a code and offset,
  never transcript content.
- Functional acceptance of unchanged surfaces is inherited from the accepted earlier releases. This
  release adds folder/partial-flow evidence for the changed surfaces; it does not re-run provider,
  client or owner-walkthrough flows.
