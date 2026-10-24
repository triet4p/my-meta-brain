## [2026-09-28] Null PowerShell string parameters become empty paths

**Symptom:** The marked compile-check fixture cleanup threw `GetFullPath`'s “path is not of a legal form” after the ACL smoke had completed, leaving that temporary fixture partially removed.
**Root cause:** In Windows PowerShell 5.1, passing `$null` to a typed `[string]` function parameter produced an empty string; `$null -ne $PreservedMarker` then incorrectly entered the path-comparison branch and passed `""` to `GetFullPath`.
**Fix / workaround:** Guard optional string paths with `[string]::IsNullOrEmpty($PreservedMarker)` before canonicalization. Cleanup now skips the preserved marker explicitly and removes it only after descendants are gone.
**Watch out for:** A typed string parameter that receives `$null` may be `""` in Windows PowerShell; do not use only `$null -ne $value` before calling path APIs.

## [2026-09-29] sc.exe rejected start= after PowerShell argument marshaling

**Symptom:** The owner reported that an elevated same-owner fixture run allocated the marked root, then `sc.exe create` exited 1639 with `Invalid start=` before service identity checks; the owner reported cleanup completed.
**Root cause:** The runner relied on Windows PowerShell 5.1 native argument marshalling for a `binPath` value containing spaces and nested quotes, without explicitly preserving Windows command-line argument boundaries. The exact failed argv was not captured, so the reported parser error is consistent with, but does not independently reproduce, that boundary loss.
**Fix / workaround:** Build the `CreateProcess` command line with Windows backslash/quote escaping per argument; pass `binPath=`, its full value, `start=`, `demand`, `obj=`, and the virtual service account as separate arguments. After each successful create, set the service SID type with `sc.exe sidtype <name> unrestricted`. The non-elevated check invoked real `sc.exe` create/sidtype/delete commands against an existing service or absent random name and observed exit codes 5/1060/1060 without service changes. A later owner-reported elevated fixture run progressed to the service scenarios and reported success; see the S1-T2 artifact for its redacted observations.
**Watch out for:** Windows PowerShell 5.1 native commands with nested quoted executable paths and trailing service options; `sc.exe` requires a space between each `=` and its value. A non-elevated `create` exit `5` is access denied while opening the Service Control Manager before option parsing, so it proves nothing about `binPath=` or `start=` syntax. Do not describe it as a parser smoke test; use the elevated fixture result for that command path. Neither result establishes production isolation beyond the synthetic fixture.

## [2026-09-29] Codex CODEX_HOME canonicalize denied in AppContainer while reads succeed

**Symptom:** Codex `mcp list` under a verified AppContainer exited 1 with `failed to load configuration / Access is denied (os error 5)` even though the fixture `config.toml` probe read `allowed`.
**Root cause:** Installed Codex 0.46.0 `find_codex_home` unconditionally calls `canonicalize()` on `CODEX_HOME`, which on Windows resolves the DOS drive letter via `GetFinalPathNameByHandleW(VOLUME_NAME_DOS)`; AppContainer tokens are denied `\GLOBAL??` access. A fixture-scoped probe showed `VOLUME_NAME_NT ok`, `VOLUME_NAME_DOS error 5`, plain read ok.
**Fix / workaround:** For S1-T3 only, built a fixture-scoped Codex 0.46.0 from pinned upstream `rust-v0.46.0` commit `b650f912c50ce4ce0a23b877aa8b1174878c30e2` with `find_codex_home` falling back from `canonicalize()` to `std::path::absolute()`; binary SHA256 `F369B632127F0ED97951DC6A6BFF4185F97ABE1F809699CFCA596BDF35F744E1`. Its actual `mcp list` passed under a native-verified AppContainer token. The owner-installed Codex is unchanged; this is not a production release. Do not widen fixture ACLs to chase this; empty `managed_config.toml` and working-directory variants do not help.
**Watch out for:** Any sandboxed `CODEX_HOME` on a drive-letter path with Codex builds that hard-require `canonicalize`; direct file-read probes passing does not rule out path-resolution denial.

## [2026-09-30] Malformed MCP input terminated the stdio bridge

**Symptom:** A whitespace-only or malformed line caused the `agent-mcp` process to exit with an unhandled JSON parsing exception, so later valid requests in the same stdio session received no response.
**Root cause:** The line loop parsed input without separating framing whitespace from malformed JSON or converting parse failures into JSON-RPC errors.
**Fix / workaround:** Ignore whitespace-only lines, answer malformed JSON with `-32700` and non-object JSON with `-32600`, then continue reading the same stream; notifications remain response-free.
**Watch out for:** A line-delimited JSON-RPC process must recover at the message boundary; test malformed input followed by a valid request in one process rather than checking only the error response.

## [2026-09-30] Production AppContainer launch resumed an unchecked token

**Symptom:** The test launcher validated its child token, but the production `WindowsAgentRuntime` path resumed a suspended client without checking the native token identity or integrity.
**Root cause:** Verification existed only in the smoke launcher; production process creation trusted `SECURITY_CAPABILITIES` without inspecting the created process token.
**Fix / workaround:** While the child is suspended and assigned to its kill-on-close job, verify `TokenIsAppContainer`, the expected AppContainer SID, owner `TokenUser`, and Low integrity before calling `ResumeThread`; terminate an unassigned child directly or terminate the assigned job on failure, then wait and drain.
**Watch out for:** Failures between `CreateProcess` and successful job assignment cannot be cleaned up by terminating the job; track assignment and reap the suspended process handle directly.

## [2026-09-30] Isolated Rustup install needs its proxy in CARGO_HOME

**Symptom:** With a fresh task-owned `RUSTUP_HOME` and `CARGO_HOME`, `rustup toolchain install` installed the pinned toolchain but then returned exit 1 with “rustup is not installed at” the isolated Cargo home.
**Root cause:** The custom Cargo home did not contain the `rustup.exe` proxy location Rustup expects when `CARGO_HOME` is redirected.
**Fix / workaround:** Copy the discovered Rustup executable to the task-owned `CARGO_HOME\bin\rustup.exe`, verify the copy hash, and invoke that isolated copy while keeping both homes temporary.
**Watch out for:** A fresh `CARGO_HOME` on Windows must not assume the globally installed Rustup proxy is discoverable after its home is redirected; keep the copy scoped and remove it with the task cache.

## [2026-09-30] Cargo's nested git fixture exceeded the Windows path limit

**Symptom:** A pinned Cargo release build failed before compilation because a git dependency checkout could not create `.gitattributes`; the absolute path under task-root `CARGO_HOME` was 262 characters.

**Root cause:** The dependency repository contains deeply nested fixture and registry paths, and placing Cargo's cache under the already long retained build root pushed otherwise valid source paths beyond the Windows path limit.

**Fix / workaround:** Preserve the marker-verified Cargo and Rustup caches but move them to short sibling roots under `%TEMP%` (`c-<build-id>` and `r-<build-id>`); keep build target artifacts under the marked retained root.

**Watch out for:** Cargo dependencies with nested git fixture data can exceed path limits even when the workspace itself is short; measure the complete cache-relative path before retrying or changing dependencies.

## [2026-10-01] cmd.exe payloads must not pass through C-style argument quoting

**Symptom:** A same-token AppContainer `cmd.exe /d /c type "<path>" >NUL ...` probe using the shared `BuildCommandLine` quoter always failed: the child exited 7 and the captured output was empty, even though the generated config file existed and a managed .NET helper with the same token/environment read it.
**Root cause:** The generic quoter escapes inner quotes as `\"`, which Rust/CRT argv parsing expects but `cmd.exe` does not understand; cmd received a corrupted `type` argument (earlier variants surfaced a filename-syntax error, and the literal `/c` comparison absorbed all output so no stderr diagnostic survived). `type` output redirection and `&&`/`||` chaining therefore never ran against the intended path.
**Fix / workaround:** Build `cmd.exe /d /c` with the quoter and append the `/c` payload verbatim (`BuildCommandLine(shell, ["/d", "/c"]) + " " + command`), so only the executable and flags are quoted while cmd parses its own quoting/redirection. Compare the captured marker with `.Trim()` because captured stdio may carry `\r\n` vs `\n` line endings.
**Watch out for:** Any AppContainer `cmd.exe /c` probe whose payload contains quoted paths or redirection: never route the whole payload through a C-style argv quoter, and never infer an ACL/path-resolution cause from an empty `type`-probe capture without echoing the raw exit/output first.

## [2026-10-04] Real owner redemption stayed blocked at the IPC boundary

**Symptom:** The focused lifecycle smoke initially could not redeem a fresh owner-approved grant. After enabling the already-implemented CLI handler, the real agent-pipe reply was `ERROR forbidden`.
**Root cause:** `owner redeem` was missing from the CLI dispatch table, and then the shared `HasOwnerOnlyFields` predicate classified the legitimate grant bearer (`Token`) as an owner-only field on the agent redemption request.
**Fix / workaround:** Dispatch `owner redeem` to its existing handler and allow the grant token only when `HandleAgentRedeem` invokes the shared field guard; all other owner-only request fields remain rejected. The dedicated real-pipe smoke now redeems the token and exercises subsequent authorization.
**Watch out for:** A shared owner-only-field predicate must distinguish credentials that are valid inputs for one agent operation from owner mutation fields. Test actual dispatch plus the service's response, not just the presence of a command handler or usage text.

