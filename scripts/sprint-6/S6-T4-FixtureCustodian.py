#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""S6-T4 synthetic-fixture credential custodian and provisioning harness.

This is the corrected harness for the S6-T4 finding: the earlier fixture
setup scraped ``owner provision`` stdout with a wrapper and printed the
synthetic recovery key into a tool transcript once. This harness never does
that:

* the vault passphrase is generated in this process and exists only in memory;
  it is handed to the CLI on stdin, never as a command-line argument, an
  environment variable, a file, or a log line;
* ``owner provision`` / ``owner recover`` / ``owner unlock`` run with captured
  pipes, so their stdout/stderr never reaches a terminal, a tool transcript or
  a file; the recovery key is parsed out of the captured bytes into memory;
* every string this harness could log passes through :func:`_filter`, which
  removes any in-memory credential from it first, and the harness records
  ``SECRET_FILTERED`` as a boolean self-check of its own captured buffers (no
  external transcript or history is scanned and no secret is ever echoed);
* ``recover`` proves the parsed recovery key is actually usable without ever
  showing it;
* ``guard-revoke`` asserts each revoke precondition before it is treated as
  satisfied and only then admits the guarded step, so a silently refused
  revocation can never be followed by a billable provider send (the S6-T4
  unplanned-call finding).

