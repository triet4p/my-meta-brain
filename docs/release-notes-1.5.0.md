# Meta Brain 1.5.0 — candidate notes (not a release)

**Status: unreleased candidate preparation only.** 1.5.0 has not been published or made available for download; the latest public release remains 1.4.0. Any locally generated candidate packages and manifests remain ignored build artifacts and are not installation instructions for the current 1.4.0 downloads.

## Candidate scope

The planned release keeps the two-product CLI + MCP shape. Hybrid workspace recall is an owner CLI feature; the MCP agent tool surface remains separate and does not expose owner workspace search. The frozen 1.1.0 WPF Owner UI is not part of this release. No standalone browser console or UI installer is planned.

The owner CLI's hybrid workspace path combines existing exact/lexical evidence with local semantic candidates from the authenticated encrypted workspace passage index. Results retain the exact file identity, content revision, byte range and digest. Folder, field, currentness and content-date filters apply before results and totals are disclosed. Output distinguishes `literal_match` from `semantic_candidate`; a semantic candidate is an unconfirmed neighbor, not supporting evidence or an answer. There is no approved similarity cutoff and no general natural-language no-answer guarantee. Action/effect associations do not establish causality, and an intention mention or acknowledgement does not establish completion.

The existing lexical command remains a separate explicit path. `--hybrid true` does not silently fall back to literal-only output when its local model or authenticated generation is missing, invalid, stale or unavailable.

## Explicit local model selection

Hybrid indexing and search require the owner to explicitly select a local E5 model directory. The expected directory is the pinned model revision `614241f622f53c4eeff9890bdc4f31cfecc418b3`, ending in `model/`, with these verified runtime inputs:

| Relative file | Bytes | SHA-256 |
|---|---:|---|
| `onnx/model.onnx` | 470,268,510 | `ca456c06b3a9505ddfd9131408916dd79290368331e7d76bb621f1cba6bc8665` |
| `onnx/tokenizer.json` | 17,082,730 | `0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39` |

The model, tokenizer, SentencePiece file and four configuration assets in the approved local evaluation set occupy 492,421,754 bytes in total. That is a measured staged asset size, not an installed-product footprint. The product release does not bundle model weights. Obtaining or placing model assets is an owner-controlled action; Meta Brain does not download them, call a cloud embedding provider, persist the selected model path, or select a model implicitly.

