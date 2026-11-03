# Meta Brain 1.3.0

Windows x64, self-contained, **unsigned**. SHA-256 verifies downloaded bytes only; it does not
authenticate the publisher.

**Status: published 2026-11-03 as `v1.3.0`.** The owner authorized publication after the reviewed
candidate, evidence review, exact-snapshot checkpoint and final differential; the public assets were
uploaded from the exact frozen candidate bytes under the public names below, and the sizes and
SHA-256 values are recorded in the [`1.3.0 release record`](release-1.3.0.md). The verification
described here was performed on the prepublication candidate and is retained unchanged; publication
and the public smoke are recorded separately.

## Downloads

1.3.0 ships exactly two products. The public filenames below are the download-name mapping the
owner-controlled publication uses; the frozen candidate filenames and their exact sizes and
SHA-256 values are in the [release record](release-1.3.0.md).

- `MetaBrain-CLI-1.3.0.exe` — CLI setup. Installs `cli\MetaBrain.Cli.exe`, the shared runtime and the
  shared installer; installs and registers the CLI component only. The document bridge and its
  browser shell are compiled into this runtime, so the optional `open` session needs nothing extra
  except an installed Chromium-based browser.
- `MetaBrain-MCP-1.3.0.zip` — standalone MCP server package. Extract it completely and register the
  absolute `mcp\MetaBrain.Mcp.exe` path. The agent tool surface is unchanged.
- There is **no** 1.3.0 Owner UI setup, `owner-ui` component or UI download, and no separate web
  console.