Commands arrive as appended lines in ``<root>/custodian-in.txt``; only
redacted status is appended to ``<root>/custodian-out.txt``. Run it detached
from any kill-prone automation kernel so a service restart can still be
unlocked after an unrelated automation failure.
"""

from __future__ import annotations

import base64
import os
import pathlib
import secrets
import subprocess
import sys
import time

ROOT = pathlib.Path(os.environ.get("S6T4_FIXTURE_ROOT", "runtime/fixtures/S6-T4-A2"))
REPO = os.environ.get("S6T4_REPO_ROOT", "F:/ai-ml/my-meta-brain")
IN_PATH = ROOT / "custodian-in.txt"
OUT_PATH = ROOT / "custodian-out.txt"
RECOVERY_PREFIX = "RECOVERY KEY (save outside the vault; shown once): "

# In memory only. Never printed, never persisted, never placed in argv/env.
PASSPHRASE = base64.b64encode(secrets.token_bytes(24)).decode()
RECOVERY_KEY: str | None = None

SERVICE: subprocess.Popen[bytes] | None = None
CONNECTIONS: str | None = None
PIPE: str | None = None
PROCESSED = 0
REVOKE_GATE = "none"
SECRET_FILTERED = True

# Failures a single malformed queue line may raise; the queue loop reports the
# type and message and keeps serving instead of dying on one bad command.
COMMAND_FAILURES = (
    OSError,
    ValueError,
    TypeError,
    KeyError,
    IndexError,
    AssertionError,
    subprocess.SubprocessError,
)


def _filter(text: str) -> str:
    """Remove every in-memory credential from a string before it is logged."""
    global SECRET_FILTERED
    filtered = text
    for secret in (PASSPHRASE, RECOVERY_KEY):
        if secret and secret in filtered:
            filtered = filtered.replace(secret, "<redacted>")
            SECRET_FILTERED = False
    return filtered


def log(text: str) -> None:
    with open(OUT_PATH, "a", encoding="utf-8") as handle:
        handle.write(_filter(text.rstrip()) + "\n")
        handle.flush()


def run_cli(
    args: list[str], stdin: str | None = None, timeout: int = 180
) -> subprocess.CompletedProcess[str]:
    assert CONNECTIONS is not None, "paths must be set before any command runs"
    return subprocess.run(
        [CONNECTIONS, "owner", *args],
        cwd=REPO,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        input=stdin,
        timeout=timeout,
    )


def record(result: subprocess.CompletedProcess[str], name: str) -> None:
    """Records a non-secret command outcome: the code, its stdout, its stderr."""
    log(name + " rc=" + str(result.returncode))
    if result.stdout.strip():
        log(result.stdout.rstrip())
    if result.stderr.strip():
        log("ERRTEXT " + result.stderr.rstrip()[:400])


def assert_answer(substring: str, args: list[str], label: str) -> bool:
    result = run_cli(args)
    ok = result.returncode == 0 and substring in result.stdout
    log(
        "ASSERT "
        + label
        + " "
        + ("OK" if ok else "FAIL")
        + " rc="
        + str(result.returncode)
    )
    if not ok:
        if result.stdout.strip():
            log(result.stdout.rstrip()[:400])
        if result.stderr.strip():
            log("ERRTEXT " + result.stderr.rstrip()[:400])
    return ok


def status() -> None:
    result = run_cli(["status", "--control-pipe", PIPE or ""])
    log("STATUS " + result.stdout.strip()[:200])


def wait_ready(seconds: int = 40) -> bool:
    deadline = time.time() + seconds
    while time.time() < deadline:
        try:
            result = run_cli(["status", "--control-pipe", PIPE or ""], timeout=30)
        except (OSError, subprocess.SubprocessError):
            result = None
        if (
            result is not None
            and result.returncode == 0
            and "SERVICE running" in result.stdout
        ):
            return True
        time.sleep(0.5)
    return False


def log_is_credential_free() -> bool:
    """True when this harness's own output carries no in-memory credential.

    It reads back only its own log file; nothing else is scanned and no
    credential is ever printed.
    """
    try:
        text = OUT_PATH.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return False
    return (RECOVERY_KEY is None or RECOVERY_KEY not in text) and PASSPHRASE not in text


def provision() -> None:
    """Provisions with the in-memory passphrase and parses the recovery key
    out of the captured output, which never leaves this process."""
    global RECOVERY_KEY
    result = run_cli(
        ["provision", "--control-pipe", PIPE or ""],
        stdin=PASSPHRASE + "\n" + PASSPHRASE + "\n",
    )
    captured = result.stdout + result.stderr
    for line in captured.splitlines():
        if RECOVERY_PREFIX in line:
            RECOVERY_KEY = line.split(RECOVERY_PREFIX, 1)[1].strip()
            break
    log(
        "OK provision rc="
        + str(result.returncode)
        + " recoveryCaptured="
        + str(RECOVERY_KEY is not None)
        + " length="
        + str(len(RECOVERY_KEY or ""))
        + " stdoutChars="
        + str(len(captured))
    )
    log("PROVISION_OUTPUT_LOGGED=False")
    log("SECRET_FILTERED=" + str(log_is_credential_free()))


def handle_line(line: str) -> None:
    global SERVICE, CONNECTIONS, PIPE, REVOKE_GATE
    command, _, rest = line.partition(" ")
    rest = rest.strip()
    if command == "ping":
        log("OK ping")
    elif command == "paths":
        CONNECTIONS = rest
        log("OK paths")
    elif command == "pipe":
        PIPE = rest
        log("OK pipe")
    elif command == "start":
        assert CONNECTIONS is not None, "paths must be set before start"
        # Popen duplicates the handle for the child, so closing the parent's copy
        # here is safe and the service keeps writing to the same log.
        with open(ROOT / "service.log", "ab") as stream:
            SERVICE = subprocess.Popen(
                [CONNECTIONS, "serve-console", "--config", rest],
                cwd=REPO,
                stdout=stream,
                stderr=subprocess.STDOUT,
                stdin=subprocess.DEVNULL,
            )
        log("OK start ready=" + str(wait_ready()))
    elif command == "stop":
        if SERVICE is not None:
            SERVICE.terminate()
            try:
                SERVICE.wait(timeout=20)
            except subprocess.TimeoutExpired:
                SERVICE.kill()
            SERVICE = None
            time.sleep(1.0)
        else:
            log(
                "STOP no process handle held; an externally started service is not killed"
            )
        log("OK stop")
    elif command == "provision":
        provision()
    elif command == "unlock":
        result = run_cli(
            ["unlock", "--control-pipe", PIPE or ""], stdin=PASSPHRASE + "\n"
        )
        log(
            "OK unlock rc="
            + str(result.returncode)
            + " unlocked="
            + str("VAULT unlocked" in result.stdout)
        )
    elif command == "recover":
        if RECOVERY_KEY is None:
            log("ERR no captured recovery key; provision first")
            return
        result = run_cli(
            ["recover", "--control-pipe", PIPE or ""], stdin=RECOVERY_KEY + "\n"
        )
        log(
            "OK recover rc="
            + str(result.returncode)
            + " unlocked="
            + str("VAULT unlocked" in result.stdout)
        )
    elif command == "status":
        status()
    elif command == "assert":
        substring, _, arguments = rest.partition(" ")
        ok = assert_answer(substring, arguments.split() if arguments else [], substring)
        if not ok:
            REVOKE_GATE = "failed"
    elif command == "guard-revoke":
        fields = rest.split(" ")
        if len(fields) < 3:
            log("ERR guard-revoke needs <sourceId> <zoneId> <locatorRequestFile>")
            return
        source_id, zone_id, locator_file = fields[0], fields[1], fields[2]
        enrolled = assert_answer(
            "life=enrolled",
            [
                "source-enroll",
                "--control-pipe",
                PIPE or "",
                "--source-id",
                source_id,
                "--zone-id",
                zone_id,
                "--format",
                "research.note",
                "--format-version",
                "1",
                "--input-file",
                locator_file,
            ],
            "source-enroll " + source_id,
        )
        revoked = enrolled and assert_answer(
            "life=revoked",
            ["source-revoke", "--control-pipe", PIPE or "", "--source-id", source_id],
            "source-revoke " + source_id,
        )
        REVOKE_GATE = "satisfied" if revoked else "failed"
        log("PRE_REVOKE_GATE=" + REVOKE_GATE.upper())
    elif command in ("run", "run-guarded"):
        args = rest.split(" ") if rest else []
        if not args:
            log("ERR no arguments")
            return
        if args[0] in ("provision", "recover", "unlock"):
            log("ERR refused secret-bearing command")
            return
        if args[0] in ("job-create", "job-start") and REVOKE_GATE != "satisfied":
            log(
                "REFUSED "
                + args[0]
                + " without a confirmed revoke precondition (gate="
                + REVOKE_GATE
                + ")"
            )
            return
        record(run_cli(args), "RC")
    elif command == "ledger":
        name = rest or "ledger"
        result = run_cli(["job-ledger", "--control-pipe", PIPE or ""])
        (ROOT / (name + ".ledger")).write_text(result.stdout, encoding="utf-8")
        log(
            "LEDGER "
            + name
            + " rc="
            + str(result.returncode)
            + " lines="
            + str(len(result.stdout.splitlines()))
        )
    elif command == "quit":
        log("OK quit")
        sys.exit(0)
    else:
        log("ERR unknown command")


def main() -> None:
    global PROCESSED
    ROOT.mkdir(parents=True, exist_ok=True)
    log(
        "CUSTODIAN READY (credential held in memory only; provisioning output never leaves this process)"
    )
    while True:
        try:
            if IN_PATH.exists():
                lines = IN_PATH.read_text(
                    encoding="utf-8", errors="replace"
                ).splitlines()
                while PROCESSED < len(lines):
                    line = lines[PROCESSED].strip()
                    PROCESSED += 1
                    if not line:
                        continue
                    try:
                        handle_line(line)
                    except COMMAND_FAILURES as exc:
                        log("ERR " + type(exc).__name__ + ": " + str(exc)[:200])
        except COMMAND_FAILURES as exc:
            log("ERR loop " + type(exc).__name__ + ": " + str(exc)[:200])
        time.sleep(0.3)


if __name__ == "__main__":
    main()
