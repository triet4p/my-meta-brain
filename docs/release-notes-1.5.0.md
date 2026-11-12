# Meta Brain 1.5.0

Windows x64, self-contained, **unsigned**. SHA-256 verifies downloaded bytes only; it does not
authenticate the publisher.

**Status: published 2026-11-09 as `v1.5.0`.** The owner authorized publication after the reviewed
candidate, evidence review, exact-snapshot checkpoint and final differential; the public assets were
uploaded from the exact frozen candidate bytes under the public names below, and the sizes and
SHA-256 values are recorded in the [`1.5.0 release record`](release-1.5.0.md). The verification
described here was performed on the prepublication candidate and is retained unchanged; publication
and the public smoke are recorded separately.

## Downloads

1.5.0 ships exactly two products. The public filenames below are the published download-name mapping;
the frozen candidate filenames and the exact sizes and SHA-256 values are in the
[release record](release-1.5.0.md).

- [`MetaBrain-CLI-1.5.0.exe`](https://github.com/triet4p/my-meta-brain/releases/download/v1.5.0/MetaBrain-CLI-1.5.0.exe)
  — CLI setup. Installs `cli\MetaBrain.Cli.exe`, the shared runtime and the shared installer, and
  registers the CLI component only. The owner CLI carries every cognitive workflow plus the workspace,
  hybrid search and optional `open` document session. No new Owner UI setup is shipped.
- [`MetaBrain-MCP-1.5.0.zip`](https://github.com/triet4p/my-meta-brain/releases/download/v1.5.0/MetaBrain-MCP-1.5.0.zip)
  — standalone MCP server package. Extract it completely and register the absolute
  `mcp\MetaBrain.Mcp.exe` path. The scoped agent surface is unchanged at 22 tools and does not
  expose owner workspace search.
- There is **no** 1.5.0 Owner UI setup, `owner-ui` component or UI download, and no separate web
  console.

The published 1.0.0–1.4.0 assets stay available and byte-unchanged. This release does not rebuild,
rename or withdraw them, and it does not upgrade or replace the 1.1.0 WPF console.

## What is new in 1.5.0

- **Encrypted, structure-aware passage index.** `owner workspace-index-status`,
  `workspace-index-rebuild` and `workspace-index-anchors` maintain one authenticated
  AES-GCM-encrypted posting/semantic generation from the unlocked owner workspace. It indexes
  Markdown headings and fenced code, CSV headers and complete quoted records, known/raw JSONL
  records and plain text, and every anchor cites the exact file, content revision and byte-range
  digest. A rebuild revalidates revisions and data formats and publishes atomically; a failed
  rebuild preserves the prior generation.
- **Scoped hybrid workspace search.** `owner workspace-search --hybrid true` runs one server-side
  retrieval pass that keeps the existing exact/lexical/heading evidence and adds local semantic
  candidates from the pinned model. Folder, field, currentness and content-date filters apply before
  results and totals are disclosed. Every result is labelled `literal_match` or
  `semantic_candidate`; a semantic candidate is an unconfirmed neighbour, **not an answer or
  supporting evidence**. Literal evidence and exact symbols are not displaced by semantic similarity,
  ties are deterministic, and duplicates are merged.
- **Purpose-aware recall and regions.** `--purpose` accepts `auto`, `experience`, `temporal`,
  `intention`, `action-effect` and `general`; it applies deterministic rule-first routing after the
  owner's filters, joins cognitive annotations, lifecycle and recall state on exact revisions, and
  returns region representatives, scoped counts and the reason each region was chosen. A region
  summary never reveals files outside the owner's scope. Action/effect associations do not establish
  causality, and intention mentions or acknowledgements do not establish completion.
- **Search UX, background refresh and exact open.** Successful owner content mutations queue one
  coalesced background passage refresh inside the unlocked epoch; vault lock cancels it, removes the
  `.building` stage, clears the model selection and decoded cache, and publishes nothing partial.
  `--open-rank N` opens a hit from the returned page at its exact file, content revision and byte
  anchor in the existing document bridge; stale or incomplete locations refuse. The browser stays an
  optional document-only preview/edit surface.
- **Explicit local model, no implicit egress.** Hybrid indexing and search require the owner to
  select a local E5 model directory; the product never downloads model weights, calls a cloud
  embedding provider, persists the selected path or falls back silently to lexical-only output.
- **Same two-product release contract.** One shared runtime and one version for the CLI, MCP server,
  shared host and installer (1.5.0); candidate-bound manifests, per-file and embedded-payload SHA-256
  values, dependency/license inventories, native tokenizer notices and truthful unsigned status.

## Prerequisites and installation

- **Windows x64** on the framework-supported boundary in the
  [release contract](release-contract.md#product-and-support-matrix-for-150-s15-t6). The CLI setup
  and MCP package are self-contained; they need no installed .NET runtime, SDK, repository or
  PowerShell at runtime.
- **Microsoft Visual C++ v14 x64 Redistributable, version 14.43.34808 or later**, installed by the
  owner from Microsoft's
  [latest supported downloads](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170).
  Setup, direct install and MCP startup check it read-only and refuse before touching files or a data
  root when it is missing or too old. Meta Brain does not bundle, download, chain or install it.
  Windows 10+ includes the OS Universal CRT, which does not supply the required `VCRUNTIME140.dll`
  and `VCRUNTIME140_1.dll` imports.
- **Hybrid search only:** an owner-selected local model directory for the pinned E5 revision
  `614241f622f53c4eeff9890bdc4f31cfecc418b3`. The expected directory ends in `model/` and contains
  `onnx/model.onnx` (470,268,510 bytes; SHA-256
  `ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665`) and `onnx/tokenizer.json`
  (17,082,730 bytes; SHA-256
  `0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39`). The seven approved
  model/tokenizer/config assets occupy 492,421,754 bytes in the evaluation staging set; that is a
  staged asset size, not an installed-product footprint. Lexical search, capture, workspace, the
  cognitive workflows and the document session do not need a model.
- Model files are not part of the release and are never fetched by Meta Brain. Missing or invalid
  assets refuse with `workspace_index_model_missing` or `workspace_index_model_invalid`; the
  model directory and decoded passage reuse are process-memory-only and are cleared on vault lock or
  host exit.

Example hybrid path (replace the placeholders with the actual pipe and model directory):

```powershell
$cli = '<install-root>\cli\MetaBrain.Cli.exe'
$model = '<owner-selected-pinned-E5-model-directory>'
& $cli owner workspace-index-rebuild --control-pipe '<control-pipe>' --model-directory $model
& $cli owner workspace-index-status --control-pipe '<control-pipe>'
& $cli owner workspace-search --control-pipe '<control-pipe>' --query 'your query' --hybrid true --model-directory $model --format json
```

## Compatibility and upgrade

- The isolated 1.4.0 → 1.5.0 verification required no migration: every pre-upgrade canonical vault
  file, `service-settings.json` and pipe endpoint stayed byte-identical, and only the data-root
  ownership marker changed. The passage/vector generation is additional encrypted state under the
  existing vault; an upgraded 1.5 instance reports it as `not_materialized` until the owner explicitly
  rebuilds it, and no model is selected automatically.
- **Upgrade in place from 1.2.0, 1.3.0 or 1.4.0 is supported.** The 1.5.0 CLI setup drains/stops the
  shared host, replaces the application files, keeps the selected data root, vault, settings and pipe
  endpoints, and changes no owner data. The isolated 1.4.0 → 1.5.0 verification changed only the
  data-root ownership marker and kept every pre-upgrade canonical vault file byte-identical.
  Re-running the setup is the ordinary reinstall path.
- **Pre-1.2 installations are refused in place.** A 1.1.0 WPF installation, an unknown/mixed
  component registration, a data root owned by another installation or a live endpoint speaking
  another protocol revision is refused before the host is stopped or any file changes. Install this
  release beside it with separate install/data roots and distinct endpoints; the old installation and
  its data are untouched.
- **Uninstall keeps the data root.** Removing the CLI (or the final component) removes application
  files only; the data root, vault, backups and recovery material remain. Reinstalling and
  reselecting the same data root opens the same data with the owner's credential.
- **Rollback.** To return to 1.4.0, stop the 1.5 host, take an encrypted `owner backup`, and run the
  published 1.4.0 CLI setup as an upgrade over the same install root; the data root is preserved. A
  1.4 build does not provide hybrid search, so take and verify the backup before downgrading. The
  1.4.0 record remains the authority for 1.4 behavior.

## Security and attribution boundaries

- Workspace content, the passage/vector generation and annotations are encrypted with the vault data
  key. Every read, rebuild and search goes through the owner control pipe under the unlocked vault;
  a locked service refuses private reads, cancels background work and clears decoded caches.
- Scope is applied before disclosure: folder, field, currentness and content-date filters bound the
  result set and totals, and a region summary never reveals an out-of-scope file.
- Hybrid output is locally computed relevance, not verified fact. There is no approved similarity
  cutoff, no general natural-language abstention guarantee and no automatic truth promotion. Exact
  symbols, currentness and source anchors remain separate correctness requirements.
- The product makes no model or provider download and no cloud embedding call. A separately
  consented provider/model job remains governed by the existing job consent and budget and is not
  part of this release's local verification.
- The MCP agent surface is unchanged at 22 tools with no owner-authority tool; cognitive records,
  the passage index and the document bridge are not agent tools.
- The optional browser session remains document-only: the browser receives no vault key, filesystem
  handle, private path or owner search API, and the product writes no plaintext HTML, draft or temp
  file.

## Signing and licensing

- Both assets are **unsigned**. SHA-256 is an integrity check only; it does not authenticate the
  publisher. Never disable SmartScreen, Defender or a security policy to install.
- Project license: MIT, `Copyright (c) 2026 triet4p`. Each asset carries the project `LICENSE`,
  per-dependency license texts and third-party notices. The self-contained runtime includes
  `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT), `Microsoft.ML.OnnxRuntime` and
  `Microsoft.ML.OnnxRuntime.Managed` 1.31.1, `Tokenizers.DotNet` 1.4.1 and
  `System.Numerics.Tensors` 10.0.14. The selected native tokenizer DLL ships with the complete mapped
  notices for its 86-package Cargo graph. The Microsoft Visual C++ runtime is not included and is not
  covered by those notices; its owner installation is separate. Nothing here is a legal conclusion.

## Known limits

- Windows x64 only on the framework-supported boundary in the
  [release contract](release-contract.md#product-and-support-matrix-for-150-s15-t6); not every listed
  Windows SKU was exercised.
- Hybrid search quality is established only on the frozen synthetic evaluation and held-out query
  sets. It does not establish quality on owner data, large workspaces or every query family, and a
  semantic candidate is not evidence.
- Integrated latency and memory figures from the task evidence are host- and fixture-specific
  observations, not approved SLAs. The first query after a locked restart reloads the model.
- The interactive setup-GUI refusal on a machine without the VC++ runtime was verified at
  code-path/registry level, not as a full clean-machine installation.
- The optional `open` document session needs an installed Chromium-based browser and is not a search
  console. Browser-managed memory, extensions and OS swap cannot be erased by the product.
- A workspace act carries at most 1 MiB of original bytes; larger files are refused rather than
  truncated. Source-session JSONL records remain read-only and are never served.
- The exact candidate identity, asset sizes, SHA-256 values, provenance, dependency/license
  inventories and the verification actually performed are in the
  [1.5.0 release record](release-1.5.0.md).

## Distribution status

1.5.0 is published as `v1.5.0` on 2026-11-09. The public assets are
`MetaBrain-CLI-1.5.0.exe` (233,550,554 bytes; SHA-256
`cc4c05d5d9bf73247dbca640f2b9c468c6f7b2d4340ea99f84664f7dad62aefd`) and
`MetaBrain-MCP-1.5.0.zip` (89,848,853 bytes; SHA-256
`5ead1bc922596e719e158cf523eb6d4939bee9a32b368c7e03ecc28e117f9b94`), plus
`candidate-manifest.json`, `SHA256SUMS.txt` and `LICENSE`; the source/tag is `05ca71d1`, candidate
`100079356d73`, and the publication-docs checkpoint is `9780091f`. See
[`release-1.5.0.md`](release-1.5.0.md) for the full record. Public smoke passed and the live website
is deployed.

Useful native-payload provenance retained: the selected native tokenizer DLL
(`a7ca979443378649ad2e18ab923f684390c2f57ff16f611f8671268e4da5986b` is not used) remains in ignored
staging at `runtime/s15-t6-native-build/target/x86_64-pc-windows-msvc/release/hf_tokenizers.dll`
(3,198,464 bytes; SHA-256
`4595800ae02f9c7c96d583347e6cf1e2ddd17c26348ac8169c8314ca28b7480d`), outside versioned product
source. The versioned Cargo lock, build provenance and complete native notice text remain under
`src/MetaBrain.Connections/native/`. The native notices, the Microsoft runtime prerequisite and the
candidate/package review are distinct gates.