The published 1.2.0, 1.1.0 and 1.0.0 assets stay available and byte-unchanged at
[`v1.2.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.2.0),
[`v1.1.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.1.0) and
[`v1.0.0`](https://github.com/triet4p/my-meta-brain/releases/tag/v1.0.0). This release does not
upgrade, replace, migrate or withdraw the 1.1.0 WPF console.

## What is new in 1.3.0

- **Owner CLI workspace search.** `owner workspace-search --control-pipe <pipe> --query <literal>
  [--folder-id <live folder id>] [--format text|json]` searches exact case-insensitive file names,
  logical paths, Markdown headings and decoded UTF-8/UTF-16 text in the encrypted workspace. Symbol
  queries require identifier boundaries, phrases stay contiguous, and ranking is deterministic
  (exact name, exact path, other name, path, heading, content) with no stemming, synonym or semantic
  expansion. Every hit reports the live file revision and content digest, and text hits carry the
  byte offset/length and a digest of the exact matched bytes. `--folder-id` narrows to one live
  folder and grants nothing; a file id is refused with `workspace_filter_not_folder`.
- **Optional foreground document session.** `MetaBrain.Cli.exe open <file-id> --control-pipe <pipe>
  [--revision <n>] [--anchor-offset <bytes> --anchor-length <bytes> --anchor-sha256 <digest>]
  [--no-browser] [--lifetime-seconds <n>]` serves exactly one live workspace file at exactly one
  content revision to a local browser. The page offers inert Markdown preview (raw HTML stays text,
  no remote images/resources), a paged CSV table with a raw-source toggle, bounded text/JSONL pages
  and an explicit raw-text editor for supported files. `--no-browser` prints the bound profile and
  the loopback URL without launching a browser.
- **Explicit revision-gated save and conflicts.** Save is a separate explicit action carrying the
  bound expected revision through the shared workspace API. A concurrent revision is reported as a
  conflict and the draft stays in the page; it is never silently overwritten and is never autosaved
  to browser storage. A no-op save creates no revision, and preview/source toggles do not rewrite the
  file. Encoding and newline policy: UTF-8 (with or without BOM) and consistent LF/CRLF/CR are
  editable with the original convention restored on save; UTF-16, mixed newlines, NUL-bearing,
  invalid-UTF-8 and unrecognised suffixes are truthful read-only states.
- **Fail-closed session lifecycle.** Ctrl+C, foreground CLI exit, the optional lifetime, vault lock,
  service restart/disconnect or a changed unlock epoch stop the bridge and clear the page; browser
  reload/back refuses to revive the capability, and a stale in-flight reply cannot repopulate a
  revoked page. The capability is a per-process value in the served page and an `X-MetaBrain-Session`
  header, never a URL, cookie, log line or file. A foreign Origin/Host, cross-site metadata, a query
  string or a missing/wrong capability is refused.
- **Revised open-at-location.** A search hit can be opened at its exact revision and byte range; a
  stale revision, an out-of-range span or a mismatched digest is refused with
  `workspace_anchor_stale` / `workspace_anchor_invalid` / `workspace_anchor_mismatch`.

## Compatibility and upgrade

- The vault format, workspace format and metadata schema are unchanged from 1.2.0 (workspace format
  version 1). This release performs **no automatic migration**; existing 1.2 workspaces open exactly
  and remain byte-compatible.
- **Upgrade in place from 1.2.0 is supported.** The 1.3.0 CLI setup upgrades a 1.2.0 CLI/MCP
  installation in the same install root: it drains/stops the shared host, replaces the application
  files atomically as feasible, keeps the selected data root, vault, settings and pipe endpoints, and
  changes no owner data. Re-running the setup is the ordinary reinstall path.
- **Pre-1.2 installations are refused in place.** A 1.1.0 WPF installation, an unknown/mixed
  component registration, a data root owned by another installation or a live endpoint speaking
  another protocol revision is refused before the host is stopped or any file changes. Install this
  release beside it with separate install/data roots and distinct endpoints; the old installation and
  its data are untouched.
- **Uninstall keeps the data root.** Removing the CLI (or the final component) removes application
  files only; the data root, vault, backups and recovery material remain. Reinstalling and reselecting
  the same data root opens the same data with the owner's credential.
- **Rollback.** To return to 1.2.0, stop the 1.3 host and run the published 1.2.0 CLI setup as an
  upgrade over the same install root; the data root is preserved. Keep an encrypted `owner backup`
  before changing binaries. Downgrading the application does not rewrite the vault or workspace, and
  the 1.2.0 record remains the authority for its own behavior.

## Security and boundaries

- The browser receives no vault key, filesystem handle, private path or general owner API. Every
  read and write goes back through the existing owner control pipe; loopback alone, an `Origin`
  header or a request body never grants authority.
- Responses are `Cache-Control: no-store` with a restrictive CSP; there are no cookies, CORS headers,
  browser storage, service workers, private URLs or network egress, and the bridge writes **no**
  plaintext HTML, draft or temp file. Only the served page carries the capability.
- The product does not claim absolute erasure of browser-managed memory/cache, extensions, DevTools,
  OS swap or copies made outside it, and it never changes browser or host policy. Lock and exit revoke
  what the product controls.
- The same owner-only operations remain available through the CLI without any browser; the document
  surface is not an agent tool and the MCP tool list is unchanged.

## Signing and licensing

- Both assets are **unsigned**. SHA-256 is an integrity check only; it does not authenticate the
  publisher. Never disable SmartScreen, Defender or a security policy to install.
- Project license: MIT, `Copyright (c) 2026 triet4p`. The self-contained runtime contains
  `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT) and no WindowsDesktop/WPF runtime or external
  NuGet package. Per-dependency license texts and third-party notices ship inside each asset.

## Known limits

- The MCP tool list is unchanged by this release; workspace search and `open` are owner CLI surfaces,
  and no agent or MCP client can reach the workspace.
- The optional `open` session needs an installed Chromium-based browser on the same Windows x64
  machine; without one, use the CLI read/revise path. Non-Chromium engines are not supported in this
  release.
- Workspace search is lexical only: no stemming, synonyms, semantic retrieval or ranking by model.
- A workspace act still carries at most 1 MiB of original bytes; larger files are refused
  (`workspace_too_large`) rather than truncated.
- Source-session JSONL records remain read-only and are never served by the document session.
- The product is Windows x64 only, and its supported-OS statement is the framework-supported boundary
  in the [release contract](release-contract.md#product-and-support-matrix-for-130-s13-t6); not every
  listed Windows SKU was exercised.
- The exact candidate identity, asset sizes, SHA-256 values, provenance, dependency/license
  inventories and the verification actually performed are in the
  [1.3.0 release record](release-1.3.0.md).