Example commands (replace the placeholders with the owner's actual pipe and local model directory):

```powershell
$cli = '<install-root>\cli\MetaBrain.Cli.exe'
$model = '<owner-selected-pinned-E5-model-directory>'
& $cli owner workspace-index-rebuild --control-pipe '<control-pipe>' --model-directory $model
& $cli owner workspace-index-status --control-pipe '<control-pipe>'
& $cli owner workspace-search --control-pipe '<control-pipe>' --query 'your query' --hybrid true --model-directory $model --format json
```

`workspace-index-rebuild` explicitly creates a current encrypted passage generation. Hybrid search and rebuild fail closed for absent model assets with `workspace_index_model_missing`, and for incomplete, mismatched or unusable assets with `workspace_index_model_invalid`; they do not return a lexical-only substitute. The model directory and decoded passage reuse are process-memory-only for the unlocked epoch and are cleared on vault lock or host exit. Background refresh is limited to successful owner workspace mutations and an already explicitly supplied in-memory model path; it does not enroll or scan external folders.

## Evidence and measured limits

The 35-query held-out hybrid scenario exercised the actual owner CLI and service over synthetic source files. The accepted S15-T3 result returned all 27 labeled expected-evidence cases (13 literal-baseline and 14 paraphrase/near-miss cases); two expected paraphrase cases ranked second and the remaining labeled cases ranked first. Four frozen exact/no-evidence negatives and four folder-scope negatives returned no results. Unrelated natural-language queries did produce explicitly labeled semantic candidates, so this does not establish general prose abstention. See [S15-T3 evidence](../artifacts/sprint-15/task-3.md).

The approved standalone S15-T1 model screen used pre-tokenized Hugging Face IDs, not the product CLI/service. It observed 25/27 expected sources at rank 5, 492,421,754 bytes across seven pinned model/tokenizer/config files, a 559.1953 MiB process-private boundary delta, and 17.8471 ms warm in-process p95 after 10 warmups and 100 searches. Those results met the approved T1 screen on that evaluator only; they are not CLI/service latency or memory limits. See [S15-T1 evidence](../artifacts/sprint-15/task-1.md).

The accepted S15-T5 integrated observation used 112 synthetic files and 35 frozen queries. After 10 warmups and 100 fresh CLI-to-service searches, nearest-rank p50/p95 were 291.65/379.81 ms; the first query after a locked restart took 2,365.89 ms including model reload. Service private bytes were 810,008,576 before and 826,810,368 at the maximum sampled query boundary; working set was 851,689,472 before and 863,821,824 at the maximum sample. These are host- and fixture-specific observations, not an approved integrated SLA or candidate-package proof. See [S15-T5 evidence](../artifacts/sprint-15/task-5.md).

- **S15-T6 A7 selected-payload source CLI/service smoke:** rebuilt the Debug smoke project with no restore (0 warnings/errors), then ran the actual `s15-t3` scenario against the Debug CLI and shared host with the pinned dynamic tokenizer. All 35 frozen queries passed, including 27/27 labeled evidence, four exact/no-evidence and four folder-scope negatives, missing/invalid-model refusal, no silent fallback, and filter/anchor/dedup/tie/lock/cancellation/stale-generation checks. First post-locked-restart query was 6,438.13 ms; after 10 warmups and 100 timed searches, p50/p95 were 519.10/837.26 ms. Private bytes were 842,612,736 before and 844,554,240 maximum; working set was 873,529,344 before and 876,802,048 maximum; lifetime peak working set was 1,254,805,504. This is one synthetic source-runtime observation, not an SLA, package proof or independent review; see [S15-T6 worker evidence](../artifacts/sprint-15/task-6.md).

Hybrid search returns locally computed relevance candidates, not verified facts. It applies no raw cosine threshold. Exact symbols, currentness and source anchors remain separate correctness requirements; the synthetic results do not establish quality on owner data, large workspaces or every query family. The browser remains an optional document-only preview/edit surface and is not required for search.


## Native runtime prerequisite

The selected dynamic native tokenizer imports `VCRUNTIME140.dll` and `VCRUNTIME140_1.dll` and requires an owner-installed Microsoft Visual C++ v14 x64 Redistributable version 14.43.34808 or later. CLI setup, direct install and MCP startup read only the installed x64 version and refuse before payload extraction, application/data-root changes or service changes if it is missing, unreadable, malformed or too old. Meta Brain does not fetch, bundle or install the prerequisite; the owner uses [Microsoft's latest supported downloads](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist?view=msvc-170). Windows 10+ includes the OS Universal CRT, but that does not supply these VCRUNTIME imports.

## Distribution status

The 1.5.0 candidate remains unreleased. Local candidate setup/MCP assets, when assembled, and their candidate/package manifests are written only under ignored `runtime/release-candidates/`; their generated manifests and the task evidence are the source for exact filenames, sizes and hashes. The old selected DLL (`a7ca979443378649ad2e18ab923f684390c2f57ff16f611f8671268e4da5986b`) is not used. The selected dynamic payload remains at `runtime/s15-t6-native-build/target/x86_64-pc-windows-msvc/release/hf_tokenizers.dll` (3,198,464 bytes; SHA-256 `4595800ae02f9c7c96d583347e6cf1e2ddd17c26348ac8169c8314ca28b7480d`), outside versioned product source. The versioned Cargo lock, build provenance, and complete native notice text remain under `src/MetaBrain.Connections/native/`.

This payload imports the Microsoft x64 v14 runtime, which the packages do not include. The runtime prerequisite is a separate owner install from Microsoft; its setup terms and the app's direct-link/manual-install path are not a legal determination. The native source/notices, Microsoft runtime prerequisite, and final candidate/package review remain distinct gates. The notes are unreleased and the 1.4.0 public downloads and website stay unchanged.