## [2026-10-05] Smoke DPAPI P/Invoke must name the real crypt32 entry point

**Symptom:** The new S1-T6 smoke compiled but failed at runtime with `EntryPointNotFoundException: Unable to find an entry point named 'CryptUnprotectSessionData' in DLL 'crypt32.dll'` before any scenario ran.
**Root cause:** The test declared its own method name as the native entry point; `DllImport` defaults `EntryPoint` to the method name, and `crypt32.dll` only exports `CryptUnprotectData`. The sibling `LocalFree` import had the same latent shape.
**Fix / workaround:** Set `EntryPoint = "CryptUnprotectData"` (and `EntryPoint = "LocalFree"` for kernel32) while keeping the descriptive managed method names.
**Watch out for:** Any custom-named DPAPI/P/Invoke wrapper in test or product code; a green build does not prove the entry point exists — only a real run does.

## [2026-10-07] Restarted services invalidate memory sessions even when the vault is unlocked again

**Symptom:** After restarting the walkthrough service and unlocking, every previously redeemed session denied with `resource_unavailable`, and `owner sessions` showed count 0 despite successful pre-restart redeems.
**Root cause:** Sessions are process-local and epoch-bounded by design; restart wipes them and unlock does not restore them. A denial-turn prompt that pointed at a pre-restart session therefore proved fail-closed behavior but not the intended granted leg.
**Fix / workaround:** Redeem fresh request→preview→approve→redeem sessions after the final restart/unlock, verify each grants via CLI before the client turn, and keep revoked versus never-revoked session files in distinctly named outputs.
**Watch out for:** Any multi-turn walkthrough spanning a service restart or lock: never reuse pre-restart session files as granted legs; re-verify with `owner sessions` and a CLI granted read first.
## [2026-10-09] New operation plumbing can pass a build and still fail at the name/state seam

**Symptom:** The new S2-T4 relation lifecycle commands compiled cleanly, but every owner transition returned `ERROR invalid_request` at runtime, and after fixing that, every transition returned `ERROR service_rejected_request` while the service reply was actually correct.
**Root cause:** Two independent mistakes at seams the compiler cannot check. First, `RelationValidator.ValidateTransition` attached the "carries no rationale" rule to the wrong enum member (`Retracted`, the unlink target) instead of the relink target (`Proposed`), so the unlink path always refused the rationale it required. Second, the owner CLI's expected-status switch still matched hyphenated command names (`relation-unlink`) while its dispatch passed the wire operation names (`relation.unlink`); a correct `relation_unlinked` reply therefore fell through to the default arm and was reported as a mismatch with no error field, surfacing as `service_rejected_request`. A third, adjacent miss (`relation.read` absent from the grantable-operation vocabulary) failed as `invalid_grant_request` on owner grant issuance rather than as anything relation-specific.
**Fix / workaround:** Attach per-state validation rules to the state they actually govern and assert the pair in both directions (rationale required for reject/unlink, refused for relink); compare the CLI's expected status against the same `ServiceRequestHandler` operation constants the dispatch uses so there is exactly one spelling per operation; add new grantable operations to `OwnerScopeGrants.AllowedOperations` when the agent surface is introduced. The smoke then failed correctly and quickly on each seam.
**Watch out for:** Any new command/operation pair where the CLI argument name and the wire operation name differ in shape (hyphen vs dot): route both through one constant and compare statuses with that constant. A validation rule written next to the wrong enum member is invisible until runtime, so exercise each transition direction in both the positive and negative case in the real smoke rather than relying on the happy path.

## [2026-10-11] A raw byte flip inside a JSON envelope can be a no-op tamper

**Symptom:** The S2-T2 canonical durability smoke intermittently failed with "A tampered canonical ciphertext was accepted." — roughly 3 of 20 solo runs, always at the one-byte flip `tampered[tampered.Length / 2] ^= 0x20` over the stored `.mbv` envelope. Re-running the same scenario usually passed, and no product defect or transport error could be found, so the failure looked unexplainable.
**Root cause:** The stored envelope is JSON produced by `System.Text.Json`, whose default encoder escapes `+` as `\u002B`. A byte flip that lands inside that six-character escape (flipping the final `B` to `b`) still decodes to the same `+`, so the raw file bytes change while the decoded nonce/ciphertext/tag are byte-identical. The service then read the file, decrypted the unchanged authenticated bytes correctly, and served the item — the "acceptance" was a no-op mutation, not a cryptographic failure. Decoding-and-comparing the base64 fields proved it: in every served case `nonce`/`ciphertext`/`tag` were identical, and service-side instrumentation logged the identical plaintext hash for the original and the tampered file.
**Fix / workaround:** Mutate the *authenticated* bytes, not raw file bytes: parse the envelope, decode every base64 field, flip a bit of the first decoded byte, re-encode, and write the file back. Then self-check that the decoded fields actually differ before asserting the denial, so a future mutation that degenerates into a no-op fails loudly instead of asserting a rejection the boundary never had to make. Keep the byte-flip history in mind: a hash change over a JSON container is not evidence that authenticated content changed.
**Watch out for:** Any cryptographic negative test that mutates a serialized container in place (JSON, base64, protobuf varints, length-prefixed frames): check the decoded authenticated bytes changed before concluding the boundary failed. Also watch for boolean helpers that collapse outcomes — `IsS2T2MemoryDeniedAsync`-style checks that return a bare `false` hide whether the command was denied, served, or failed at the transport layer, which is how a no-op tamper came to be reported as a security acceptance.

## [2026-10-11] A 100,000-resource vault fails closed at unlock because the manifest read shared the per-resource 4 MiB envelope cap

**Symptom:** A real 100,000-record canonical fixture (created in one O(n) pass through `EncryptedVaultStore.Create`) could not be unlocked: `vault.unlock` returned `unlock_denied` with no service stderr, while the identical code path worked at 1,000 and 10,000 records.
**Root cause:** `EncryptedVaultStore.ReadManifest` bounded the encrypted manifest file at the shared `MaximumEnvelopeBytes` (4 MiB), but `ValidateManifest` admits up to 100,000 descriptors — a full-size manifest is ~15 MB plaintext / ~25 MB envelope. The read threw size `InvalidDataException`, which `TryUnlock` swallows into a generic `false`, so the one limit that scales with data silently made the validator's own advertised ceiling unreachable.
**Fix / workaround:** Give the manifest its own cap pair sized above the 100,000-descriptor worst case (`MaximumManifestBytes` 64 MiB plaintext / `MaximumManifestEnvelopeBytes` 96 MiB), mirroring the existing scope-grant (4/6 MiB) and catalog (512/768 KiB) pairs; per-resource caps stay untouched.
**Watch out for:** Any private file whose size scales with the record count (manifest, indexes, policy state): the cap must be derived from the data limit the validator advertises, not shared with the fixed-size file type. Also note the fail-closed symptom is indistinguishable from a wrong passphrase from the outside.

## [2026-10-11] The 10-second pipe request timeout silently forbids any owner operation whose frozen budget exceeds it

**Symptom:** The first successful 100,000-record `owner.index.rebuild` over the real pipe answered `request_timeout` and the connection died, even though the frozen D008 budget explicitly allows that rebuild up to 20 minutes.
**Root cause:** `NamedPipeService` armed one `CancellationTokenSource` per connection at `RequestTimeout` (10 s) covering auth, request read, handler execution and reply write. The index rebuild is owner-only but still an ordinary request, so a compliant long rebuild was cut off mid-materialization.
**Fix / workaround:** After the channel/operation check, re-arm the same linked token with `IndexRebuildTimeout` (25 min, above the frozen 20-minute budget) for the `owner.index.rebuild` operation only; every other request keeps the 10 s guard, and the rebuild still has to finish inside the frozen budget to pass.
**Watch out for:** Any new long-running owner operation (bulk export, migration, reindex): decide its per-operation timeout against its acceptance budget before wiring it into the connection loop, and remember the client (CLI/pipe) has no timeout of its own — the server bound is the only guard.

## [2026-10-11] A held .NET Process object reports frozen memory numbers — sample a long-lived service with a fresh process object

**Symptom:** A measurement probe reported byte-identical service working set and private memory from service READY through a 100,000-record rebuild, a 300-second idle window and a third rebuild (e.g. 31,363,072 B throughout) — a 100k vault with a 25 MB manifest cannot possibly sit at that value.
**Root cause:** The probe captured one `Process` object at start and read `WorkingSet64`/`PrivateMemorySize64` from it repeatedly; those performance counters are cached per `Process` instance, so only the first read was live. An independent fresh `Get-Process` read of the same live service disagreed with the cached value.
**Fix / workaround:** Sample long-lived processes through a fresh process object per read (PowerShell `Get-Process -Id` inside the sampling loop, equivalent to `Refresh()` before each read), and validate a memory harness against an independent reader before trusting any sample.
**Watch out for:** Any benchmark or idle-RAM protocol built on `Process` metrics across time (GC/OS-trim questions, idle budgets, soak tests); a constant value over a known allocation-heavy workload is the signature of a stale snapshot, not a stable service.

## [2026-10-11] A lifecycle request that must wait for a legitimate drain can outlive the generic per-request timeout and fail in front of the caller

