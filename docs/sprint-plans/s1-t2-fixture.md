# S1-T2 Fixture Runner — Operator Steps

**Historical only — CR01/D016 and S1-CLEAN:** This runbook belongs to the superseded OS-identity/ACL S1-T2, not the replacement key/unlock/lock task. Its fixture script was deleted; the script path and all command examples below are literal history, not live links or runnable steps. Do not rerun elevated service/ACL experiments. Retain this page as evidence history; new tasks and limits are in [Sprint 1](sprint-1.md). No production setup or crypto proof is claimed.

This historical runbook describes the temporary fixture-only Windows identity and ACL experiment for [Sprint 1 S1-T2](sprint-1.md). It was not a production installer or service deployment. Its deleted runner's historical location was `scripts/sprint-1/S1-T2-Fixture.ps1` (literal path only; no current source exists).

## Safety and Scope

The runner creates one new directory named `MetaBrain-S1-T2-<GUID>` directly under `%ProgramData%`, plus two demand-start test services whose names begin `MBS1T2P` and `MBS1T2N`. It uses two temporary virtual service identities (`NT SERVICE\<service-name>`), not local users or passwords. A completed run stops/deletes both services and removes the marked fixture. If service or fixture cleanup fails, the runner reports a recovery command and preserves the marked path when possible; if final directory removal fails after marker removal, it attempts to restore the marker. If it reports marker restoration failure, leave the path in place for review rather than using a generic recursive delete.

The fixture contains only synthetic, explicitly non-secret markers. No existing profile, vault, policy, credential, transcript, or other user data is read or copied; the runner accesses only the newly created synthetic markers. The `credentials` directory is an ACL test location; its marker is not a credential. The runner prints only synthetic test outcomes, account names/SIDs, and ACL entries. It never prints fixture file contents.

The fixture root and every protected directory/file receive explicit ACLs for the current owner SID and `SYSTEM`. The primary service identity gets only the rights exercised by the scenario: directory traversal, `Modify` on vault/control, and read/execute on credentials and the helper binary. A second, distinct non-administrator virtual service identity gets root traversal and read/execute on the test helper so the service can launch; it has no ACE on vault, control, credentials, or their marker files. The protected owner pipe allows only the owner and primary service SID. A separate result-only pipe lets the negative-test identity report its identity and fixed access-status fields; it carries no fixture data.

## Prerequisites

- Windows 10/11 x64 and Windows PowerShell 5.1.
- A local .NET Framework C# compiler available to Windows PowerShell. The helper is compiled only into the new fixture; .NET 10 SDK is not required by this PowerShell-only experiment.
- For `Run` and `Teardown`, an owner-approved elevated PowerShell session using the **same Windows account that owns the fixture**. Do not enter credentials for a different administrator account: the runner derives and verifies the owner SID from its process token.
- The temporary fixture is under `%ProgramData%`, outside the repository. Non-elevated `Check` creates and removes a fresh marked directory under `%TEMP%`; it also reads the existing `EventLog` service metadata and runs a no-change `sc.exe` argument probe.

## Run

First run `Check` under a non-elevated token. It validates the local helper/compiler and owner/`SYSTEM` ACL operations, then exercises the real `sc.exe` create, SID-type, and delete argument paths without registering or changing a service or testing cross-identity access:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File F:/ai-ml/my-meta-brain/scripts/sprint-1/S1-T2-Fixture.ps1 -Action Check
```

Do not proceed to `Run` until a fresh evidence review approves the corrected script and artifact. After that review, open an elevated Windows PowerShell window as the same owner account, move to the repository root, and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File F:/ai-ml/my-meta-brain/scripts/sprint-1/S1-T2-Fixture.ps1 -Action Run
```

`Run` creates the new fixture, compiles the service helper there, creates only the two temporary demand-start test services, configures each service SID as `unrestricted`, applies the bootstrap ACL, reapplies it as an installer/repair simulation, performs owner/service/negative file and pipe scenarios, prints ACL observations, then stops/deletes the test services and removes only the verified marked fixture. Its `SCOPED VERIFICATION PASS` line is emitted only if all assertions succeed and cleanup completes. A failed scenario is a failure, not a partial acceptance pass.

