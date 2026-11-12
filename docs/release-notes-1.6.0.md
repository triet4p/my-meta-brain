# Meta Brain 1.6.0

Windows x64, self-contained, **unsigned**. SHA-256 verifies downloaded bytes only; it does not
authenticate the publisher.

**Status: prepared candidate, not published.** The two assets were assembled and verified locally
for Sprint 16 and exercised on their actual packaged surfaces. No `v1.6.0` tag, GitHub Release or
public download URL exists, and none may be described as live before the owner authorizes
publication after the required reviews and checkpoints. Exact candidate identity, sizes and
SHA-256 values are in the [1.6.0 release record](release-1.6.0.md).

## Downloads

1.6.0 ships exactly two products. When published, the public names below are the download-name
mapping; the frozen candidate filenames and the exact sizes and SHA-256 values are in the
[release record](release-1.6.0.md).

- `MetaBrain-CLI-1.6.0.exe` — CLI setup. Installs `cli\MetaBrain.Cli.exe`, the shared runtime and
  the shared installer, and registers the CLI component only. The owner CLI carries every cognitive
  workflow plus the workspace, typed-relation, hybrid-search and optional `open` document session.
  No new Owner UI setup is shipped.
- `MetaBrain-MCP-1.6.0.zip` — standalone MCP server package. Extract it completely and register the
  absolute `mcp\MetaBrain.Mcp.exe` path. The scoped agent surface is unchanged at 22 tools and does
  not expose owner workspace search or owner relation acceptance.
- There is **no** 1.6.0 Owner UI setup, `owner-ui` component or UI download, and no separate web
  console, graph workbench or relation browser.

The published 1.0.0–1.5.0 assets stay available and byte-unchanged. This release does not rebuild,
rename or withdraw them, and it does not upgrade or replace the frozen 1.1.0 WPF console.

## What is new in 1.6.0

- **Shared typed workspace relations.** The owner service and CLI create, inspect and
  revision-gate relations between stable workspace file, folder and passage endpoints while keeping
  the existing seven kinds and their stored direction. `related_to` remains an identity-only
  association; the semantic kinds pin the exact retained content revision and digest (full file or
  byte-range passage), so a source change is reported as review-needed instead of silently
  reinterpreting the edge. Owner, imported-file and AI origins stay distinct; imported and AI
  suggestions remain `proposed`, and similarity is never promoted to `supports`.
- **Durable relation state and history.** Accept, reject, retract, unlink and restore are
  revision-preconditioned acts with attributed prospective history. Rename and move preserve the
  stable endpoint identity; a rejected relation is terminal; undo returns the edge to `proposed`,
  never to `accepted`. Constrained `derived_from`/`supersedes` cycles are refused across stored
  states. Explicit edges and their history are encrypted canonical records; adjacency stays a
  rebuildable derivative, and an encrypted backup/restore preserves edges, pins and history.
- **Bounded relational candidate discovery.** Owner hybrid search keeps a separate
  `relationCandidates` array derived from the returned direct-hit page. Only accepted/current
  relations in stored direction expand, at most two hops and a request-limit number of distinct
  candidates. Every file and passage endpoint is checked against folder and exact-revision scope;
  folder endpoints are checked by stable identity and never expand to descendants. Candidates are
  not merged into direct hits or totals, and they disclose no relation reason, traversal path, text
  or out-of-scope name.
- **Relational ranking and explanations.** The same response carries one deterministic
  `rankedEvidence` list: direct literal evidence first, relation-derived evidence next, direct
  semantic-only evidence last. Why/decision questions surface supports, contradictions, derived and
  applied evidence ahead of identity links, and contradictions are retained and labelled rather
  than filtered. Each relation entry exposes stable reason codes and its hop-by-hop stored path.
  Duplicate links or alternate paths to the same source select one path and add no rank or
  confidence; nothing ranks by recency or link count, and no score exists.
- **Owner inspection by exact endpoint.** `owner relation-list --workspace-node-id` selects only
  edges naming that exact stable file, passage-file or folder endpoint, with provenance, retained
  pins and service-computed `current|needsReview` state. Folder membership does not expand and no
  workspace node is duplicated. The owner can read an endpoint at its pinned content revision with
  `workspace-read`; the optional `open` document session remains a document-only convenience.