**Symptom:** Issuing `owner lock` while a large `owner index-rebuild` was in flight on the real pipe returned `ERROR request_timeout` to the CLI after the drain finished (25.6 s at 10k; 81.7 s at 100k), even though the lock had actually executed — the post-lock `index-status` reported `vault_locked`. Only a rebuild short enough to drain inside the 10-second general request guard returned the expected `VAULT locked; in-flight private operations drained`.
**Root cause:** Every connection arms the general 10-second `RequestTimeout` at start (only `owner.index.rebuild` is re-armed to 25 minutes). `LockAsync` deliberately waits for the counted in-flight operations to drain before clearing the key — the correct lifecycle invariant — but the waiting lock request's own token expires during that wait, so the reply write fails closed with `request_timeout` after the lock state has already transitioned. The deeper design gap: nothing let a requested lock stop the in-flight index work, so the drain equaled the whole remaining rebuild.
**Fix / workaround:** A requested lock now signals the in-flight index-rebuild batch reads to cancel at their batch boundary (a per-locked-epoch `CancellationTokenSource` cancelled by `LockAsync`, linked inside `VaultLifecycle.ReadBatch` — the only batch path, used exclusively by the index rebuild); the bounded active I/O drains and zeroizes, the counted operation disposes, and only then is the key cleared (key-lifetime and drain invariants unchanged). The cancelled rebuild fails closed with a distinct `index_rebuild_cancelled` status and publishes no generation, so it is never confused with the request's own timeout (`request_timeout` unchanged); single reads, writes, and all non-index operations keep drain-only semantics. Measured after the fix on the real pipe: the same lock completes in 315 ms at 100,000 records with the rebuild cancelled. No timeout bump/retry/suppression was used.
**Watch out for:** Any owner operation whose semantics include waiting on other counted operations (lock during rebuild, revoke during a read, stop during migration): decide both the operation's own timeout and whether a stop request should cancel the in-flight work at a safe boundary — waiting out a multi-minute job behind a 10-second guard produces a user-visible failed command even when the state transition succeeded, and only a same-request pair (denied while locked, served after unlock) proves which state caused a denial.

## [2026-10-11] A .NET apphost .exe hash is not a build fingerprint — hash the assembly DLLs

**Symptom:** The shipping `MetaBrain.Connections.exe` SHA-256 matched a much earlier pre-fix recording even though the assembly now contained new code, which looked like a stale-binary or mis-recording anomaly.
**Root cause:** The output `.exe` is the generic native apphost launcher; its bytes depend on the build configuration/app name, not the compiled assembly, so unrelated builds produce the identical apphost hash. The actual code (including the concurrency fix) lives in `MetaBrain.Connections.dll` beside it.
**Fix / workaround:** Record fingerprints on the assembly DLLs (Core/Application/Connections/Smoke) plus timestamps, and treat apphost hashes as non-identifying. This attempt recorded the final DLL set (after removing the throwaway builder and rebuilding) as the exact binary identity of every subsequent proof, so no proof ran on a substituted binary.
**Watch out for:** Any "same binary as before?" comparison, checkpoint manifest, or release audit that hashes only apphost executables — it can neither detect nor prove a code change; always hash the managed assemblies actually loaded.

## [2026-10-13] The Bash tool may target an unavailable WSL shell on a Windows workstation

**Symptom:** Invoking the existing scratch build script through `functions.bash` failed with `execvpe(/bin/bash) failed: No such file or directory` on a Windows workstation, even though the Windows .NET runtime and Roslyn compiler were installed.
**Root cause:** This tool's Bash route attempted to start `/bin/bash` in its WSL environment; no Bash binary was available there. This was an execution-environment mismatch, not a repository permission failure or missing Windows compiler.
**Fix / workaround:** Run the Windows `csc.exe`/`dotnet.exe` executables directly with foreground `subprocess.run` through `functions.eval`, await each phase, and keep all outputs in the existing external scratch directory. Record explicitly that this is a manual compiler build, not an SDK `dotnet build`.
**Watch out for:** A Windows-hosted task that calls `functions.bash` with Windows-drive paths may fail before the command starts if WSL Bash is absent; do not infer a permission block or claim the script ran. Use an actually available host process route and report the exact environment limit.

## [2026-10-14] An unhandled validation throw on a pipe handler wedges that channel's listener

**Symptom:** `owner request-approve` with a past expiry printed `ERROR transport_unavailable` (exit 3, no handoff minted) and every later owner-pipe command on the same live service kept failing while the agent pipe on the same process kept serving.
**Root cause:** Two coupled gaps. `HandleAccessRequestApprove` caught `OwnerAccessRequestException` but not the plain `ArgumentException` that `ValidateExpiry` throws for a past/non-UTC expiry, so the connection handler faulted without writing a JSON reply (client saw a dropped pipe). Then `ListenAsync` reaped completed connections with a bare `await`, which rethrew the faulted handler's exception out of the accept loop and disposed that channel's listener permanently.
**Fix / workaround:** Map the known validation failure to a structured deny at the established boundary (`catch (ArgumentException)` → `request_invalid` in `HandleAccessRequestApprove`, before any grant is stored), and make the listener reap observe instead of rethrow (`try/await/catch`-observe faulted connection tasks, then keep accepting). No blanket `Exception` catch around the handler and no listener/retry redesign.
**Watch out for:** Any pipe handler whose validator throws outside the handler's caught set, and any accept loop that awaits reaped connection tasks without observing faults — one bad request then kills the whole channel. Reproduce with a past-expiry approve followed by an `owner requests` on the same live service.

## [2026-10-15] Server-stamp required metadata before agent draft validation

**Symptom:** Agent relation creation passed session authorization but returned generic `resource_unavailable` without publishing a link.
**Root cause:** The agent path cleared the caller's `RecordedAtUtc` before invoking the shared draft validator, which requires a non-null recorded timestamp; the server-generated timestamp was assigned only after validation.
**Fix / workaround:** Generate a server-owned UTC timestamp before validation, put it into the normalized agent draft, and reuse it for the stored relation and request-evidence event. Caller timestamps remain ignored.
**Watch out for:** If a shared validator requires a server-owned field, normalize that field before validation rather than clearing caller input and backfilling after the validator.

## [2026-10-16] Generalizing a reply reader silently folded service refusals into "unavailable"

**Symptom:** A new owner operation answered `search_index_stale`/`collection_unknown`/`invalid_request`, but the client reported `Unavailable` with that code instead of `Refused`; the same regression would have turned the accepted vault-key contract (`unlock_denied`, `credential_required`, `vault_already_initialized` must present as refusals, never as an unreachable service) into "service unavailable".
**Root cause:** `OwnerChannelClient` classified owner answers in two steps: the reply reader tracked only transport availability, and a separate `MapOutcome` decided `Refused` vs `Unavailable` from the service error code. Generalizing the reader to serve several owner commands kept the first step and dropped the second, so every service-side error code fell through to `Unavailable`.
**Fix / workaround:** Restore the single classification point inside the generalized reader: `forbidden`/`unauthorized` → owner-authority denied, `unsupported_protocol` → protocol mismatch, the codes this client fabricates (`malformed_reply`, `unexpected_reply`, `transport_*`) → unavailable, and every other code that came back from a running service → refused. Keep one reader for all owner commands instead of adding a second reply path.
**Watch out for:** Any refactor that merges a two-step transport/policy classification — a refusal from a live endpoint must always stay distinguishable from no answer at all. Regression evidence: the `s5-t2` owner vault-key scenario (`Refused` for `unlock_denied`/`credential_required`/`vault_already_initialized`).

## [2026-10-16] WPF UI Automation: virtualized list rows and popup combo items are not the same as data items

**Symptom:** A UI Automation proof counted 8 realized `ListItem`s for a `ListBox` whose page held 10 rows, and selecting a `ComboBox` item found through `ItemContainerPattern.FindItemByProperty` failed with `Exception calling "Select": "Unrecognized error."`.
**Root cause:** WPF virtualizes `ListBox` rows, so UI Automation only realizes the containers on screen (the rest exist in no automation tree), and a closed WPF `ComboBox` exposes its data items through `ItemContainerPattern` without a realized, selectable container until the popup is open.
**Fix / workaround:** Assert the count the *service* reported (the console also renders the page total as live text) and use the realized-row count only for pages that fit on screen; drive combo boxes with the keyboard (focus the combo, walk its ordered items with the arrow keys, then read the selection back through `SelectionPattern` to prove it moved) instead of relying on popup containers.
**Watch out for:** UI-automation assertions that assume one automation element per data row or that popup items are always selectable; assert state through live text/properties and prefer real keyboard input, which is also what the acceptance requires.

## [2026-10-16] Retry safety compared writer-stamped bytes, so an identical retry looked like different content

**Symptom:** The S5-T4 scenario assertion "a repeated review writes nothing twice" failed: the service refused a retry that carried exactly the reviewed content, while the same path resumed correctly for a rejection.
**Root cause:** The crash/retry resume check compared the exact stored envelope bytes with the envelope the current attempt would write. The record's own recorded instant is part of that envelope but is stamped per attempt, so two semantically identical reviews produced different bytes and were read as "different content".
**Fix / workaround:** Compare the content field by field (identity, zone, schema, kind, author, executor, creation method, evidence, level, title, body, occurred instant, applicability, citations) and let the first attempt's recorded instant stand as the truthful one; the act's own stamp is excluded from the comparison.
**Watch out for:** Any "is this the same write" check over serialized records — a field the writer itself stamps (recorded time, request id, attempt counter) makes two identical attempts differ. Either exclude writer-stamped fields from the identity of the content or state explicitly which stamp wins.

