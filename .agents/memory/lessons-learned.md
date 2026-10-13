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
