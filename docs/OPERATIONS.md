# Operations — installing and running Meta Brain every day

Meta Brain is installed for **one owner account** on Windows. The owner installs it, starts it,
unlocks it, and uses it; the agents connect to it as scoped clients. Nothing in this runbook needs
administrator rights, an AppContainer, a patched agent runtime, or an account of its own.

- Product scope and requirements: [`META-BRAIN.md`](META-BRAIN.md), [`REQUIREMENTS.md`](REQUIREMENTS.md).
- Sprint plan and acceptance for this work: [`sprint-plans/sprint-8.md`](sprint-plans/sprint-8.md).

## 1. The two roots

| Root | Holds | Removed by |
| --- | --- | --- |
| **Install root** — e.g. `%LOCALAPPDATA%\MetaBrain\App` | The application files: `service/`, `owner-ui/`, `installer/`, `install-state.json` | `uninstall` |
| **Data root** — e.g. `%LOCALAPPDATA%\MetaBrain\Data` | The owner's private data: `service-settings.json`, `vault/`, the runtime state (`runtime/`), the source snapshots, the backups and the recovery material | **never** by the installer |

The two roots are separate on purpose: an upgrade replaces the application files and leaves the
data alone, and an uninstall removes the application and keeps the data. The two roots must not
overlap: the installer **refuses** a data root inside the install root and the other way round,
because an uninstall removes the install root and would take the data with it. Give the two roots
sibling directories, as in the example above (`…\MetaBrain\App` and `…\MetaBrain\Data`). A data
root *inside* the install root (`…\MetaBrain` and `…\MetaBrain\data`) is refused with
`The data root must not live inside the install root: an uninstall removes the install root.`

On the daily path — `install`, `start`, `stop`, `restart`, `status`/`health`, `autostart`,
`uninstall`, the owner unlock and the agent session — the product **does not change the
permissions of a path**: it creates the data root, the settings file, the vault directory and its
envelopes, the runtime directory and its log with the ordinary permissions of the owner account,
and it never takes ownership of a path. The privacy of the owner's data comes from the key the
owner controls, the authenticated encryption around every persisted record, the lock of the vault
and the owner-approved catalog, token and scoped-session APIs — not from the access rights of the
file system. The owner channel and the agent pipe are the two endpoints, and each of them accepts
the owner account only; a pipe receives that descriptor when the endpoint is created, which is not
a change to the permissions of an existing path.

**One exception, outside the daily path.** The inherited legacy migration command
(`MetaBrain.Connections.exe owner migrate --config <legacy-settings.json>`) still applies an
owner-only access rule and takes the owner of the migrated settings file. It is not reachable from
the daily path — the service refuses a legacy settings file with `Legacy plaintext settings
require the explicit owner migration command before service startup.` — it was not exercised for
this release, and **it must not be run while it still mutates permissions** (§11).

## 2. Install

```text
MetaBrain.Installer.exe install --payload <published-payload-dir> --install-root <dir> --data-root <dir> [--control-pipe <name>] [--agent-pipe <name>] [--autostart] [--start]
```

- `--payload` — the published application: `service/MetaBrain.Connections.exe`,
  `owner-ui/MetaBrain.OwnerUi.exe`, `installer/MetaBrain.Installer.exe`.
- `--install-root` — where the application files go.
- `--data-root` — where the owner's private data goes. It is created with the ordinary permissions
  of the owner account. It must be a **sibling** of the install root, not a directory inside it
  (§1): the installer refuses overlapping roots.
- `--control-pipe` / `--agent-pipe` — optional fixed endpoint names. Without them the installer
  generates random ones. Fixing them lets the owner point a client at a known endpoint; an upgrade
  keeps the endpoints an existing installation already uses.
- `--autostart` — opt-in login start (see §6). Without it, nothing is registered.
- `--start` — start the service right away.

A first install with sibling roots, exactly as the runbook intends it:

```text
MetaBrain.Installer.exe install --payload <published-payload-dir> --install-root %LOCALAPPDATA%\MetaBrain\App --data-root %LOCALAPPDATA%\MetaBrain\Data
```

An install **never creates the vault**: the first start is locked and uninitialized, and the owner
creates the vault explicitly (§4). Installing over an existing install root is an upgrade of the
application files; the data root, the vault and the pipe endpoints are kept.

## 3. The daily commands

Every command below is run from the installed copy in `<install root>\installer`, and every one of
them accepts `--install-root <dir>` to address an installation other than the one the running
installer belongs to. `uninstall` is the one exception: it takes no options at all (§8).