## [2026-10-16] A UI-automation wait matched the view's static guidance text instead of the service's answer

**Symptom:** An S5-T4 proof run failed at "the capture outcome states that the note needs no provider" even though the console did show that outcome; an earlier run of the same harness passed.
**Root cause:** The wait needle was the short phrase `registered source`, which also appears in the view's static guidance sentence ("stored as an encrypted registered source before any structuring step runs"), so the wait could return as soon as the view was rendered and the following assertion read the screen before the answer arrived.
**Fix / workaround:** Wait on a phrase only the answer can produce (`The service saved the text as registered source`), then assert the rest of the outcome text from the same snapshot.
**Watch out for:** Any UI-proof wait whose needle may already exist before the act's answer: static guidance, labels and the console's default "nothing sent yet" messages all live in the same automation text as the answer.

## [2026-10-16] A recorded decision was invisible because the reader resolved "the item" by the wrong rule

**Symptom:** After the S5-T4 console rejected a revise proposal, reopening that proposal showed "no publication record yet" and the undo control stayed disabled, although the rejection had been recorded successfully.
**Root cause:** The console resolved the review's canonical item from the proposal kind (pinned subject item for a revise proposal) and read *that* item's lifecycle record, while the act records the decision on the review item derived from the proposal. Publishing a revise proposal does target the pinned item, but rejecting one records the review item, so the single "resolve the item" rule was wrong for one of the two decisions.
**Fix / workaround:** Resolve the review item as the item the decision is recorded on (derived from the proposal), read its record for the decision, and read the pinned item's record separately as context for what the publish would change.
**Watch out for:** A derived identity that depends on the *act* rather than on the subject: when one entity is "the target of this decision" for a different act than for another, keep the resolution rule next to the act and never assume a single heuristic covers both.

## [2026-10-16] A write path that reuses the shared draft schema still has to fill the fields only the other path's server code stamps

**Symptom:** The first S5-T5 scenario run failed at the owner's first link: the service refused the create as `invalid_request` although the console had two live-revision endpoints, a kind, a state and a reason.
**Root cause:** The owner-plane client built the draft from the shared `RelationCodec` schema and left `recordedAtUtc` null. The shared validator requires a UTC recorded instant, and only the agent path stamps one server-side (it assigns the time before validating, precisely because the agent body may not carry it). The owner path had no equivalent stamp, so the request was valid-looking but incomplete.
**Fix / workaround:** The owner client records the instant of the act in the draft it encodes, and the comment next to the field states that only the agent path stamps one on the server.
**Watch out for:** Any client that reuses a server's shared draft schema: the two paths may be validated by the same rule while only one of them is filled in by the server. Read the validator for the fields it requires *and* check which caller is supposed to supply each one before assuming a second writer is complete.

## [2026-10-16] A whole-window automation snapshot made a detail assertion read another record's row

