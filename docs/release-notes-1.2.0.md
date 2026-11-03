# Meta Brain 1.2.0

Windows x64, self-contained, **unsigned**. SHA-256 verifies downloaded bytes only; it does not
authenticate the publisher.

**Status: published 2026-11-01 as `v1.2.0`.** The owner authorized publication after the reviewed
candidate; the public assets were uploaded and smoke-verified against the frozen sizes and hashes in
the [`1.2.0 release record`](release-1.2.0.md). The verification described here was performed on the
prepublication candidate and is retained unchanged; publication itself is recorded separately.

## Downloads

1.2.0 ships exactly two products. The public filenames below are the download-name mapping the
owner-controlled publication uses; the frozen candidate filenames and their exact sizes and
SHA-256 values are in the [release record](release-1.2.0.md).

- `MetaBrain-CLI-1.2.0.exe` — CLI setup. Installs `cli\MetaBrain.Cli.exe`, the shared runtime and the
  shared installer; installs and registers the CLI component only.
- `MetaBrain-MCP-1.2.0.zip` — standalone MCP server package. Extract it completely and register the
  absolute `mcp\MetaBrain.Mcp.exe` path.
- There is **no** 1.2.0 Owner UI setup, `owner-ui` component or UI download.

The released 1.1.0 Owner UI and every 1.0.0/1.1.0 asset stay available and byte-unchanged at
[`v1.1.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.1.0) and
[`v1.0.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.0.0). 1.2 does not upgrade,
replace, migrate or withdraw either of them.

## What is new in 1.2.0

- **Encrypted owner workspace.** `owner workspace-status`, `workspace-tree`, `workspace-mkdir`,
  `workspace-import`, `workspace-read` and `workspace-revise` keep a private logical folder tree
  inside the unlocked vault and store markdown, CSV, JSONL, UTF-16 text, opaque binary and zero-byte
  files as exact original bytes with retained content revisions. `workspace-rename`,
  `workspace-move`, `workspace-copy`, `workspace-delete-preview`, `workspace-delete`,
  `workspace-restore` and `workspace-tombstones` change identity only under explicit tree/node
  preconditions and keep deletion reversible. `workspace-anchor-create` and `workspace-anchor-list`
  pin a citation to a document id, content revision, byte range and range digest, never to a path.
  Names, paths, node metadata and bytes are encrypted with the vault data key; the authenticated
  workspace format and metadata schema stay version 1.
- **Plaintext export and portability.** `workspace-export-preview` and `workspace-export` write
  exactly the previewed scope as original bytes plus one versioned
  `metabrain-workspace-export.json` metadata document (`format: metabrain-workspace-export/1`,
  `metadataSchemaVersion: 1`) into one new destination directory. A file may pin one retained
  revision with `--content-revision`. A typed `EXPORT` confirmation and the preview token are
  required, and an existing entry is never overwritten.
- **Backup, restore and irreversible purge.** `owner backup` now captures the workspace identity,
  tree, retained original bytes, anchors and tombstones together with the rest of the vault, and
  `workspace-purge-preview`/`workspace-purge --confirm PURGE --reason <text>` erase one previewed
  tombstone group while recording the owner's reason and every purged identity in the encrypted
  purge ledger. A restore re-applies that ledger, so an older backup cannot resurrect a purged
  identity, and a restore of a live vault whose purge ledger cannot be read fails closed with
  `restore_requires_unlock`.
- **Secure provider key entry.** `owner provider-config` reads the provider key from the non-echo
  secure prompt or one `--api-key-stdin` line; the developer `--from-env` path is gone, so no
  credential is read from the environment, argv, a plaintext file or a log.
- **CLI-complete owner workflows.** Every owner workflow this release changes or adds is available
  from `cli\MetaBrain.Cli.exe` and its `owner --help` / `owner <verb> --help`; the CLI is the full
  owner path in 1.2.

The shared instance lifecycle, the scope/permission model, the sharing review flow, vault format
upgrade, source enrollment and raw preservation are the ones accepted for 1.1.0. No new provider
call, telemetry, sync, service registration, autostart or PATH change is introduced.

## Compatibility and the 1.1 support transition

64-bit Windows releases that Microsoft lists as supported for .NET 10 at publication time. The CLI
and the MCP server need no desktop/WPF capability, so Server Core/Nano Server is not excluded by a
UI requirement. Linux, macOS and ARM are out of scope. The product was not exercised on every
supported Windows SKU. No installed .NET runtime, SDK, source checkout or PowerShell is required.

Observed client evidence points: Codex CLI 0.164.0 and Oh My Pi 18.12.6. These are evidence points,
not a minimum/maximum support range.

1.2.0 publishes the CLI and the MCP server only. A 1.2 setup **refuses in place** an installed
pre-1.2 (1.1.0 WPF) or unknown/mixed installation before it stops that host or writes into either
root, and names the supported transition: install 1.2 into a **separate install root** with its own
**data root** and distinct `--control-pipe`/`--agent-pipe` endpoints. The 1.1 application, data,
endpoints and any running 1.1 host stay untouched; nothing is uninstalled, replaced or migrated
automatically, and sharing one live data root or running two writers is refused. `--component
owner-ui` is refused with the same message.

A 1.1 vault is never read, converted, deleted or migrated by 1.2. A separately confirmed manual
transition is the owner's own act: stop the 1.1 host, keep both 1.1 roots, install 1.2 beside them,
then create or unlock the 1.2 vault and copy only what the owner chooses. The frozen 1.1.0 Owner UI
is not updated by 1.2 and makes no 1.2 runtime or data compatibility claim.

## Signing and licensing

- The release is **unsigned**. Do not disable SmartScreen, Defender or a security policy to install
  it, and do not treat a checksum as publisher authentication.
- Each setup/package carries the project `LICENSE` (MIT, `Copyright (c) 2026 triet4p`),
  `RELEASE-NOTES.txt`, `THIRD-PARTY-NOTICES.txt` and the bundled dependency license texts. The MCP
  ZIP also carries `package-manifest.json`. The only redistributed third-party component is the
  self-contained .NET runtime package `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT, with
  notices). No WindowsDesktop/WPF runtime is part of either 1.2.0 asset.

## Known limits

- **Not published.** The candidate, its hashes and this page are prepublication material. The public
  download names become real only through the owner-authorized checklist in the release record.
- **One workspace act carries at most 1 MiB** of original bytes in 1.2.0 (`workspace_too_large`).
- **Exports leave the vault.** An export writes plaintext original bytes plus metadata outside the
  vault; locking, deleting or purging later does not revoke that copy. A purge also cannot recall
  backups or exports taken before it, and it says so in its own preview.
- **A restore carries no live authority.** A restored vault comes back locked and without sessions,
  grants or tokens; the previously consumed token state and the purge ledger are preserved.
- **The vault is lost if the owner loses both the passphrase and the recovery key.** There is no
  reset and no back door.
- **No 1.2 Owner UI.** The frozen 1.1.0 console is not guaranteed to read 1.2 runtime or workspace
  data, and a 1.2 setup does not install, upgrade or remove it.
- **Static installer banner.** The installer prints `vault state=uninitialized` even when upgrading
  an existing locked data root; this is pre-existing messaging, not a data-state report.
- **Inherited functional acceptance.** Unchanged owners/agent surfaces inherit their accepted
  earlier evidence; the [1.2.0 release record](release-1.2.0.md) states which release-scope checks
  were completed for this candidate. It does not claim a provider or official-client walkthrough.