```text
MetaBrain.Installer.exe start        # start the service host (locked)
MetaBrain.Installer.exe stop         # clean stop: the host drains, locks the vault and exits
MetaBrain.Installer.exe restart      # stop and start again
MetaBrain.Installer.exe status       # health: service, vault state, process, autostart
MetaBrain.Installer.exe health       # the same report as status
```

`status` exits with `0` when the owner channel answers and with a non-zero code when it does not,
so it can be used as a health check. The report contains the install root, the data root, the
settings file, both pipe endpoints, the running process (pid plus whether it is still that
process), the service state, the vault state and the autostart state.

The stop is a *request*, not a kill: the installer asks the host to stop, waits for the process to
exit and confirms the exit. The host identifies itself with its pid **and** its start time, so a
recycled pid is never mistaken for the running host.

## 4. The locked start and the owner unlock

Every start of the host begins **locked**. The key lives only in the unlocked process memory, so:

- after a stop, a restart, a logout or a reboot the vault is locked again;
- no agent session survives a restart: the session is process memory, not a file.

The owner unlocks explicitly:

```text
MetaBrain.Connections.exe owner provision --control-pipe <control-pipe>   # first time only
MetaBrain.Connections.exe owner unlock   --control-pipe <control-pipe>
MetaBrain.Connections.exe owner lock     --control-pipe <control-pipe>
```

`provision` asks for the vault passphrase twice and prints a one-time recovery key; store that key
outside the vault. `unlock` accepts the passphrase or, with `owner recover`, the recovery key.
The owner console (`owner-ui/MetaBrain.OwnerUi.exe`) drives the same operations.

### 4.1 Encrypted backup, restore and owner-scoped export

Three owner acts live on the owner control channel. They are owner-only: the agent channel refuses
them, and no agent can mint, widen or reuse any of them.

```text
# 1. One encrypted snapshot of the unlocked vault
MetaBrain.Connections.exe owner backup --control-pipe <control-pipe> --output-file <backup file>

# 2. Restore that snapshot into a clean directory
MetaBrain.Connections.exe owner restore --control-pipe <control-pipe> \
    --backup-file <backup file> --target-directory <directory that does not exist yet>
MetaBrain.Connections.exe owner restore-with-recovery-code --control-pipe <control-pipe> \
    --backup-file <backup file> --target-directory <directory that does not exist yet>

# 3. Export the records you selected as one readable plaintext file
MetaBrain.Connections.exe owner export --control-pipe <control-pipe> \
    --resource-ids <id>,<id> --output-file <file> \
    [--include-relations true] [--include-lifecycle-states true]
```

**In the owner console.** The same three acts are on the console view **Backup & export**
(`owner-ui\MetaBrain.OwnerUi.exe --control-pipe <control-pipe>`), so the owner never has to type an
operation, a resource ID or a path:

- **1. Encrypted backup** — pick the file with *Choose file…* (or type the path) and choose
  *Create encrypted backup*. The vault has to be unlocked; the file is written once and an
  existing file is never overwritten.
- **2. Clean restore into a new folder** — pick the backup file, pick the folder the restored
  vault should go into (it must not exist yet), choose *Owner passphrase* or *Recovery key*, type
  the credential and choose *Restore into the new folder*. The credential travels in that one
  request and the field is cleared right after it. A restore works while the vault is locked,
  because it opens a backup file and writes a new folder instead of reading the live vault. The
  live vault is never replaced: to work with the restored copy, point a vault directory setting at
  it.
- **3. Explicit readable export** — search for the records you want to copy out, choose them by
  their title in the ranked list, and — if the records cite a source — choose the cited source the
  service names, because a citation to a source outside your selection is withheld whole. Then pick
  the file and choose *Write the readable copy*. The file is plaintext, it is written once, and a
  copy that has left the vault is not revoked by a later lock, purge or export.

Locking the vault, losing the service or ending the owner session clears this view: the chosen
paths, the export selection and the credential field go with it, and no answer that was already in
flight can restore them.

**What the backup file is.** One file that holds the encrypted snapshot and the wrapped key
envelope of the vault. It never holds the data key, a passphrase, a recovery key, a token, a session
or any record content in readable form: the snapshot is sealed with the vault's data key, and the
envelope in the file is the same passphrase-wrapped and recovery-wrapped key the live vault header
already holds. A backup file on its own opens nothing; a backup file **together with your
passphrase or your recovery key** opens your data, which is exactly the same boundary as the live
vault. Keep backups where you would keep the recovery key.

**What a restore does.** It reproduces your data — every record, its exact revision, its relations,
its sources, its lifecycle and intention states, its archive and retained revisions, and the fact
that something was deleted — into a directory that must not exist yet. The restored vault opens with
your own passphrase or your own recovery key, because the restore carries the owner's key envelope
and never re-keys it. The restored vault carries **no live authority**: no scope grant, no agent
session and no redeemable token comes back, and a token that was already used stays used. The
private index is not restored either; it is rebuilt from the restored canonical records
(`owner index-rebuild`).