### Native service-command correction and local evidence

The owner reported that the earlier same-owner elevated run allocated the fixture, then `sc.exe create` exited `1639` with `Invalid start=` before any service-identity checks; the owner reported that the fixture was cleaned up. That report is the prior run's ground truth, not a result reproduced by this worker.

The runner now constructs a Windows command line with per-argument quoting and sends `create` fields as separate `binPath=` / value, `start=` / `demand`, and `obj=` / `NT SERVICE\<service>` arguments. Microsoft documents the required space between each option's `=` and its value ([`sc.exe create`](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-create)). After each successful create it calls `sc.exe sidtype <service> unrestricted` before applying service-SID ACLs or starting the service. The existing generated-name, image-path-checked rollback remains in place.

`Check` invokes the real `sc.exe` command path against the already-installed `EventLog` service (which cannot be replaced by `create`), with a spaced, nested-quoted synthetic binary path, then targets a generated absent service for `sidtype` and `delete`. This host observed the `create` failure exit `5`; `sidtype` and `delete` on the absent name each returned `1060`. The existing service image path remained unchanged and the generated service name remained absent. Exit `5` is access denied while opening the Service Control Manager, before `sc.exe create` parses its options; it is not evidence that `binPath=` or `start=` arguments were parsed correctly. The absent-name `1060` outcomes do not validate create syntax either. The corrected runner's separated and quoted arguments are exercised by the later owner-reported elevated `Run`, recorded in `artifacts/sprint-1/task-2.md`. `Check` does not create a service or prove service-token, ACL, or IPC behavior.

Expected identity-backed assertions when run elevated:

- Owner token: the owner SID has `FullControl` ACEs on the root and fixture objects, and can create/read/rename/delete fresh files in vault, control, and credentials before and after ACL reapplication.
- Primary service token: the observed pipe caller SID matches its `NT SERVICE\...` SID and is not an administrator; it reads vault/control/credential markers, writes only test files in vault/control, and connects to the owner pipe.
- Second test-service token: its observed result-pipe caller SID matches its distinct non-administrator service SID; reads of vault/control/credential markers, vault/control writes, and connection to the protected owner pipe are all denied.
- ACL output shows the actual owner, service, and `SYSTEM` SIDs and rights. File contents are never emitted.

A successful `Check` exercises the real `sc.exe` command path with non-mutating targets, but the create probe's access-denied exit occurs before option parsing and does not validate its argument syntax. It remains only a compiler/API and same-owner fixture smoke test, not evidence for service-token, unprivileged-token, ACL separation, or cross-identity IPC acceptance.

## Recovery / Teardown

If the shell closes or the run is interrupted before automatic cleanup, use the exact fixture path printed by the runner. From the same owner's elevated PowerShell session, run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File F:/ai-ml/my-meta-brain/scripts/sprint-1/S1-T2-Fixture.ps1 -Action Teardown -FixtureRoot 'C:/ProgramData/MetaBrain-S1-T2-<GUID>'
```

Replace the example with the actual generated path. Teardown requires the generated root name, an exact marker matching the owner SID and path, and no reparse point at the root. Before deleting a service it verifies that its registered image path points into that fixture; it stops and deletes only the two generated test service names. It refuses to recursively remove an unmarked directory or a path outside the direct `%ProgramData%` fixture area. If a service path does not match or cleanup cannot complete, the runner prints a recovery command and retains the marked fixture when possible; if marker restoration fails, leave the path in place and do not use a generic recursive delete.

## Acceptance Boundary

The runner's ACL reapplication is the fixture's bootstrap/repair path; no product service or production installer is present in this task. The owner has since reported a successful elevated same-owner `Run`, including the service-token, ACL, file/IPC, and cleanup scenarios; the redacted owner observations are recorded in `artifacts/sprint-1/task-2.md` for evidence review. That synthetic fixture result is not production-isolation evidence. The task remains `[~]` until the evidence gate decides whether the report satisfies S1-T2.