- **Same two-product release contract.** One shared runtime and one version for the CLI, MCP server,
  shared host and installer (1.6.0); candidate-bound manifests, per-file and embedded-payload
  SHA-256 values, dependency/license inventories, native tokenizer notices and truthful unsigned
  status.

## Prerequisites and installation

- **Windows x64** on the framework-supported boundary in the
  [release contract](release-contract.md#product-and-support-matrix-for-160-s16-t6). The CLI setup
  and MCP package are self-contained; they need no installed .NET runtime, SDK, repository or
  PowerShell at runtime.
- **Microsoft Visual C++ v14 x64 Redistributable, version 14.43.34808 or later**, installed by the
  owner from Microsoft's
  [latest supported downloads](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170).
  Setup, direct install and MCP startup check it read-only and refuse before touching files or a data
  root when it is missing or too old. Meta Brain does not bundle, download, chain or install it.
  Windows 10+ includes the OS Universal CRT, which does not supply the required `VCRUNTIME140.dll`
  and `VCRUNTIME140_1.dll` imports. This prerequisite is unchanged from 1.5.0.
- **Optional hybrid retrieval:** the existing owner-selected local model directory for the pinned E5
  revision `614241f622f53c4eeff9890bdc4f31cfecc418b3`, ending in `model/`, with
  `onnx/model.onnx` (470,268,510 bytes; SHA-256
  `ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665`) and `onnx/tokenizer.json`
  (17,082,730 bytes; SHA-256
  `0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39`). The product bundles no model
  weights, downloads none, persists no selected path and never falls back silently to literal-only
  output. Relation lifecycle, endpoint inspection and relational ranking over literal direct hits
  do not require the model; `--hybrid true` semantic candidates do.
- Model files are not part of the release and are never fetched by Meta Brain. Missing or invalid
  assets refuse with `workspace_index_model_missing` or `workspace_index_model_invalid`; the
  model directory and decoded passage reuse are process-memory-only and are cleared on vault lock or
  host exit.

Example owner path (replace the placeholders with actual ids, pipe and files):

```powershell
$cli = '<install-root>\cli\MetaBrain.Cli.exe'
& $cli owner relation-create --control-pipe '<control-pipe>' --resource-id '<relation-id>' --zone-id '<zone-id>' --input-file '<relation-draft.json>'
& $cli owner relation-list --control-pipe '<control-pipe>' --workspace-node-id '<file-or-folder-id>'
& $cli owner workspace-read --control-pipe '<control-pipe>' --file-id '<endpoint-file-id>' --revision <pinned-content-revision>
& $cli owner relation-unlink --control-pipe '<control-pipe>' --resource-id '<relation-id>' --expected-revision <live-relation-revision> --reason '<text>'
```

## Compatibility and upgrade

- **Upgrade in place from 1.2.0–1.5.0 is supported.** The 1.6.0 CLI setup drains/stops the shared
  host, replaces the application files, keeps the selected data root, vault, settings and pipe
  endpoints, and changes no owner data. The isolated 1.5.0 → 1.6.0 verification changed only the
  data-root ownership marker and kept every pre-upgrade canonical vault file byte-identical.
  Re-running the setup is the ordinary reinstall path.
- **No relation migration is required.** Workspace relations are additional encrypted canonical
  records under the existing workspace format version 1; an upgraded vault with no relations reports
  an empty relation list, and no automatic schema conversion or backfill runs. Existing managed
  relations from earlier releases remain readable with their prior evidence-side behavior.
- **Pre-1.2 installations are refused in place.** A 1.1.0 WPF installation, an unknown/mixed
  component registration, a data root owned by another installation or a live endpoint speaking
  another protocol revision is refused before the host is stopped or any file changes. Install this
  release beside it with separate install/data roots and distinct endpoints; the old installation and
  its data are untouched.
- **Uninstall keeps the data root.** Removing the CLI (or the final component) removes application
  files only; the data root, vault, backups and recovery material remain. Reinstalling and
  reselecting the same data root opens the same data with the owner's credential.
- **Rollback.** To return to 1.5.0, stop the 1.6 host, take an encrypted `owner backup`, and run the
  published 1.5.0 CLI setup as an upgrade over the same install root; the data root is preserved. A
  1.5 build does not understand typed workspace relations, so take and verify the backup before
  downgrading. The 1.5.0 record remains the authority for 1.5 behavior.

## Security and attribution boundaries

- Workspace content, typed relations, relation history and the passage/vector generation are
  encrypted with the vault data key. Every read, create, review act and search goes through the
  owner control pipe under the unlocked vault; a locked service refuses private reads, cancels
  background work and clears decoded caches.
- Scope is applied before disclosure: folder and exact-revision scope bound relation traversal and
  ranked output, and an out-of-scope endpoint contributes no candidate, reason, path or count. A
  folder relation names the folder identity only; it never authorizes or evidences its children.
- A relation is an attributed statement, not a verified fact. Accepted links are review state, not a
  truth or confidence level; contradictions are retained and labelled, similarity is never promoted
  to `supports`, and no newest-wins rule exists.
- The MCP agent surface is unchanged at 22 tools with no owner-authority tool. Agent relation calls
  remain proposals and cannot accept, share or mutate owner workspace relations; the owner
  workspace search, relation lifecycle and document bridge are not agent tools. Agent claims and
  owner acceptance are separate.
- The optional browser session remains document-only: the browser receives no vault key, filesystem
  handle, private path or owner search API, and the product writes no plaintext HTML, draft or temp
  file.
- The product makes no model or provider download and no cloud embedding call. A separately
  consented provider/model job remains governed by the existing job consent and budget and is not
  part of this release's local verification.

## Signing and licensing

- Both assets are **unsigned**. SHA-256 is an integrity check only; it does not authenticate the
  publisher. Never disable SmartScreen, Defender or a security policy to install.
- Project license: MIT, `Copyright (c) 2026 triet4p`. Each asset carries the project `LICENSE`,
  per-dependency license texts and third-party notices. The dependency inventory is unchanged from
  1.5.0: `Microsoft.NETCore.App.Runtime.win-x64 10.0.14` (MIT), `Microsoft.ML.OnnxRuntime` and
  `Microsoft.ML.OnnxRuntime.Managed` 1.31.1, `Tokenizers.DotNet` 1.4.1 and
  `System.Numerics.Tensors` 10.0.14, with the pinned source-built native tokenizer notices covering
  its 86-package Cargo graph. The Microsoft Visual C++ runtime is not included and is not covered by
  those notices; its owner installation is separate. Nothing here is a legal conclusion.

## Known limits

- Windows x64 only on the framework-supported boundary in the
  [release contract](release-contract.md#product-and-support-matrix-for-160-s16-t6); not every listed
  Windows SKU was exercised.
- Relation behavior and relational retrieval were verified on frozen synthetic workspaces. They do
  not establish correctness or quality on owner corpora, very large relation graphs or every query
  family. The disclosed why-case observation (direct-hits-only 0/7 labelled evidence versus 7/7 in
  the ranked view) is one synthetic case, not an approved threshold or benchmark.
- Relational expansion is bounded to two stored hops and the request limit; it is not general graph
  search. Only accepted/current edges expand, so pending, rejected, retracted and needs-review
  relations do not contribute candidates.
- Relation history is prospective: acts recorded from the current relation implementation are
  attributed; earlier managed-relation states were not backfilled.
- Hybrid search quality is established only on the frozen synthetic evaluation and held-out query
  sets. A semantic candidate remains an unconfirmed neighbour, not evidence. Integrated latency and
  memory figures from the task evidence are host- and fixture-specific observations, not approved
  SLAs.
- The interactive setup-GUI refusal on a machine without the VC++ runtime was verified at
  code-path/registry level, not as a full clean-machine installation.
- The optional `open` document session needs an installed Chromium-based browser and is not a search
  console. Browser-managed memory, extensions and OS swap cannot be erased by the product.
- A workspace act carries at most 1 MiB of original bytes; larger files are refused rather than
  truncated. Source-session JSONL records remain read-only and are never served.
- The exact candidate identity, asset sizes, SHA-256 values, provenance, dependency/license
  inventories and the verification actually performed are in the
  [1.6.0 release record](release-1.6.0.md).

## Distribution status

**Not published.** 1.6.0 exists only as the locally assembled, verified candidate described in the
[1.6.0 release record](release-1.6.0.md). The public names above are the intended download-name
mapping and are not live URLs. String `v1.6.0` is not an existing tag or Release in this repository
until the owner-authorized publication step creates it from the reviewed candidate.
