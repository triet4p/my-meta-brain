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