**What fails closed.** A wrong passphrase, a wrong recovery key, a lost key, a tampered container
and a truncated container all refuse the restore, write nothing, leave no partial vault behind and
never reset, re-key or invent a recovery. A refused restore leaves no target directory at all. There
is no fallback key and no "recovered" mode: if you have lost both the passphrase and the recovery
key, the backup cannot be opened.

**Compatibility.** The backup format is versioned (`metabrain/vault-backup` v1) and the restore
reads the version it writes. A backup taken by this build restores with this build. A future format
change must keep reading the older backups or state plainly that it does not; no backup is
silently dropped.

**Retention.** The product does not delete your backups for you. A backup keeps its own ciphertext
until you delete that file yourself, and a backup taken before a purge still holds the ciphertext
of what was purged: the purge ledger names what was deleted, and a restore re-applies that ledger
instead of resurrecting the purged records. Deleting a backup file is the only way to remove that
copy.

**The export is a disclosure, and it cannot be taken back.** The export file is plaintext and holds
exactly the records you selected, in full, as the vault stores them. A citation to a source outside
your selection is withheld whole — neither the source, its locator nor its span leaves the vault —
and the export says in its own header how many citations, relations and records it withheld. The
file is written once and never over an existing file. A copy that was already made is not revoked
by a later purge, a lock or another export: the vault can delete the record, not the copy you took
out of it.

## 5. The owner console and the two clients

- **Owner console**: `owner-ui\MetaBrain.OwnerUi.exe --control-pipe <control-pipe>` — the owner's own
  surface: connect, vault status, vault and key, library, inbox capture and proposal
  review, relations, sharing (catalog, access requests, sessions), sources
  (allowlist, mapping review, collections, snapshots), jobs, intentions and
  review queue. Every screen works from lists the service answered: pick a zone,
  resource, operation, expiry duration or file with the on-screen pickers
  instead of typing internal IDs; outcomes carry ✓/✕/⚠ labels, never color
  alone. A zone is a permission boundary, a collection is your own folder
  label. Collection members are chosen from a library search or the open item
  (titles first, IDs secondary) and can be removed/re-added before saving. An
  intention is chosen from the service search, and its trigger is entered as a
  local date, time and time zone; the console resolves the offset for that date,
  refuses a local time that is invalid or ambiguous at a daylight-saving
  boundary, and shows the exact UTC instant before sending. Job inputs are a
  list added from the open item or the saved capture, not a typed
  `resourceId@revision` line; provider/model/endpoint remain explicit external
  configuration and the API key is never shown again. Approving an access
  request ticks resources and operations from the bound preview (narrowing
  only), picks an expiry duration (UTC instant) and a new local handoff file;
  the bearer is sealed into that file and never shown.
- **Repo-local synthetic UI review instance** (no install, autostart, service
  registration, profile or permission change). From the repository root:

    ```powershell
    powershell.exe -NoProfile -File scripts\owner\start-owner-review.ps1
    ```

  This script publishes the owner service and the owner console from the
  repository into `runtime\owner-review\payload` (repository-local toolchain:
  the CLI home, the package cache and the temp directories all stay inside the
  repository), writes a synthetic service settings file for the current
  account, starts the service on `runtime\owner-review\data`, waits for the
  owner channel and opens the owner console against it. Closing the console
  stops the synthetic service; stop it independently with the same script and
  `-Stop`. Add `-ServiceOnly` to start the service without opening the console.
  The **Backup & export** view of that console is where the owner reviews the
  encrypted backup, the clean restore and the explicit readable export. This
  script changes nothing outside the repository: no installation, no service
  registration, no autostart entry, no registry value, no environment or
  profile change and no permission change. It runs under the machine's existing
  PowerShell execution policy: it needs no execution-policy override and never
  installs a certificate, changes a trust store, runs an update or writes a
  profile setting. If your policy blocks the script, PowerShell reports that
  restriction — the launcher does not weaken the policy for you.

  The older launcher of the S8-T1A walkthrough is still there and needs a
  published payload of its own:

    ```powershell
    powershell.exe -NoProfile -File runtime\s8-t1a\review\start-owner-ui.ps1
    ```

  The script writes a private settings file with the current user SID, starts
  the published service on the review data root, waits for the owner channel,
  opens the published owner console, and stops the service when that console
  closes. Its synthetic vault is `runtime\s8-t1a\review\data` (first start is
  uninitialized: use **Vault & key** to create a review-only passphrase). Stop
  independently with:

    ```powershell
    powershell.exe -NoProfile -File runtime\s8-t1a\review\stop-owner-ui.ps1
    ```

  The stop script writes the service's own `service.stop` request under
  `runtime\s8-t1a\review\data\runtime` and waits for the host process to exit.
  Deleting that data root removes only this synthetic review instance.
