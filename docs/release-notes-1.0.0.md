# Meta Brain 1.0.0

Windows x64, self-contained, **unsigned**. SHA-256 verifies downloaded bytes only; it does not
authenticate the publisher.

## Downloads

- `MetaBrain-CLI-1.0.0.exe` — CLI setup.
- `MetaBrain-UI-1.0.0.exe` — graphical Owner UI setup.
- `MetaBrain-MCP-1.0.0.zip` — standalone MCP server package.

The owner-authorized in-place rename is complete; no duplicate assets were uploaded. The three
public download URLs resolve to the existing frozen assets. Byte sizes, SHA-256 values, internal
candidate provenance, installation details, licensing and known limits are in the
[`1.0.0 release record`](https://github.com/triet4p/my-meta-brain/blob/main/docs/release-1.0.0.md).

## What is included

- A dedicated CLI setup that installs `cli/MetaBrain.Cli.exe` without the Owner UI, SDK,
  repository or PowerShell glue.
- A dedicated graphical Owner UI setup that lets the owner choose application and data locations,
  install/update, and optionally open the console. It is not a CLI bootstrap.
- A standalone self-contained MCP ZIP with `mcp/MetaBrain.Mcp.exe`, the shared runtime, a package
  manifest and dependency/license notices. Extract it completely and register the absolute
  executable path with exactly one of `--data-root <owner-data-root>` or
  `--config <service-settings.json>`.

All three entrypoints use one shared per-user installation identity and one authoritative data
root. Installation does not create or unlock a vault, and every start is locked. Upgrade refuses to
replace a live shared host instead of reporting false success. Uninstall/reinstall preserves the
vault, source snapshots, backups and recovery material. Installs do not add PATH, profile,
autostart, service, registry or ACL entries.

## Compatibility

64-bit Windows releases that Microsoft lists as supported for .NET 10 at release time and that can
run the required desktop/WPF UI. Server Core/Nano Server cannot run the Owner UI. Linux, macOS and
ARM are out of scope. The product was not exercised on every supported Windows SKU. No installed
.NET runtime, SDK, source checkout or PowerShell is required.

Observed client evidence points: Codex CLI 0.164.0 and Oh My Pi 18.12.6. These are evidence points,
not a minimum/maximum support range.

## Known limits

- The release is unsigned. Do not disable SmartScreen, Defender or a security policy to install it,
  and do not treat a checksum as publisher authentication.
- The generated `RELEASE-NOTES.txt` and `THIRD-PARTY-NOTICES.txt` headings in the frozen assets
  show a cosmetic mojibake em dash (`â€”`). Machine-readable manifests are ASCII and all hashes
  verify; the text defect is preserved as a disclosed limit rather than silently changed.
- The CLI has no dedicated proposal-undo verb. Use the Owner UI Review Queue to undo a published
  proposal.
- Sprint 10 verifies release packaging, installers, site and documentation only. Product, client,
  security and owner-walkthrough acceptance is inherited from Sprint 9 and was not re-run.

## Licenses and notices

Meta Brain is licensed under the [MIT License](https://github.com/triet4p/my-meta-brain/blob/main/LICENSE).
Copyright (c) 2026 triet4p. The project license is included in each release asset.
The self-contained .NET runtime packages are separately MIT-licensed and carry their own
license texts and third-party notices; the MCP ZIP includes per-dependency license texts
and `package-manifest.json`. Third-party components retain their own terms.