**Symptom:** An S5-T5 proof assertion "the opened association carries no review flag of its own" failed although the opened link was current, because the token `endpointRevisionChanged` was still present in the window.
**Root cause:** The text snapshot the harness uses collects every text element of the window, so a relation list row that carries the service's review reason ("[NEEDS REVIEW:endpointRevisionChanged]") was indistinguishable from the detail panel the assertion meant to read.
**Fix / workaround:** Assert on phrasing only the detail panel produces (`NEEDS REVIEW: the service flagged`, `Review state: current` plus the opened relation's own `id=<id>  revision=<n>` line) instead of on the reason token, and wait on the panel-specific sentence rather than on a phrase a list row can already satisfy.
**Watch out for:** Window-wide automation text in any proof that also renders a list: rows, static guidance and panel answers share one snapshot. Pick needles that are structurally unique to the surface being asserted, and re-read the panel after the act instead of trusting a snapshot taken before it.

## [2026-10-16] The proof harness's own scaffolding tripped the plaintext leak check it runs

**Symptom:** The S5-T5 proof passed every relation assertion and then failed on its final hygiene check: a synthetic private body marker was found in plaintext under the fixture root.
**Root cause:** The harness itself wrote a memory-item revise draft (containing the synthetic body marker) for the CLI act and never deleted it; the check scans the whole fixture root, which includes the harness's own files.
**Fix / workaround:** Delete every draft immediately after the act that consumes it (the harness now removes its revise draft, and the fixture scenario deletes the source and relation drafts it writes), and treat the marker check as covering the harness scaffolding as well as the product.
**Watch out for:** A leak check whose own inputs are plaintext by construction: harness drafts, prompts and scratch files live in the same tree the product is asked not to leak into.


## [2026-10-18] Stopping a fixture service for the build can strand its encrypted outputs

**Symptom:** A build targeting the shared output directory failed with `MSB3027` while the fixture service held a copied application assembly. Stopping the task-owned WPF/service process released the DLL and the build passed, but the encrypted fixture could no longer be unlocked because its generated passphrase and one-time recovery key had existed only in the setup PowerShell process.
**Root cause:** The build overwrote an assembly loaded by the live service, while the owner-vault contract deliberately returns recovery material once and persists it nowhere; terminating the process that held the fixture credential removed the only available recovery path.
**Fix / workaround:** The observed build-lock workaround was to stop only the task-owned UI/service and rebuild, but that did not restore fixture access. For future fixture work, complete builds before launching the runtime or direct builds to an isolated output directory; keep authorized recovery material available in a protected, non-logging owner session before any required restart. Never replace the fixture with a blank ledger to recover test progress.
**Watch out for:** A runtime fixture whose encrypted output or spend ledger must be reviewed after a build/restart. Independent fixtures have independent encrypted ledgers, so a new fixture cannot stand in for the locked one or carry forward its budget by default.

## [2026-10-20] A purge Finalizing fast-path wedged crash recovery and an empty-Remaining fallback replayed deletes

**Symptom:** S7-T5 evidence review R2 showed two coupled purge defects: resume of a `Finalizing` intent returned `DecisionApplied:false` and skipped scope-strip/catalog/Applied forever (permanent wedge), and resume with empty `Remaining` fell back to the full `Impacted` set instead of finalizing. The mid-loop tombstone probe additionally showed `lifecycle-create`/`feedback-create`/`relation-create` had no purge-ledger guard, so a purged anchor could be re-minted while `Applying`.
**Root cause:** The `Finalizing` claim was treated as terminal (report-only) rather than as a resumable claim on idempotent Phase 3 work; the deletion seed treated empty `Remaining` as "no checkpoint" instead of "deletes done"; three create paths were added without the `RequireNotPurged` call the other guarded paths carry.
**Fix / workaround:** Re-enter the same idempotent Phase 3 from `Finalizing` (set-subtraction scope strip, `catalog_unknown`-tolerant withdrawal), bump the policy generation only when live scope state still names a purged ID, seed deletes from `Remaining` alone, and add `RequireNotPurged` to the three unguarded create handlers with the existing `invalid_request` fail-closed mapping. Prove with real mid-loop (1 deleted / 2 remaining) and inside-`Finalizing` crashes plus generation-equality across the second resume.
**Watch out for:** Any multi-phase state machine where a "claim" phase returns early on resume instead of completing its idempotent work; any `remaining.IsEmpty ? fullSet : remaining` fallback (empty must mean done, not missing); any new create/import path added without the ledger tombstone check — audit every writer against `IsPurged` when the ledger promises forever-reservation.

## [2026-10-20] An unset-by-default env crash knob in shipping code is still shipping scaffolding

**Symptom:** S7-T5 evidence review R3 passed the purge crash proof but the shipping `VaultLifecycle` still carried a `METABRAIN_S7T5_PURGE_CRASH_AFTER_INTENT_SAVES`/`COUNT_PATH` environmental FailFast plus arbitrary count-file writes. `Unset by default` was read as synthetic isolation; the correction required full removal as throwaway runtime scaffolding after proof.

**Root cause:** A test-only crash control lived behind a process-environment check inside the product binary: one env lookup when unset, process kill plus a file write when armed. No pipe command can set process environment, so it was not remotely triggerable — but it was still a permanent production debug path with destructive effects (FailFast + arbitrary path writes) that code review must carry forever, and `unset` is a runtime state, not a compile-time separation.

**Fix / workaround:** Deleted the failpoint and both call sites from shipping source (plain persisted commits, zero env/file/FailFast surface; repo-wide grep clean), and re-homed the deterministic crash between two real durable commits in a test-only `IVaultOperation` decorator (`S7T5CrashingPurgeOperation` in `S7T5PurgeSmoke.cs`) that counts only persisted commits and drives the same shipped `PurgeService.Confirm`/`Resume` entry points on the fixture vault. Same checkpoints re-proven: Confirmed save-1, mid-loop save-2 (1 deleted / 2 remaining), Finalizing save-4, generation-equality across second resume.

**Watch out for:** Any `X_TEST_ONLY` env/flag/file knob inside `src/` justified by `unset by default` — removal after proof is the cleanup, not the unset state. Prefer the existing fixture infrastructure (here: the `IVaultOperation` decorator pattern already used by `CorruptedEnrollmentOperation` in `S6T1ModelJobsSmoke.cs`, plus `Stop/StartServiceAsync` restart/unlock around direct-vault steps) over product-reachable fault injection. When repairing test files with surgical edits, re-verify anonymous-object fields line by line (a dropped `reason` field here failed closed as `invalid_request` and was caught only by the exact-error probe).

## [2026-10-20] A surface that leaves the shared authority loses the owner's own policy

**Symptom:** The Sprint 7 differential deep review found that a contextual intention reminder (`CONTEXT_INTENTION`) was still served in the scoped brief after the owner set that intention aside, even though the same request's generic brief path honoured the set-aside; the reminder also left no omission trace, so the owner saw an item they had explicitly removed.
**Root cause:** `ServiceRequestHandler.HandleAgentMemoryBrief` computed the contextual candidates first and removed their IDs from the scope handed to `ScopedBriefAuthority.Build`, the only code that applied the shared recall policy and wrote the `owner_archived` omission trace. `FindContextualIntentions` re-implemented the candidate selection on its own (grant, live revision, kind, pending, unacknowledged, enabled trigger, exact context match) and had no recall-disposition check at all, so a second projection path silently bypassed the one policy authority. Each task's own evidence was internally sound: the T3 smoke never set a recall disposition and the T4 smoke never enabled a context trigger, so the intersection was untested by both.
**Fix / workaround:** The shared recall policy index is now read once per brief and handed to both projections: the contextual candidate carries the owner's disposition, a set-aside intention is left out of the contextual projection and named in the omission trace (`owner_archived`), it returns only under the explicit `includeArchived` expansion, and a restore returns the default reminder. The context trigger, the frozen grant, the exact approved ID/revision, the pending and unacknowledged state and the explicit context match stay exactly as they were, an out-of-scope intention is never named, and the contextual set keeps its deterministic ID order (the tier ordering belongs to the generic brief, search and library). The regression is behavioral in the existing S7-T3 smoke: archive → default MCP brief omits the reminder and names it → explicit expansion returns it → restore returns the default, plus the out-of-scope boundary and an unchanged lifecycle record.
**Watch out for:** Any second projection path that filters the shared scope before the authority runs (a candidate list computed outside the authority and subtracted from its scope). The scope subtraction is what removes the item from the policy's reach, so the policy must be consulted in the path that owns the projection, not only in the path that owns the scope. When two tasks own one behaviour, check their intersection: a policy task and a projection task each proved their own half and neither exercised the seam.

## [2026-10-21] The owner console discards its own vault-format outcome while the vault is locked

**Symptom:** After an upgrade or rollback on the **Vault format** card, the format line changed to `current`/`upgrade_available` but the card's outcome message still read the previous answer (for example the earlier `vault_denied` refusal), so the owner never saw what the service actually did.
**Root cause:** `OwnerConsoleSession.ProbeAsync` moved the private epoch (`ClearPrivateState`) on every changed status answer that was not the unlocked vault. A maintenance act runs only while the vault is locked, and changing the keyless format record or the rollback availability changes the status record without changing availability or `VaultState`. The maintenance methods guard their reply with `epoch != _privateEpoch`, so the spurious epoch move made them drop their own fresh answer as stale.
**Fix / workaround:** Move the private epoch only when the serving state itself moves (availability or vault state); keep clearing on a lost service, a lock or any other vault-state change. Permanently exercised by `s8-t3-ui`, which asserts the service's own resume/rollback/discard text on the real WPF card (`runtime/s8-t3/evidence/s8-t3-ui-run.txt`).
**Watch out for:** Any status field added that changes without a serving-state change (format marker, rollback availability, counts) combined with an act whose answer compares a captured private epoch. The epoch is the lock boundary, not a generic "the status record changed" token.

## [2026-10-21] Some product refusals answer with a sentence, so an "error code" field can carry free text

**Symptom:** The new owner audit trail recorded `code=unspecified` for a failed model job instead of the real failure code `provider_unconfigured`, and the owner diagnostics showed the generic "the service refused this request with the code unspecified" sentence. The same product path also refused a job creation with `pricing_unknown: No approved pricing card found for '...'` — a code and a sentence in one string.

**Root cause:** `ServiceRequestHandler` is not uniform about its `Error` field: most paths return a stable code (`Error("vault_locked")`), but several catch blocks answer `new ServiceReply(protocol, "denied", "owner", ex.Message)` and the job-start path answers with `job.Failure?.Message`. So the wire field that the audit reads as "the error code" is sometimes a human sentence, and a sentence is unbounded, may name a resource, and is not a code at all.

**Fix / workaround:** The S8-T4 recorder normalizes every error value before it is stored: a short token of lowercase letters, digits, dots, dashes and underscores is kept as the code; anything else becomes `unspecified` (the sentence stays the owner's answer in the reply and never enters the private state or the operational log). The job failure is read from `reply.Job.Failure.Code` first, so a failed job keeps its real code even though its reply carries the failure message.

**Watch out for:** Any new audit, log or diagnostic surface that copies `ServiceReply.Error` into a field named "code". Check the producing path first (`grep "denied\", \"owner\", ex.Message"`), and prefer a structured field (`Job.Failure.Code`) over the free-text `Error` when one exists.

## [2026-10-21] A locked write is refused as `invalid_request`, not `vault_locked`, when the handler validates fields before the lease

**Symptom:** An S8-T4 probe of a private write while the vault was locked expected the refusal code `vault_locked` and got `invalid_request`, which made the locked-refusal evidence wrong.

**Root cause:** The handlers are not uniform about the order of their two refusals. `HandleMemoryCreate`/`HandleSourceCreate` fold `operation is null` into the same condition as the field validation and answer `invalid_request`, while `HandleOwnerJobCreate`, `HandleOwnerPurgePreview` and the new operations handlers check the lease first and answer `vault_locked`. Both are refusals; only the code differs.

**Watch out for:** When a smoke asserts a specific refusal code, pick an operation whose handler answers that code, or assert "the request was refused" instead of a code that depends on the handler's check order. The S8-T4 proof uses `owner.job.create` for the locked refusal because that path names `vault_locked`.

## [2026-10-21] A deleting request must be validated completely before the first deletion, not before its own step

**Symptom:** The owner retention act cleared the safe operational log first and validated the retention window and the record ceiling afterwards, so a request with `purgeOperationalLog=true` and an out-of-range window deleted log lines and was still answered with `invalid_request`.

**Root cause:** The validation sat inside the step that needed it (the retention pass), while the *other* removal of the same request (the log purge) ran before it. A request that removes two things needs one validation that covers both, and it has to run before the first removal.

**Fix / workaround:** `OperationsAuditAuthority.ValidateRetentionChoice` validates the whole choice and is called before anything is removed; the private state is written before the log is cleared, so a failed encrypted write also leaves the log intact. The proof sends an invalid choice with the log purge requested and asserts that every log line and every audit entry of the moment is still there afterwards.

**Watch out for:** Any act that removes more than one thing (a log, a file, a row set, a ledger entry). Validate the union of the inputs once, at the top of the act, not inside the step that happens to need the value.

## [2026-10-21] A catch list that mirrors "the exceptions I remember" is not a failure contract

**Symptom:** The recorder of the private audit trail caught `IOException`, `InvalidDataException`, `UnauthorizedAccessException` and `ObjectDisposedException`, but the encrypted write calls `EncryptPayload` without translating cryptographic exceptions, so a crypto failure could escape the recorder after the owner's mutation had already succeeded and replace its answer with a failure.

**Root cause:** The catch list was written from the exceptions the author could remember, while the real failure domain of the encrypted write is larger than that list. A list that is not derived from the producer can never be complete.

**Fix / workaround:** The producer names its failure: the store translates every read and write failure of the private operations state into one typed `OperationsAuditPersistenceException`, and the recorder catches exactly that type plus the locked-vault case. Everything else keeps travelling, because it is a defect. The failure is reported truthfully in the safe operational log (`audit_persistence_failed`), so the owner sees an incomplete trail instead of a silent gap.

**Watch out for:** Whenever a catch block protects "the answer of an act that already happened", the exception type must come from the layer that raises it, not from a list maintained by the caller.

## [2026-10-21] A code that is written into a sentence cannot be recovered later

**Symptom:** A refusal that answered `new ServiceReply(protocol, "denied", "owner", ex.Message)` put a human sentence into the field the audit reads as the error code, so the trail recorded `unspecified` and the owner lost the real failure (`pricing_unknown`, `source_unavailable`, `budget_exceeded`, …).

**Root cause:** The code and the explanation were carried in one string. Everything downstream then had to guess: the recorder normalized the sentence away, and any attempt to recover the code from the text would be a message-parsing heuristic that breaks on the first reworded sentence.

**Fix / workaround:** The code travels in its own typed field (`ServiceReply.ErrorCode`) and the product services raise the refusal through one typed exception (`ServiceRefusalException`). The sentence stays the owner's answer in the reply and in the CLI output (`ERROR <code>: <sentence>`); the trail, the diagnostics and the log record the code. Every caller of the job and schedule services was migrated explicitly, because the typed refusal does not derive from `ArgumentException`.

**Watch out for:** A field that is read as a code must only ever hold a code. If a path wants to answer with a sentence, it needs a second field, not a shared one.

## [2026-10-21] A typed field is a contract only when every consumer reads it

**Symptom:** The service answered a refused job create with the real code in `errorCode` and the
owner's sentence in `error`, and the audit trail and the CLI read both correctly. The owner channel
client did not: it read the `error` text as the code, so the console showed
`code: No approved pricing card found for 'synthetic-provider:synthetic-model'.` and the real code
`pricing_unknown` was nowhere. A refused job cancel was worse: the typed refusal was never caught
by its handler, so the connection died without an answer and the client reported
`transport_unavailable` — a transport failure for a refusal that had a real code.

**Root cause:** The typed field was introduced on the producing side and the recording side, but
the reading side kept the old convention that `error` holds the code. A field that only some
consumers read as a code is not a contract. The second defect had the same shape: a service that
raised a typed refusal was called from a handler whose catch list did not know that type, so the
refusal escaped as an exception instead of an answer.

**Fix / workaround:** One shared classifier in the client reads the typed code and the sentence
from their own fields, classifies the answer once for every owner command, and keeps the sentence
for the console; the handler that calls the job lifecycle catches the typed refusal like its
siblings do. The proof drives the real client against the real service: the refused job create is
read as refused with the code `pricing_unknown` and the service's own sentence, and the refused job
cancel is read as refused with the code `invalid_request` — never as an unavailable transport.

**Watch out for:** After a wire field changes meaning, list every reader of the old meaning, not
every writer of the new one. `grep` the readers of the field (`TryGetProperty("error"`,
`reply.Error`, the `ErrorCode` of a result record) and migrate them explicitly. And when a service
starts raising a typed exception, check the catch list of every handler that calls it: a typed
refusal that escapes its handler is an unavailable transport in the eyes of every consumer.

## [2026-10-21] A per-item lookup of a list the caller already holds is a quadratic bug in waiting

**Symptom:** The private index rebuild of a 1,120-record vault took 8.140 s (137.6 records/s) and
the owner library search answered with a p95 of 576.7 ms at the same size, against a frozen
target of 250 ms at 1,000 records. Both paths got slower as the vault grew, even though the work
per item did not change.

**Root cause:** Three loops each re-derived something the caller already had. The rebuild asked
the vault for its whole resource list once per memory item (and then attempted a lifecycle read
for a record that mostly does not exist); the search asked the vault for its whole resource list
once per served hit, and read and decrypted every relation again for every candidate. The
manifest lookup itself was a linear scan of the descriptor array on every read, so the read path
paid O(n) per read.

**Fix / workaround:** The rebuild takes one descriptor snapshot for the pass and joins the
lifecycle state from it; the search resolves the live revision set and the superseding relations
once per query; the manifest keeps a descriptor index for its own lifetime, and a write publishes
a new manifest instance. The rules the loops fed are unchanged — the same candidates, the same
staleness flag, the same currentness answer — only the number of walks changed.

**Watch out for:** When a loop body asks a store for a list, check whether the caller already
holds that list. A snapshot that is taken once per operation and passed down is usually the whole
fix, and it is the fix that keeps the security properties: the scope filter still runs inside
the engine, the body is still read at its exact frozen revision, and the index is still a
disposable in-memory projection.

## [2026-10-21] Two `UtcNow` calls in one row are two different instants

**Symptom:** A measurement harness that wrote synthetic memory items through the real owner pipe
had every write refused with `invalid_request`, while the same product path succeeded with a
hand-written payload. The product was right: the message was "Occurred time cannot follow
recorded time".

**Root cause:** The harness stamped `recordedAtUtc` and `occurredAtUtc` with two separate
`DateTimeOffset.UtcNow` calls. The second call is later than the first, so the record claimed to
have happened after it was written, and the domain validator refused it — correctly, because a
record that claims to have occurred after it was recorded is not a record the product can accept.
The in-process probe that printed the product's own exception message is what found it; the
service only answered with a code.

**Fix / workaround:** One instant is captured and both stamps are derived from it (the occurred
instant is the event, the recorded instant is the write). A harness that drives the product must
print the product's own message when it can, not only the code the product returns to a caller.

**Watch out for:** Two "now" calls in one record are two facts that will disagree. And when a
product answers a caller with a code, a harness that cannot see the message behind the code will
blame the product for its own bug.

## [2026-10-22] High-selectivity recall still paid for per-candidate ranking objects

**Symptom:** With the synthetic corpus and 1,100 timed queries after 100 warm-ups unchanged, 10k
recall p95 was 647.7 ms against the frozen 500 ms target at 4,940 average matching candidates.

**Root cause:** Even after candidate IDs were filtered inside the index and read in batches, each
candidate still incurred a `HashSet` allocation during scoring, plus separate temporal/posting
dictionaries, token lists and a mapped document in the batched materializer. Removing only the
score-time set improved p95 to 556.2 ms but did not meet the target.

**Fix / workaround:** Preserve term order and membership semantics with allocation-free
`Array.IndexOf` scoring, then stream temporal metadata and matching postings from one ordered
candidate-table join. The candidate ordinal and token order preserve the previous result order and
token set. The same 10k measurement then observed 453.2 ms p95, below the unchanged target.

**Watch out for:** When candidate counts grow, include every per-candidate collection and temporary
map in the cost model, not just vault reads. Compare identical corpus/query workloads at each
iteration; do not lower candidate coverage or change the frozen target to hide a miss.

## [2026-10-22] A scope table that is written but never joined leaves the filter to the caller

**Symptom:** The private index bound the caller's approved scope IDs into the engine's own
`private_index_scope` table, but the candidate query never referenced that table: it returned every
resource that carried the query's terms and the caller filtered the out-of-scope ones. The
evidence and the decision log both claimed that the scope filter ran inside SQL before any token
row left the engine.

**Root cause:** The scope table was introduced to get past the engine's bound-parameter limit, and
the implementation stopped there. The managed post-filter was added as the obvious way to use the
rows that had just been bound, and the SQL never joined the table it had paid to fill.

**Fix / workaround:** The candidate query joins the scope table (`INNER JOIN private_index_scope sc
ON sc.scope_id = t.resource_id`), so the scope is applied before the row leaves the engine, and the
managed post-filter is deleted. The permanent regression asserts the *observable* exclusion (a
document outside the scope is not returned for a scope larger than the parameter limit); it does
not assert the source text, so it protects the behaviour and not the wiring.

**Watch out for:** When a decision log says a filter runs inside the engine, check the query, not
the prose. A table that is only written is a table nobody reads, and it costs a full rebind of the
scope on every change.

## [2026-10-22] An identity counter in the engine falls behind the rows a migration brought in

**Symptom:** Indexing a document into a private index that had just been upgraded from the previous
index schema failed with `SqliteException: SQLite Error 19: 'UNIQUE constraint failed:
private_index_temporal.doc_id'`. The engine handed out document ids from a counter that started
at 1, while the migration had already given the migrated rows the ids 1..N.

**Root cause:** The document id was a managed counter (`_nextDocumentId`) next to a database that
already owned the same fact: the temporal table's `doc_id` is its rowid. The counter was reset on
clear and on reset, but it was never reconciled with the rows the database held, and a migration
writes rows the counter never saw.

**Fix / workaround:** The engine no longer keeps a counter. The temporal row is inserted without an
explicit id and the engine reads back the id SQLite assigned (`last_insert_rowid()`), so the id is
always free above the rows the database holds. The permanent regression takes over a live
schema-2 database, lets the engine migrate it, and then indexes a new document, replaces a
document at the same identity, adds a revision and rebuilds the generation.

**Watch out for:** A migration test that only counts rows after the upgrade does not test the
first write after the upgrade. And an identity that a database can assign is safer than one the
process remembers.

## [2026-10-22] Attribute a latency miss only to a path the timed query actually executed

**Symptom:** The 100,000-record recall p95 (6,706.6 ms) was recorded as the cost of reading the
body of every matching candidate. The timed queries ran with the level filter unset, so the
owner-library path never called the body reader for an unserved candidate: it read only the 10
served hits. The body-heavy path is the level-filter scenario of the result dump, a different
scenario, and one of the four that did not finish at that size.

**Root cause:** The measurement harness ran the timed loop and the result dump with different
query shapes, and the two were read as one profile. The recorded explanation named a plausible
code path instead of the one the timed queries execute.

**Fix / workaround:** The timed query's shape is now part of the record (level unset, current
`all`, as-of unset), and the attribution follows the code path the timed loop actually runs: the
candidate materialization — 48,686 candidates per query on average, 122 batches of 400 candidate
rows, 21.0 GB allocated over the 200 timed queries and 106 gen-2 collections during them. The
preserved profiling separates measured numbers from derived ones, and the split between the
engine's join and the managed allocation is left unclaimed because it was not measured.

**Watch out for:** Before blaming a code path for a latency miss, check that the timed loop
reaches it. A filter that is unset in the benchmark is not part of its cost, and a scenario that
was aborted is not part of the timed profile.

## [2026-10-22] `Data Source=:memory:` with `Cache=Shared` does not share the database

**Symptom:** A second connection opened with `Data Source=:memory;Cache=Shared` did not see the
table the first connection had created (`no such table`), so a test could not hand a pre-seeded
database to the engine.

**Root cause:** In shared-cache mode SQLite shares a cache between connections, but the
`:memory:` name still gives every connection its own private in-memory database. Only a named
in-memory database (`file:name?mode=memory&cache=shared`) is shared between connections.

**Fix / workaround:** The engine can open on a database the caller already holds (an internal
constructor that adopts a `SqliteConnection`), which is also how a live schema-2 engine is upgraded
in place. No shared-cache naming and no file on disk are involved.

**Watch out for:** Do not design a test seam on the assumption that two `:memory:` connections see
each other. Probe it first; the answer is no.

## [2026-10-22] A `finally` that zeroes the buffer also zeroes the value the method returns

**Symptom:** The private index engine authenticated every record correctly (the AEAD tag verified)
and then reported "The private index format version is unsupported" while reading the generation
directory. The decrypted bytes were obviously right when the same record was decrypted by hand.

**Root cause:** The decrypt helper cleared the plaintext buffer in its `finally` block and returned
that same buffer. In C#, the `finally` block runs **before** the return value reaches the caller, so
the caller received a buffer of zeroes. The tag verified, so the failure looked like a *format*
problem while it was a *lifetime* problem.

**Fix / workaround:** The helper now hands the plaintext to a local, clears the local reference
before the `finally` and only zeroes the buffer when the method did not return it — the same shape
the vault's own `DecryptPayload` already uses (`var result = plaintext; plaintext = null; return
result;`).

**Watch out for:** Any helper that returns a buffer it also clears in a `finally`. If a decrypt or
a decode produces "the right bytes" but the consumer sees zeroes, suspect the lifetime, not the
format.

## [2026-10-22] A buffered reader that cannot hold the record fails as "truncated"

**Symptom:** The generation file was rejected as truncated as soon as the engine read the
directory record, even though the file was complete and its length was known from the header.

**Root cause:** The buffered reader filled its 64 KiB buffer with exactly the number of bytes the
caller asked for, so a record larger than the buffer could never be held: the fill loop stopped
after the first buffer-full read, reported "not enough bytes" and the caller raised the truncation
error. The bug only appears on a record bigger than the buffer — the directory record of a
1,000-record generation is about 1.7 MB.

**Fix / workaround:** The reader now serves a record that fits from the buffer and reads a record
that does not fit into a right-sized buffer of its own; the fill loop refills the whole buffer
instead of the requested byte count.

**Watch out for:** Any hand-rolled buffered reader over a file whose records have variable size:
the buffer must be a *cache*, not a *limit*.

## [2026-10-22] A snapshot's digest and the rows it carries can come from two different generations

**Symptom:** The private index rebuild stamped its snapshot with the digest of the live canonical
set, while the row count and the row sample it put in the same snapshot came from whatever
generation the engine had loaded. A generation that was substituted between the publish and the
read could therefore be published under the live digest, and the owner status reply — which only
compared the snapshot's digest with the live one — reported another generation's rows as
`stale: false`.

**Root cause:** The digest and the rows are two independent facts, and only one of them was
checked. The generation's identity is bound to the canonical set it was built from, so the engine
can report the digest of the generation it *actually holds*; the rebuild compared the live digest
against nothing and simply assumed the generation it had just published was the one it was reading.

**Fix / workaround:** The rebuild compares the engine's own generation digest with the digest of
the live canonical set **after** the publish, and discards and retries the pass when they differ
(a set that keeps being substituted publishes nothing). The owner index status applies the same
guard. The invariant is covered by a permanent regression that drives a store reporting a
canonical digest that is not the one of the generation it holds through the real rebuild authority
over a real vault: with the check disabled the scenario fails.

**Watch out for:** Any snapshot, cache or report that carries an identity **and** content. Check
the identity against the source of truth *and* the content against the same source, and make the
two checks the same check. "The digest I computed" and "the generation I am holding" are not the
same fact.

## [2026-10-22] A scoped agent filter can be rejected before its handler sees it

**Symptom:** The real in-process agent search returned `invalid_request` for `SearchLevel=l1`, even though the handler and scoped authority already implemented the level filter.

**Root cause:** A protocol-wide guard classified every `SearchLevel` field as owner-library-only and allowed it only on `OwnerLibrarySearchOperation`; the agent-search handler's own allowlist was not the only gate.

**Fix / workaround:** The early guard now permits `SearchLevel` on the scoped agent-search operation only, while keeping `SearchFromUtc`/`SearchToUtc` owner-only. The real grant/redeem/search smoke covers L1 and L2 results.

**Watch out for:** When adding a consumer filter, trace the dispatch guard, operation-specific extras predicate, handler validation and downstream authority. A downstream implementation is unreachable if any earlier field allowlist rejects it.

## [2026-10-22] Form-D normalization rejects code points accepted by canonical memory validation

**Symptom:** The exhaustive BMP token-bound smoke threw `ArgumentException: String contains invalid Unicode code points` inside `string.Normalize(FormD)`.

**Root cause:** Canonical memory validation does not exclude Unicode noncharacters or isolated surrogate code units, while the runtime normalizer rejects those values. An imported but otherwise accepted record could therefore fail during index rebuild.

**Fix / workaround:** Search normalization replaces Unicode noncharacters and unpaired surrogates with token separators before Form-D normalization, leaving valid scalar values and the tokenizer's existing delimiter behavior intact. The full non-surrogate BMP sweep now verifies the nine-byte source bound.

**Watch out for:** Derive index normalization bounds from what canonical validators actually admit, not from typical user text; run the normalizer over unsupported scalars as well as ordinary letters.

## [2026-10-22] Owner scope preview cannot combine a zone and exact resource IDs

**Symptom:** A real owner scope-preview request carrying both `ZoneId` and `ResourceIds` failed with `invalid_grant_request`.

**Root cause:** `ResolveResourceSelection` intentionally treats a zone selection and an exact-resource selection as mutually exclusive; it rejects the combination before building the frozen grant.

**Fix / workaround:** The synthetic proof now previews the exact frozen resource list without also supplying a zone. A revision-pinned query whose term exists only on a later revision expects no candidate from the older approved revision.

**Watch out for:** When proving exact agent grants, send one unambiguous selection form; do not expect the index to emit an empty-match candidate merely because a newer revision contains the searched term.

## [2026-10-22] Count overlapping rebuild buffers, not only steady-state caps

**Symptom:** The bounded-index review found whole-vault transient memory beyond the resident directory: reconciliation retained two manifest snapshots, the canonical digest concatenated the full ID/revision set before UTF-8 encoding, and commit reopened the generation while posting/directory build buffers were still live.

**Root cause:** Each steady-state structure had a cap, but the simultaneous lifetime of snapshots, serialized directory bytes, spill buffers, and the final authenticated readback was not counted as one peak.

**Fix / workaround:** Stream the exact existing digest encoding through `IncrementalHash` and fixed stack buffers, release the build snapshot before requesting the reconciliation snapshot, and dispose posting/directory-spill buffers plus the directory plaintext before reopening the published generation. Reject directory counts from a source-derived resident upper bound before allocating arrays; the focused build and in-process regression pass.

**Watch out for:** A bounded cache or directory does not bound rebuild peaks. Trace which snapshot, serialization, ciphertext/plaintext buffers and reader caches coexist at every transition, and report sampled process-private/managed peaks separately from resident-structure counters.

## [2026-10-22] Apply strict token-record validation on every read path

**Symptom:** The optimized matched-token path rejected malformed UTF-8, while the full-token reader used replacement decoding and accepted non-minimal 7-bit lengths; both parsed the same authenticated record format.

**Root cause:** Hardening the fast query path did not automatically harden the full reader used by other consumers, so authenticated bytes could be interpreted differently depending on the route.

**Fix / workaround:** Both readers now enforce the count and byte caps, strict UTF-8, nonempty entries, minimal length prefixes, and exact end-of-record consumption. Regression cases cover invalid UTF-8, malformed lengths, trailing data, forced digest collisions, and UTF-8 ordering that differs from UTF-16 ordinal ordering; the focused current-source smoke passes.

**Watch out for:** AEAD proves record authenticity, not format validity. Every decoder for a shared record type must reject the same malformed forms and preserve the established ordering/equality contract.

## [2026-10-23] Exact-token verification can dominate 10k recall even when allocations fall

**Symptom:** A complete 10,000-row current-source matcher run measured 1,898.32 ms p95 against the frozen 500 ms target, despite the focused functional smoke passing.

**Root cause:** The matched-token reader computed a SHA-256 digest for every token in every candidate's authenticated token record before determining whether that token was a query term. Static inspection showed this per-entry digest work was on the hot path.

**Fix / workaround:** Sort query beacons by canonical UTF-8 order, binary-search each validated token's exact byte span, and verify the digest only after byte equality. Keep the forced-collision assertion and exact equality so a digest collision cannot become a match. Re-run the complete frozen query method; the final 10k p95 was 186.48 ms, with 100 warm-ups and 1,100 timed queries.

**Watch out for:** A passing fixture, lower allocation count, or 1k p95 does not predict 10k latency. Hashing every authenticated field in each candidate can dominate at scale; retain a full 10k timing result and exact comparison before accepting such a path.

## [2026-10-23] Keep sort-validation operand direction consistent with the comparator

**Symptom:** The grouped spill writer rejected a sorted run during the maximum-term smoke at pair
17 in bucket 645, reporting a negative digest-order result even though the source array had just
been sorted.

**Root cause:** The bucket comparison used `current.CompareTo(previous)`, but the digest comparison
used `CompareBytes(previous, current)` and then applied the same “negative means out of order”
rule. For increasing digests that operand order correctly returns negative, so a valid run was
refused.

**Fix / workaround:** Compare the current digest with the previous digest before applying the
ascending-order check. The focused smoke then passed the maximum 200,900-code-unit canonical
document (3,005 terms), the 900,000-pair multi-pass grouped spill and all nine exact 100,000-ordinal
final postings.

**Watch out for:** A sort validator must use one operand convention for every key component. A
locally reversed tie-break comparison can make a correct `Array.Sort` look invalid; exercise
different adjacent keys as well as repeated keys.

## [2026-10-23] PowerShell treats `$home` as the reserved `$HOME` variable

**Symptom:** An SDK launcher failed before starting any child when assigning `$home` to the
repository-local CLI home, with “Cannot overwrite variable HOME because it is read-only or
constant.”

**Root cause:** PowerShell variable names are case-insensitive, so `$home` resolves to the
automatic read-only `$HOME` variable.

**Fix / workaround:** Use a nonreserved name such as `$cliHomePath` for the local SDK home, while
setting only process-scoped `DOTNET_CLI_HOME`.

**Watch out for:** Avoid `$home` in PowerShell scripts that prepare a local .NET CLI environment;
the assignment can fail before the sentinel check or any child starts.

## [2026-10-23] Time the complete owner handler, not only the search core

**Symptom:** A14’s 100k warm `SearchOwnerCore` p95 was 680.55 ms, while A15’s actual `owner.library.search` handler p95 was 2,167.332 ms. The direct-core number would have hidden a material over-target handler regression.

**Root cause:** The handler performs request validation, canonical generation/digest checks, scope construction, exact served-revision verification, and reply conversion in addition to the core search. Those costs are part of the caller-visible operation and can be significant at 100k resources.

**Fix / workaround:** Time the exact owner-handler entry point with the supported fresh-child protocol, separately retain cold and warmup calls, capture the comparator’s actual result and child exit code, and sample idle working set/CPU independently. Keep partial/interrupted samples as partial evidence rather than calling them API p95.

**Watch out for:** Core p95, a few completed handler calls, an empty stderr file, or a wrapper that merely starts a process does not prove the full protocol passed. Report only a complete timed sample with its comparator and process-exit evidence, and never treat fixture success as service/UI/IPC acceptance.

## [2026-10-23] Assert the owner error code, not a synthetic status token

**Symptom:** The first resumed owner-search smoke expected `ServiceReply.Status == "denied"` for stale-generation and locked-vault refusals and failed, despite the handler returning the intended error.

**Root cause:** The shared `Error(code)` reply uses `Status == null` and stores the refusal in `Error`; `"denied"` is not a status emitted by this handler.

**Fix / workaround:** Assert both the null status and the exact `Error` code (`search_index_stale` or `vault_locked`). Keep the failure output’s actual status and error in the assertion so a later contract change is distinguishable from a test’s guessed token.

**Watch out for:** A mismatched expected status is a test assertion defect, not evidence that the handler refused the wrong way. Inspect the shared reply-construction contract before changing product behavior.

## [2026-10-23] Relocated .NET intermediates need explicit source-glob exclusions

**Symptom:** The isolated A17 focused build first failed with duplicate assembly attributes, and a later runner build included generated files from older `obj*`/intermediate output folders.

**Root cause:** Moving `BaseIntermediateOutputPath` outside the project’s conventional `obj` directory, then building from a harness root that contains multiple output generations, caused generated source trees to fall back into default compile globs unless the local intermediate/output patterns were explicitly excluded.

**Fix / workaround:** Preserve the existing directories and explicitly exclude project-local `obj`/`bin`, `obj*/`, `intermediate-output*/`, `out-*/`, `temp-*/` and evidence trees in both default item-exclusion properties; give each attempt unique intermediate and output paths.

**Watch out for:** Never delete or blanket-clean another attempt’s `obj` or output to work around duplicate attributes. Isolate the new build and correct its compile globs instead.

## [2026-10-23] Move owner collection mutations after agent-session assertions

**Symptom:** A17's focused smoke failed an agent stale-revision assertion after adding an owner collection membership/search case.

**Root cause:** `OwnerScopeGrantAuthority.SetCollection` advances `PolicyGeneration` when collection state changes (`src/MetaBrain.Core/Security/OwnerScopeGrants.cs:168, 216–217`). That invalidates an agent session pinned to the previous policy generation, so mutating the collection before the agent-session checks makes the later stale-revision expectation unreachable.

**Fix / workaround:** Run the owner collection mutation/search after the agent session/revision assertions. The final focused `s8-t5-index` smoke then passed. This was test ordering, not a product behavior defect.

**Watch out for:** Any owner policy-generation mutation can stale existing agent sessions. Keep test setup and policy changes ordered around session-generation expectations; do not weaken the production generation checks to make a test pass.

## [2026-10-23] A faster rebuild batch can be a memory regression; the host guard is the arbiter

**Symptom:** The A18 run with a bounded 8-envelope rebuild read batch made progress faster but hit the mandatory 1 GiB host free-memory guard after ~85 s of rebuild (`free_bytes=1052467200`), while the serial run on the same host completed the whole protocol with a 2,919,084,032 B minimum.

**Root cause:** Not established. No per-process managed/GC/private samples were captured during the batched rebuild, and the only measured difference from the known-serial configuration was the batch. Host free memory is a whole-host quantity, so the decline cannot be attributed to the batch from these counters alone.

**Fix / workaround:** Revert to the serial one-envelope rebuild read for the measured run, record the aborted run's scratch artifacts and hashes, and describe the observation as a correlation. If batching is revisited, capture per-process memory during the rebuild and attribute before claiming a cause.

**Watch out for:** Never present a guard-aborted run as a performance result, and never lower or bypass the free-memory floor to make a run finish. A faster phase that endangers the host is not a safe optimization.

## [2026-10-23] PowerShell `Start-Process -PassThru` without `-Wait` does not expose `ExitCode` here

**Symptom:** The A18 run-1 wrapper recorded `child_exit_code=` (empty) even though the child had exited, and `$process.ExitCode` stayed `$null` after `WaitForExit(milliseconds)` and a second `WaitForExit()`.

**Root cause:** In this environment the `Process` object returned by `Start-Process` without `-Wait` does not surface the child's exit code. A dummy child that runs `exit 88` reproduced the empty value; the same child under `Start-Process -PassThru -Wait` returned 88, and a direct `System.Diagnostics.Process` with `WaitForExit()` also returned 88.

**Fix / workaround:** Use `Start-Process -FilePath ... -PassThru -Wait` then read `ExitCode`, or drive `System.Diagnostics.Process` directly. Record `exit_code_source` in the wrapper and treat an empty code as unknown, never as success.

**Watch out for:** PowerShell event-handler scriptblocks used for asynchronous output redirection did not reliably see script-scope variables in this host; prefer `Start-Process` native redirection. Any harness that prints an exit code must prove the capture path with a synthetic non-zero child before the real run.

## [2026-10-23] A persisted derivative is only as useful as its load path

**Symptom:** After the encrypted private index became persistent, every lock/unlock and every process restart still forced a full rebuild (A18: 1,396,956.547 ms at 100k) before any search could answer, although the encrypted generation was intact and `Open()` already authenticated it.

**Root cause:** `ResetPrivateIndex` clears `_privateIndexMaterialized` and the resident engine on lock and unlock, and only the explicit `owner.index.rebuild` handler ever set the snapshot again. The persistent artifact had no load-only consumer, so persistence saved disk but not restart time.

**Fix / workaround:** Add an authority-level `Load` that opens and authenticates the published generation and returns a snapshot only when its authenticated canonical digest and row count match the live canonical set, then resolve it lazily from owner search, scoped search and index status under one epoch-serialized policy. Fail closed on missing/stale/unreadable and keep the explicit rebuild as the only way to publish a changed set.

**Watch out for:** Do not treat "the file exists" as "the generation is current": compare the authenticated digest to the live canonical set and drop resident state on mismatch. Also do not assume the file format supports record deltas — a whole-generation sealed file with whole-vault postings still requires a full rewrite per change. Test the load path at the handler boundary, including a second process, and assert the encrypted file hash is unchanged by the read.

## [2026-10-24] A listed CLI command can still be missing from dispatch

**Symptom:** Real MCP `metabrain_authorize` calls returned `isError: true` with the complete owner CLI usage text instead of an authorization decision.
**Root cause:** `AuthorizeAsync` existed and `Usage()` listed `authorize`, but `OwnerControlCli.RunAsync` omitted it from the command switch, so the command fell through to usage before argument/session processing.
**Fix / workaround:** Dispatch `authorize` to the existing handler; keep the shared `agent.access.authorize` policy and path-only DPAPI session-file flow unchanged. Exercise the actual MCP-to-CLI process path, and retain allow/deny assertions at the real service boundary.
**Watch out for:** A handler and help entry do not prove CLI dispatch is wired. Check the command switch and run an end-to-end consumer regression rather than testing copied arguments or response echoes.


## [2026-10-24] An existing CLI command can fall through when a neighboring switch arm is added

**Symptom:** After `authorize` was added to the owner command switch, existing `revoke-session` callers returned usage even though the handler and help text remained.
**Root cause:** The switch edit replaced the `revoke-session` arm instead of adding a separate `authorize` arm.
**Fix / workaround:** Keep both dispatch arms and exercise the real `revoke-session` CLI path with a valid session ID and a random nonexistent control-pipe name; the handler's `transport_unavailable` refusal is distinguishable from the default usage result without creating a pipe server.
**Watch out for:** Review adjacent switch arms when adding a command. An invalid-argument probe can be nondiscriminating when both the handler and default branch return usage; prefer an observable command-specific boundary that does not execute the service operation.