- **Official Codex / OMP clients**: they connect as agents over the **agent pipe**, never over the
  owner pipe. The agent pipe carries only what an owner-approved grant allows, and the agent can
  neither unlock, approve, publish nor purge.

The agent bridge is started by the client, not by the installer:

```text
MetaBrain.Connections.exe agent-mcp --agent-pipe <agent-pipe>
```

The owner issues a scope grant with `owner grant`, the client redeems the one-time handoff with
`owner redeem`, and the resulting session is what the client uses. The grant is the only authority:
an agent token, a working directory or a same-account process does not create owner authority.

## 6. Autostart (explicit opt-in)

```text
MetaBrain.Installer.exe autostart enable   # writes the per-user login entry
MetaBrain.Installer.exe autostart status   # read-only
MetaBrain.Installer.exe autostart disable  # removes it
```

The three autostart commands accept an optional `--install-root <dir>`; `enable` then registers
the host of that installation. The login entry itself is a single per-user value (`MetaBrain`),
so `status` and `disable` always refer to that one entry.

`autostart enable` writes one value under
`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` (`MetaBrain`) that starts the same installed
host in background mode. It needs no administrator rights because it lives in the current user's
own key. The installer never writes that entry on its own: it is written only when the owner asks
for it, and `uninstall` removes it when the entry still points at that installation.

A login start is locked exactly like every other start.

## 7. Restart, logout and reboot

- **Restart** (`restart`, or a crash): the host comes back locked, and no agent session survives.
- **Logout / reboot**: the host is gone. The next start — manual or through the autostart entry —
  is locked. Nothing is resurrected: no key, no session, no grant token.

## 8. Uninstall

```text
MetaBrain.Installer.exe uninstall
```

The uninstall **takes no options**. It always acts on the installation the running installer
belongs to, so it is run from the installed copy `<install root>\installer\MetaBrain.Installer.exe`
and it finds that installation through its own `install-state.json`. Passing any option is
refused with `The uninstall command takes no options.` — `uninstall --install-root <dir>` is not a
valid command, and no option is ever silently ignored. To remove a different installation, run
that installation's own installer.

The uninstall stops the host, removes the autostart entry (if it points at that installation),
removes the application files and **keeps the data root**: the vault, the source snapshots, the
backups and the recovery material. The installer has no option that deletes the owner's data;
removing it is a separate, deliberate act by the owner.

## 9. Diagnostics

The background host writes its operational messages to `<data root>\runtime\service.log`. The log
carries operational envelopes (readiness, refusals, failure types) — never a key, a token or
private content.

## 10. Verifying the installed lifecycle

The repository's permanent smoke drives the real installer and the real installed host end to end:

```text
dotnet run --project tests/MetaBrain.S1T4.Smoke/MetaBrain.S1T4.Smoke.csproj -- s8-t1 <published-payload-dir> <fixture-root>
```

It installs into a fixture inside the repository, starts the host, proves it is locked, provisions
and unlocks the vault, stores and reads an owner record, issues a grant, redeems it, uses a live
agent session, stops, restarts (locked again, session gone), uninstalls and proves the vault
survived. It also reads the access rules of the paths it watches — the settings file, the vault
directory, the `vault\resources` directory and the files inside it, the runtime log, the grant
handoff and the session handoff — once before the uninstall and once after it, and the run fails
if they differ. That comparison is a read-only observation of those paths across the stop,
restart and uninstall phase; it is not a comparison against a pre-creation baseline, so an
access rule that the product applied while creating a path would not be caught by it. The fixture
root has to live inside the repository: the smoke refuses a fixture outside it, and it
removes the fixture when the run ends.

## 11. Known limits

- The autostart entry, a real logout or reboot, and a connection from the official Codex/OMP
  clients need the owner in front of the machine; they cannot be proved by a headless run.
- The installer is Windows-only and per-user. There is no multi-account or shared-machine install.
- The vault is lost if the owner loses both the passphrase and the recovery key. There is no reset
  and no back door.
- The same owner account can read the owner's own files. The boundary is the key, the lock and the
  owner-approved APIs, not the access rights of the file system: malware running as that account is
  not stopped by file permissions.
- The legacy migration command (`owner migrate`, §1) still applies an owner-only access rule and
  takes the owner of the migrated settings file. It is not on the daily path, it was not exercised,
  and it must not be run while it still mutates permissions; the migration path belongs to the
  schema and key-format migration work and has to be brought inside the same boundary first.
