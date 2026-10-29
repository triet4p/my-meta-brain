#!/usr/bin/env -S uv run --script
# /// script
# requires-python = ">=3.12"
# dependencies = []
# ///
"""S11-T1 synthetic ingestion corpus: deterministic generator and validator.

This script is the durable definition of the Sprint 11 synthetic corpus used by
`docs/CR02-MEASUREMENT-PROTOCOL.md`. It writes JSONL source variants plus a
manifest of the outcomes the CR02 proposal expects, and it validates the
generated tree byte for byte.

Every byte is synthetic. The script never reads a real .codex/.omp session and
never touches a vault: it only materializes fixture files under the requested
output directory (the default is the Git-ignored `artifacts/sprint-11/corpus`).

Schema 3 (2026-10-29) finalizes the corpus after the owner approved CR02 with
D1=B, D2-D8=A: every previous `decision_pending` marker is resolved, so all 20
case expectations are binding for S11-T3. Case bytes and the corpus digest are
unchanged from schema 2; only the pending metadata and schema number moved.

Usage:

    uv run --script scripts/sprint-11/S11-T1-Corpus.py generate
    uv run --script scripts/sprint-11/S11-T1-Corpus.py check
    uv run --script scripts/sprint-11/S11-T1-Corpus.py generate --out <dir>

`generate` refuses to replace a directory it did not create (marker file
`CORPUS-MARKER`). `check` regenerates the corpus in memory and verifies the
files, the manifest invariants, the query baseline and the folder enumeration
expectations.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import shutil
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Final, Literal

CODEX_FORMAT: Final = "codex.session.jsonl"
OMP_FORMAT: Final = "omp.session.jsonl"
MARKER: Final = "s11t1-synthetic"
CORPUS_SCHEMA_VERSION: Final = 3
MANIFEST_NAME: Final = "manifest.json"
MARKER_NAME: Final = "CORPUS-MARKER"
GENERATOR_PATH: Final = "scripts/sprint-11/S11-T1-Corpus.py"
DEFAULT_OUT: Final = Path("artifacts/sprint-11/corpus")

Interpretation = Literal["fully_supported", "partially_interpreted", "uninterpreted", "empty"]
_VALID_INTERPRETATIONS: Final = frozenset(
    {"fully_supported", "partially_interpreted", "uninterpreted", "empty"}
)


@dataclass(frozen=True)
class Expectation:
    """One expected ingestion outcome under one preservation policy."""

    interpretation: Interpretation
    consumed_offset: int
    event_count: int
    diagnostics: tuple[tuple[int, str], ...] = ()
    preserved_spans: tuple[tuple[int, int], ...] = ()
    advances_past_preserved: bool = False
    pending_tail_bytes: int = 0
    unaccounted_tail_bytes: int = 0
    version_barrier: bool = False


@dataclass(frozen=True)
class CorpusCase:
    case_id: str
    path: str
    source_format: str
    declared_format_version: int
    content: bytes
    reference_only: Expectation
    preserved: Expectation
    decision_pending: str | None = None


@dataclass(frozen=True)
class Query:
    query_id: str
    text: str
    kind: Literal["vietnamese", "english", "exact_symbol", "timestamp", "paraphrase"]
    expected_sources: tuple[str, ...]
    expected_absent: bool = False


@dataclass(frozen=True)
class FolderEntry:
    relative_path: str
    reason: str


@dataclass
class FolderCase:
    root_relative_path: str
    expected_included: tuple[str, ...]
    expected_excluded: tuple[FolderEntry, ...]
    link_relative_path: str
    link_target_relative_path: str
    outside_relative_path: str


# --- Fixture text builders -------------------------------------------------


def _codex_line(timestamp: str, ordinal: int, record_type: str, payload: str) -> bytes:
    text = (
        '{"timestamp":"' + timestamp + '","ordinal":' + str(ordinal)
        + ',"type":"' + record_type + '","payload":' + payload + "}\n"
    )
    return text.encode("utf-8")


def _codex_payload_lines() -> list[bytes]:
    """The 14 supported Codex envelope/payload shapes as one session prefix."""

    session = "s11t1-codex-session"
    return [
        _codex_line(
            "2026-10-12T00:00:00.000Z",
            0,
            "session_meta",
            '{"id":"' + session + '","session_id":"s11t1-parent-thread",'
            '"timestamp":"2026-10-12T00:00:00.000Z","cwd":"C:/synthetic/s11t1-codex",'
            '"originator":"MetaBrain-S11T1-Corpus","cli_version":"fixture-1.0",'
            '"source":"cli","git":{}}',
        ),
        _codex_line(
            "2026-10-12T00:00:01.000Z",
            1,
            "event_msg",
            '{"type":"task_started","turn_id":"turn-1","started_at":1,'
            '"model_context_window":1000,"collaboration_mode_kind":"default"}',
        ),
        _codex_line(
            "2026-10-12T00:00:02.000Z",
            2,
            "response_item",
            '{"type":"message","id":"msg-dev","role":"developer","content":'
            '[{"type":"input_text","text":"' + MARKER + ' developer instructions"}]}',
        ),
        _codex_line(
            "2026-10-12T00:00:03.000Z",
            3,
            "response_item",
            '{"type":"message","id":"msg-user-1","role":"user","content":['
            '{"type":"input_text","text":"Mã nguồn gốc phải giữ nguyên byte"},'
            '{"type":"input_text","text":"exact span offsets for every record"}]}',
        ),
        _codex_line(
            "2026-10-12T00:00:04.000Z",
            4,
            "response_item",
            '{"type":"function_call","id":"fc-1","call_id":"call-1","name":"shell",'
            '"status":"completed","arguments":"{\\"command\\":[\\"echo\\",\\"' + MARKER + '\\"]}"}',
        ),
        _codex_line(
            "2026-10-12T00:00:05.000Z",
            5,
            "response_item",
            '{"type":"function_call_output","id":"fco-1","call_id":"call-1",'
            '"name":"shell","output":"' + MARKER + ' tool output text"}',
        ),
        _codex_line(
            "2026-10-12T00:00:06.000Z",
            6,
            "response_item",
            '{"type":"reasoning","id":"rs-1","summary":[],"content":null,'
            '"encrypted_content":"opaque"}',
        ),
        _codex_line(
            "2026-10-12T00:00:07.000Z",
            7,
            "response_item",
            '{"type":"message","id":"msg-assistant-1","role":"assistant","content":'
            '[{"type":"output_text","text":"raw preservation is opt-in; checkpoint must not '
            'pass uncommitted bytes"}],"phase":"final_answer"}',
        ),
        _codex_line(
            "2026-10-12T00:00:08.000Z",
            8,
            "event_msg",
            '{"type":"item_completed","thread_id":"s11t1-parent-thread","turn_id":"turn-1",'
            '"item":{"type":"UserMessage","id":"mi-1","content":[{"type":"text",'
            '"text":"' + MARKER + ' echoed user"}]},"completed_at_ms":1000}',
        ),
        _codex_line(
            "2026-10-12T00:00:09.000Z",
            9,
            "turn_context",
            '{"turn_id":"turn-1","root_turn_id":"turn-1","cwd":"C:/synthetic/s11t1-codex",'
            '"workspace_roots":["C:/synthetic/s11t1-codex"],"model":"fixture-model"}',
        ),
        _codex_line(
            "2026-10-12T00:00:10.000Z",
            10,
            "world_state",
            '{"full":true,"state":{"agents_md":{"directory":"C:/synthetic/s11t1-codex",'
            '"text":"' + MARKER + ' agents"}}}',
        ),
        _codex_line(
            "2026-10-12T00:00:11.000Z",
            11,
            "token_usage_record",
            '{"thread_id":"s11t1-parent-thread","turn_id":"turn-1",'
            '"session_id":"' + session + '","usage":{"input_tokens":1,"output_tokens":2,'
            '"total_tokens":3}}',
        ),
        _codex_line(
            "2026-10-12T00:00:12.000Z",
            12,
            "event_msg",
            '{"type":"token_count","info":{"total_token_usage":{"input_tokens":1,'
            '"output_tokens":2,"total_tokens":3}}}',
        ),
        _codex_line(
            "2026-10-12T00:00:13.000Z",
            13,
            "event_msg",
            '{"type":"task_complete","turn_id":"turn-1","last_agent_message":"'
            + MARKER + ' final answer","started_at":1,"completed_at":2,"duration_ms":1000,'
            '"time_to_first_token_ms":10}',
        ),
    ]


def _codex_base() -> bytes:
    return b"".join(_codex_payload_lines())


def _omp_event_line(event_id: str, parent_id: str | None, timestamp: str, type_and_body: str) -> bytes:
    parent = "null" if parent_id is None else '"' + parent_id + '"'
    text = (
        '{"type":' + type_and_body + ',"id":"' + event_id + '","parentId":' + parent
        + ',"timestamp":"' + timestamp + '"}\n'
    )
    return text.encode("utf-8")


def _omp_payload_lines() -> list[bytes]:
    """The 14 supported OMP shapes as one complete id/parentId chain."""

    session = "s11t1-omp-session"
    return [
        (
            '{"type":"title","v":1,"title":"' + session + '","source":"auto",'
            '"updatedAt":"2026-10-12T00:00:00.000Z","pad":"                "}\n'
        ).encode("utf-8"),
        (
            '{"type":"session","version":3,"id":"' + session + '",'
            '"timestamp":"2026-10-12T00:00:00.500Z","cwd":"C:/synthetic/s11t1-omp",'
            '"title":"' + session + '","titleSource":"auto"}\n'
        ).encode("utf-8"),
        _omp_event_line(
            "e01", None, "2026-10-12T00:00:01.000Z",
            '"thinking_level_change","thinkingLevel":"high","configured":true',
        ),
        _omp_event_line(
            "e02", "e01", "2026-10-12T00:00:02.000Z",
            '"service_tier_change","serviceTier":null',
        ),
        _omp_event_line(
            "e03", "e02", "2026-10-12T00:00:03.000Z",
            '"message","message":{"role":"user","content":[{"type":"text",'
            '"text":"phần chưa hiểu vẫn đọc được"},{"type":"text",'
            '"text":"source_span_offsets must be byte offsets"}],"steering":true,'
            '"attribution":"user","timestamp":"2026-10-12T00:00:03.000Z"}',
        ),
        _omp_event_line(
            "e04", "e03", "2026-10-12T00:00:04.000Z",
            '"message","message":{"role":"assistant","content":[{"type":"thinking",'
            '"thinking":"' + MARKER + ' reasoning","summary":true},{"type":"toolCall",'
            '"id":"call-1","name":"bash","arguments":{"command":"echo ' + MARKER + '"},"intent":"run"}],'
            '"api":"responses","provider":"fixture-provider","model":"fixture-model","usage":{},'
            '"stopReason":"toolUse","timestamp":"2026-10-12T00:00:04.000Z",'
            '"credentialId":"cred-1","responseId":"resp-1","serviceTier":null,'
            '"providerPayload":{},"duration":1,"ttft":1,'
            '"completedAt":"2026-10-12T00:00:04.500Z","contextSnapshot":{}}',
        ),
        _omp_event_line(
            "e05", "e04", "2026-10-12T00:00:05.000Z",
            '"message","message":{"role":"toolResult","toolCallId":"call-1","toolName":"bash",'
            '"content":[{"type":"text","text":"' + MARKER + ' tool output"}],'
            '"details":{"exitCode":0},"isError":false,'
            '"timestamp":"2026-10-12T00:00:05.000Z"}',
        ),
        _omp_event_line(
            "e06", "e05", "2026-10-12T00:00:06.000Z",
            '"custom","customType":"tool_execution_start","data":{"toolCallId":"call-1",'
            '"toolName":"bash","startedAt":"2026-10-12T00:00:04.800Z",'
            '"args":{"command":"echo ' + MARKER + '"},"intent":"run"}',
        ),
        _omp_event_line(
            "e07", "e06", "2026-10-12T00:00:07.000Z",
            '"custom_message","customType":"skill-prompt","content":"' + MARKER + ' skill prompt",'
            '"display":true,"details":{"name":"fixture","path":"C:/synthetic/s11t1-omp/skill.md",'
            '"args":"{}","prompt":"' + MARKER + ' skill body","lineCount":3},"attribution":"user"',
        ),
        _omp_event_line(
            "e08", "e07", "2026-10-12T00:00:08.000Z",
            '"custom_message","customType":"async-result","content":"' + MARKER + ' async result",'
            '"display":false,"details":{"meta":{"source":"fixture"},"jobs":[{"id":"job-1"}]},'
            '"attribution":"agent"',
        ),
        _omp_event_line(
            "e09", "e08", "2026-10-12T00:00:09.000Z",
            '"credential_pin","provider":"fixture-provider","hash":"fixture-hash-value"',
        ),
        _omp_event_line(
            "e10", "e09", "2026-10-12T00:00:10.000Z",
            '"title_change","title":"' + session + ' renamed","source":"auto",'
            '"previousTitle":"' + session + '","trigger":"fixture"',
        ),
        _omp_event_line(
            "e11", "e10", "2026-10-12T00:00:11.000Z",
            '"custom","customType":"todo_hud_state","data":{"sourceEntryId":"e03",'
            '"fingerprint":"fixture-fingerprint","visibility":"visible"}',
        ),
        _omp_event_line(
            "e12", "e11", "2026-10-12T00:00:12.000Z",
            '"compaction","summary":"' + MARKER + ' summary","shortSummary":"'
            + MARKER + ' short","firstKeptEntryId":"e03","tokensBefore":100,'
            '"tokensAfter":40,"method":"fixture-remote","details":{"readFiles":[],'
            '"modifiedFiles":[]},"fromExtension":false,'
            '"preserveData":{"fixtureCompaction":{"version":1,'
            '"provider":"fixture-provider","replacementHistory":[],"usedTokens":40,'
            '"usage":{"inputTokens":10,"outputTokens":5,"totalTokens":15,'
            '"cachedInputTokens":0,"cacheWriteTokens":0,"reasoningTokens":0}}}}',
        ),
    ]


def _omp_base() -> bytes:
    return b"".join(_omp_payload_lines())


# --- Corpus cases ----------------------------------------------------------


def _span(start: int, length: int) -> tuple[int, int]:
    return (start, start + length)


def _cases() -> list[CorpusCase]:
    codex_base = _codex_base()
    codex_len = len(codex_base)
    omp_base = _omp_base()
    omp_len = len(omp_base)

    cases: list[CorpusCase] = []

    # --- Codex: fully supported baseline --------------------------------
    cases.append(
        CorpusCase(
            case_id="codex-v1-clean",
            path="codex/codex-v1-clean.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=codex_base,
            reference_only=Expectation(
                interpretation="fully_supported",
                consumed_offset=codex_len,
                event_count=14,
            ),
            preserved=Expectation(
                interpretation="fully_supported",
                consumed_offset=codex_len,
                event_count=14,
            ),
        )
    )

    # --- Codex: unknown envelope record ----------------------------------
    unknown_envelope = _codex_line(
        "2026-10-12T00:00:14.000Z", 14, "future_envelope_v2",
        '{"opaque":"synthetic-future-record"}',
    )
    cases.append(
        CorpusCase(
            case_id="codex-v1-unknown-envelope-type",
            path="codex/codex-v1-unknown-envelope-type.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=codex_base + unknown_envelope,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=codex_len,
                event_count=14,
                diagnostics=((codex_len, "codex_type_unsupported.future_envelope_v2"),),
                unaccounted_tail_bytes=len(unknown_envelope),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=codex_len + len(unknown_envelope),
                event_count=14,
                diagnostics=((codex_len, "codex_type_unsupported.future_envelope_v2"),),
                preserved_spans=(_span(codex_len, len(unknown_envelope)),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- Codex: unknown payload type -------------------------------------
    unknown_payload = _codex_line(
        "2026-10-12T00:00:14.000Z", 14, "response_item",
        '{"type":"mystery_payload_v2","call_id":"call-x","opaque":true}',
    )
    cases.append(
        CorpusCase(
            case_id="codex-v1-unknown-payload-type",
            path="codex/codex-v1-unknown-payload-type.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=codex_base + unknown_payload,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=codex_len,
                event_count=14,
                diagnostics=((codex_len, "codex_item_type_unsupported.mystery_payload_v2"),),
                unaccounted_tail_bytes=len(unknown_payload),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=codex_len + len(unknown_payload),
                event_count=14,
                diagnostics=((codex_len, "codex_item_type_unsupported.mystery_payload_v2"),),
                preserved_spans=(_span(codex_len, len(unknown_payload)),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- Codex: unparseable JSON -----------------------------------------
    invalid_json = b'{"timestamp":"2026-10-12T00:00:14.000Z","ordinal":14,"type":}\n'
    cases.append(
        CorpusCase(
            case_id="codex-v1-invalid-json",
            path="codex/codex-v1-invalid-json.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=codex_base + invalid_json,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=codex_len,
                event_count=14,
                diagnostics=((codex_len, "codex_record_json_invalid"),),
                unaccounted_tail_bytes=len(invalid_json),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=codex_len + len(invalid_json),
                event_count=14,
                diagnostics=((codex_len, "codex_record_json_invalid"),),
                preserved_spans=(_span(codex_len, len(invalid_json)),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- Codex: blank line is not a skip ---------------------------------
    cases.append(
        CorpusCase(
            case_id="codex-v1-blank-line",
            path="codex/codex-v1-blank-line.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=codex_base + b"\n",
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=codex_len,
                event_count=14,
                diagnostics=((codex_len, "codex_record_blank"),),
                unaccounted_tail_bytes=1,
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=codex_len + 1,
                event_count=14,
                diagnostics=((codex_len, "codex_record_blank"),),
                preserved_spans=(_span(codex_len, 1),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- Codex: incomplete trailing record -------------------------------
    partial_tail = _codex_line(
        "2026-10-12T00:00:14.000Z", 14, "response_item",
        '{"type":"message","id":"msg-partial","role":"user","content":'
        '[{"type":"input_text","text":"' + MARKER + ' partial without terminator"}]}',
    ).removesuffix(b"\n")
    cases.append(
        CorpusCase(
            case_id="codex-v1-partial-tail",
            path="codex/codex-v1-partial-tail.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=codex_base + partial_tail,
            reference_only=Expectation(
                interpretation="fully_supported",
                consumed_offset=codex_len,
                event_count=14,
                pending_tail_bytes=len(partial_tail),
                unaccounted_tail_bytes=len(partial_tail),
            ),
            preserved=Expectation(
                interpretation="fully_supported",
                consumed_offset=codex_len,
                event_count=14,
                pending_tail_bytes=len(partial_tail),
                unaccounted_tail_bytes=len(partial_tail),
            ),
        )
    )

    # --- Codex: CRLF framing ---------------------------------------------
    crlf_lines = [
        line.removesuffix(b"\n") + b"\r\n" for line in _codex_payload_lines()
    ]
    codex_crlf = b"".join(crlf_lines)
    cases.append(
        CorpusCase(
            case_id="codex-v1-crlf",
            path="codex/codex-v1-crlf.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=codex_crlf,
            reference_only=Expectation(
                interpretation="fully_supported",
                consumed_offset=len(codex_crlf),
                event_count=14,
            ),
            preserved=Expectation(
                interpretation="fully_supported",
                consumed_offset=len(codex_crlf),
                event_count=14,
            ),
        )
    )

    # --- Codex: invalid UTF-8 inside a complete record --------------------
    invalid_utf8 = (
        b'{"timestamp":"2026-10-12T00:00:14.000Z","ordinal":14,"type":"response_item",'
        b'"payload":{"type":"message","id":"msg-utf8","role":"user","content":'
        b'[{"type":"input_text","text":"caf\xe9 synthetic"}]}}\n'
    )
    cases.append(
        CorpusCase(
            case_id="codex-v1-invalid-utf8",
            path="codex/codex-v1-invalid-utf8.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=codex_base + invalid_utf8,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=codex_len,
                event_count=14,
                diagnostics=((codex_len, "source_record_encoding_invalid"),),
                unaccounted_tail_bytes=len(invalid_utf8),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=codex_len + len(invalid_utf8),
                event_count=14,
                diagnostics=((codex_len, "source_record_encoding_invalid"),),
                preserved_spans=(_span(codex_len, len(invalid_utf8)),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- Codex: UTF-8 BOM prefix -----------------------------------------
    bom = b"\xef\xbb\xbf"
    codex_bom = bom + codex_base
    first_line_end = codex_bom.index(b"\n") + 1
    cases.append(
        CorpusCase(
            case_id="codex-v1-bom-prefix",
            path="codex/codex-v1-bom-prefix.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=codex_bom,
            reference_only=Expectation(
                interpretation="uninterpreted",
                consumed_offset=0,
                event_count=0,
                diagnostics=((0, "source_record_encoding_invalid"),),
                unaccounted_tail_bytes=len(codex_bom),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=len(codex_bom),
                event_count=13,
                diagnostics=((0, "source_record_encoding_invalid"),),
                preserved_spans=(_span(0, first_line_end),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- Codex: multibyte UTF-8 must not shift byte offsets ---------------
    multibyte = _codex_line(
        "2026-10-12T00:00:14.000Z", 14, "response_item",
        '{"type":"message","id":"msg-vi","role":"user","content":[{"type":"input_text",'
        '"text":"Ghi chú tiếng Việt: bảo toàn offset theo byte, không theo ký tự"}]}',
    )
    unknown_after_multibyte = _codex_line(
        "2026-10-12T00:00:15.000Z", 15, "future_multibyte_envelope",
        '{"opaque":"synthetic"}',
    )
    multibyte_offset = codex_len + len(multibyte)
    cases.append(
        CorpusCase(
            case_id="codex-v1-multibyte-utf8",
            path="codex/codex-v1-multibyte-utf8.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=codex_base + multibyte + unknown_after_multibyte,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=multibyte_offset,
                event_count=15,
                diagnostics=((multibyte_offset, "codex_type_unsupported.future_multibyte_envelope"),),
                unaccounted_tail_bytes=len(unknown_after_multibyte),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=multibyte_offset + len(unknown_after_multibyte),
                event_count=15,
                diagnostics=((multibyte_offset, "codex_type_unsupported.future_multibyte_envelope"),),
                preserved_spans=(_span(multibyte_offset, len(unknown_after_multibyte)),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- OMP: fully supported baseline -----------------------------------
    cases.append(
        CorpusCase(
            case_id="omp-v3-clean",
            path="omp/omp-v3-clean.jsonl",
            source_format=OMP_FORMAT,
            declared_format_version=1,
            content=omp_base,
            reference_only=Expectation(
                interpretation="fully_supported",
                consumed_offset=omp_len,
                event_count=14,
            ),
            preserved=Expectation(
                interpretation="fully_supported",
                consumed_offset=omp_len,
                event_count=14,
            ),
        )
    )

    # --- OMP: declared newer session version is a barrier ----------------
    omp_newer = omp_base.replace(
        b'"type":"session","version":3,', b'"type":"session","version":4,', 1
    )
    session_record_start = omp_newer.index(b'{"type":"session"')
    cases.append(
        CorpusCase(
            case_id="omp-v3-session-version-4",
            path="omp/omp-v3-session-version-4.jsonl",
            source_format=OMP_FORMAT,
            declared_format_version=1,
            content=omp_newer,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=session_record_start,
                event_count=1,
                diagnostics=((session_record_start, "omp_session_version_unsupported.4"),),
                version_barrier=True,
                unaccounted_tail_bytes=len(omp_newer) - session_record_start,
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=session_record_start,
                event_count=1,
                diagnostics=((session_record_start, "omp_session_version_unsupported.4"),),
                preserved_spans=(_span(session_record_start, len(omp_newer) - session_record_start),),
                version_barrier=True,
            ),
        )
    )

    # --- OMP: unknown record type ----------------------------------------
    unknown_record = _omp_event_line(
        "e13", "e12", "2026-10-12T00:00:13.000Z",
        '"checkpoint_v2","opaque":{"synthetic":true}',
    )
    cases.append(
        CorpusCase(
            case_id="omp-v3-unknown-record",
            path="omp/omp-v3-unknown-record.jsonl",
            source_format=OMP_FORMAT,
            declared_format_version=1,
            content=omp_base + unknown_record,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=omp_len,
                event_count=14,
                diagnostics=((omp_len, "omp_type_unsupported.checkpoint_v2"),),
                unaccounted_tail_bytes=len(unknown_record),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=omp_len + len(unknown_record),
                event_count=14,
                diagnostics=((omp_len, "omp_type_unsupported.checkpoint_v2"),),
                preserved_spans=(_span(omp_len, len(unknown_record)),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- OMP: a later known record links to a preserved unknown node ------
    later_known = _omp_event_line(
        "e14", "e13", "2026-10-12T00:00:14.000Z",
        '"message","message":{"role":"user","content":[{"type":"text",'
        '"text":"' + MARKER + ' follow-up after preserved node"}],"steering":false,'
        '"attribution":"user","timestamp":"2026-10-12T00:00:14.000Z"}',
    )
    cases.append(
        CorpusCase(
            case_id="omp-v3-unknown-parent-link",
            path="omp/omp-v3-unknown-parent-link.jsonl",
            source_format=OMP_FORMAT,
            declared_format_version=1,
            content=omp_base + unknown_record + later_known,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=omp_len,
                event_count=14,
                diagnostics=((omp_len, "omp_type_unsupported.checkpoint_v2"),),
                unaccounted_tail_bytes=len(unknown_record) + len(later_known),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=omp_len + len(unknown_record) + len(later_known),
                event_count=15,
                diagnostics=((omp_len, "omp_type_unsupported.checkpoint_v2"),),
                preserved_spans=(_span(omp_len, len(unknown_record)),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- OMP: invalid envelope must not supply chain identity ------------
    invalid_envelope = (
        b'{"type":"message","id":"e13","parentId":"e12",'
        b'"timestamp":"2026-10-12T00:00:13.000Z","message":}\n'
    )
    invalid_parent_link_offset = omp_len + len(invalid_envelope)
    cases.append(
        CorpusCase(
            case_id="omp-v3-invalid-envelope-parent-link",
            path="omp/omp-v3-invalid-envelope-parent-link.jsonl",
            source_format=OMP_FORMAT,
            declared_format_version=1,
            content=omp_base + invalid_envelope + later_known,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=omp_len,
                event_count=14,
                diagnostics=((omp_len, "omp_record_json_invalid"),),
                unaccounted_tail_bytes=len(invalid_envelope) + len(later_known),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=omp_len + len(invalid_envelope) + len(later_known),
                event_count=14,
                diagnostics=(
                    (omp_len, "omp_record_json_invalid"),
                    (invalid_parent_link_offset, "omp_chain_reference_unresolved"),
                ),
                preserved_spans=(
                    _span(omp_len, len(invalid_envelope)),
                    _span(invalid_parent_link_offset, len(later_known)),
                ),
                advances_past_preserved=True,
            ),
        )
    )

    # --- OMP: unobserved message content part ----------------------------
    unknown_part = _omp_event_line(
        "e13", "e12", "2026-10-12T00:00:13.000Z",
        '"message","message":{"role":"user","content":[{"type":"audio",'
        '"text":"' + MARKER + ' synthetic audio part"}],"steering":false,'
        '"attribution":"user","timestamp":"2026-10-12T00:00:13.000Z"}',
    )
    cases.append(
        CorpusCase(
            case_id="omp-v3-unknown-message-part",
            path="omp/omp-v3-unknown-message-part.jsonl",
            source_format=OMP_FORMAT,
            declared_format_version=1,
            content=omp_base + unknown_part,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=omp_len,
                event_count=14,
                diagnostics=((omp_len, "omp_message_part_unsupported.audio"),),
                unaccounted_tail_bytes=len(unknown_part),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=omp_len + len(unknown_part),
                event_count=14,
                diagnostics=((omp_len, "omp_message_part_unsupported.audio"),),
                preserved_spans=(_span(omp_len, len(unknown_part)),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- OMP: dangling parent reference (open decision D7) ---------------
    omp_dangling = omp_base.replace(b'"parentId":"e02"', b'"parentId":"e99"', 1)
    dangling_marker = omp_dangling.index(b'"id":"e03"')
    dangling_record_start = omp_dangling.rfind(b"\n", 0, dangling_marker) + 1
    cases.append(
        CorpusCase(
            case_id="omp-v3-dangling-parent",
            path="omp/omp-v3-dangling-parent.jsonl",
            source_format=OMP_FORMAT,
            declared_format_version=1,
            content=omp_dangling,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=dangling_record_start,
                event_count=4,
                diagnostics=((dangling_record_start, "omp_chain_reference_unresolved"),),
                unaccounted_tail_bytes=len(omp_dangling) - dangling_record_start,
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=len(omp_dangling),
                event_count=13,
                diagnostics=((dangling_record_start, "omp_chain_reference_unresolved"),),
                preserved_spans=(
                    _span(dangling_record_start, omp_dangling.index(b"\n", dangling_record_start) + 1
                          - dangling_record_start),
                ),
                advances_past_preserved=True,
            ),
        )
    )

    # --- OMP: incomplete trailing record ---------------------------------
    omp_partial = _omp_event_line(
        "e13", "e12", "2026-10-12T00:00:13.000Z",
        '"message","message":{"role":"user","content":[{"type":"text",'
        '"text":"' + MARKER + ' partial without terminator"}],"steering":false,'
        '"attribution":"user","timestamp":"2026-10-12T00:00:13.000Z"}',
    ).removesuffix(b"\n")
    cases.append(
        CorpusCase(
            case_id="omp-v3-partial-tail",
            path="omp/omp-v3-partial-tail.jsonl",
            source_format=OMP_FORMAT,
            declared_format_version=1,
            content=omp_base + omp_partial,
            reference_only=Expectation(
                interpretation="fully_supported",
                consumed_offset=omp_len,
                event_count=14,
                pending_tail_bytes=len(omp_partial),
                unaccounted_tail_bytes=len(omp_partial),
            ),
            preserved=Expectation(
                interpretation="fully_supported",
                consumed_offset=omp_len,
                event_count=14,
                pending_tail_bytes=len(omp_partial),
                unaccounted_tail_bytes=len(omp_partial),
            ),
        )
    )

    # --- OMP: invalid UTF-8 in a complete record -------------------------
    omp_invalid_utf8 = (
        b'{"type":"message","id":"e13","parentId":"e12",'
        b'"timestamp":"2026-10-12T00:00:13.000Z","message":{"role":"user",'
        b'"content":[{"type":"text","text":"caf\xe9 synthetic"}],"steering":false,'
        b'"attribution":"user","timestamp":"2026-10-12T00:00:13.000Z"}}\n'
    )
    cases.append(
        CorpusCase(
            case_id="omp-v3-invalid-utf8",
            path="omp/omp-v3-invalid-utf8.jsonl",
            source_format=OMP_FORMAT,
            declared_format_version=1,
            content=omp_base + omp_invalid_utf8,
            reference_only=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=omp_len,
                event_count=14,
                diagnostics=((omp_len, "source_record_encoding_invalid"),),
                unaccounted_tail_bytes=len(omp_invalid_utf8),
            ),
            preserved=Expectation(
                interpretation="partially_interpreted",
                consumed_offset=omp_len + len(omp_invalid_utf8),
                event_count=14,
                diagnostics=((omp_len, "source_record_encoding_invalid"),),
                preserved_spans=(_span(omp_len, len(omp_invalid_utf8)),),
                advances_past_preserved=True,
            ),
        )
    )

    # --- Empty generation ------------------------------------------------
    cases.append(
        CorpusCase(
            case_id="empty-generation",
            path="empty/empty-generation.jsonl",
            source_format=CODEX_FORMAT,
            declared_format_version=1,
            content=b"",
            reference_only=Expectation(
                interpretation="empty",
                consumed_offset=0,
                event_count=0,
            ),
            preserved=Expectation(
                interpretation="empty",
                consumed_offset=0,
                event_count=0,
            ),
        )
    )

    return cases


# --- Query baseline --------------------------------------------------------


def _queries() -> list[Query]:
    return [
        Query(
            query_id="q01-vi-exact",
            text="Mã nguồn gốc phải giữ nguyên byte",
            kind="vietnamese",
            expected_sources=("codex-v1-clean", "codex-v1-multibyte-utf8"),
        ),
        Query(
            query_id="q02-en-exact",
            text="raw preservation is opt-in",
            kind="english",
            expected_sources=("codex-v1-clean",),
        ),
        Query(
            query_id="q03-symbol",
            text="source_span_offsets",
            kind="exact_symbol",
            expected_sources=("omp-v3-clean",),
        ),
        Query(
            query_id="q04-en-checkpoint",
            text="checkpoint must not pass uncommitted bytes",
            kind="english",
            expected_sources=("codex-v1-clean",),
        ),
        Query(
            query_id="q05-vi-partial",
            text="phần chưa hiểu vẫn đọc được",
            kind="vietnamese",
            expected_sources=("omp-v3-clean",),
        ),
        Query(
            query_id="q06-timestamp",
            text="2026-10-12T00:00:04.000Z",
            kind="timestamp",
            expected_sources=("codex-v1-clean", "omp-v3-clean"),
        ),
        Query(
            query_id="q07-negative",
            text="zzz-no-such-phrase-in-corpus-zzz",
            kind="english",
            expected_sources=(),
            expected_absent=True,
        ),
    ]


# --- Folder case -----------------------------------------------------------


def _folder_case() -> FolderCase:
    return FolderCase(
        root_relative_path="folders/tree",
        expected_included=(
            "folders/tree/notes/codex-a.jsonl",
            "folders/tree/notes/draft/omp-partial.jsonl",
            "folders/tree/notes/ignore-me.tmp",
            "folders/tree/notes/summary.md",
        ),
        expected_excluded=(
            FolderEntry("folders/tree/linked-outside", "excluded_reparse_point"),
            FolderEntry("folders/outside/outside-only.jsonl", "outside_enrollment_root"),
        ),
        link_relative_path="folders/tree/linked-outside",
        link_target_relative_path="folders/outside",
        outside_relative_path="folders/outside/outside-only.jsonl",
    )


# --- Generation ------------------------------------------------------------


def _sha256(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _expectation_payload(expectation: Expectation) -> dict[str, object]:
    return {
        "interpretation": expectation.interpretation,
        "consumed_offset": expectation.consumed_offset,
        "event_count": expectation.event_count,
        "diagnostics": [
            {"offset": offset, "code": code} for offset, code in expectation.diagnostics
        ],
        "preserved_spans": [
            {"offset": start, "length": end - start} for start, end in expectation.preserved_spans
        ],
        "advances_past_preserved": expectation.advances_past_preserved,
        "pending_tail_bytes": expectation.pending_tail_bytes,
        "unaccounted_tail_bytes": expectation.unaccounted_tail_bytes,
        "version_barrier": expectation.version_barrier,
    }


def _case_entry(case: CorpusCase) -> dict[str, object]:
    return {
        "case_id": case.case_id,
        "path": case.path,
        "format": case.source_format,
        "declared_format_version": case.declared_format_version,
        "bytes": len(case.content),
        "sha256": _sha256(case.content),
        "newline_count": case.content.count(b"\n"),
        "expect": {
            "reference_only": _expectation_payload(case.reference_only),
            "preserved": _expectation_payload(case.preserved),
        },
        "decision_pending": case.decision_pending,
    }


def _query_entry(query: Query) -> dict[str, object]:
    return {
        "query_id": query.query_id,
        "text": query.text,
        "kind": query.kind,
        "expected_sources": list(query.expected_sources),
        "expected_absent": query.expected_absent,
    }


def _manifest(cases: list[CorpusCase], folder: FolderCase, reparse_status: dict[str, str]) -> dict[str, object]:
    return {
        "corpus_schema_version": CORPUS_SCHEMA_VERSION,
        "generator": {
            "path": GENERATOR_PATH,
            "sha256": _sha256(Path(__file__).read_bytes()),
            "python": sys.version.split()[0],
        },
        "cases": [_case_entry(case) for case in cases],
        "queries": [_query_entry(query) for query in _queries()],
        "folder_case": {
            "root": folder.root_relative_path,
            "expected_included": list(folder.expected_included),
            "expected_excluded": [
                {"path": entry.relative_path, "reason": entry.reason}
                for entry in folder.expected_excluded
            ],
            "reparse_point": {
                "link": folder.link_relative_path,
                "target": folder.link_target_relative_path,
                **reparse_status,
            },
        },
        "totals": {
            "case_count": len(cases),
            "case_bytes": sum(len(case.content) for case in cases),
            "decision_pending_count": sum(1 for case in cases if case.decision_pending),
        },
    }


def _materialize_reparse_point(out: Path, folder: FolderCase) -> dict[str, str]:
    link = out / folder.link_relative_path
    target = out / folder.link_target_relative_path
    if os.name != "nt":
        return {"status": "platform_unsupported"}
    if link.is_junction():
        return {"status": "created"}
    if link.exists() or link.is_symlink():
        return {"status": "not_materialized", "reason": "unexpected existing entry"}
    result = subprocess.run(
        ["cmd", "/c", "mklink", "/J", str(link), str(target)],
        capture_output=True,
        text=True,
        check=False,
    )
    if result.returncode == 0 and link.is_junction():
        return {"status": "created"}
    detail = (result.stderr or result.stdout).strip().splitlines()
    return {
        "status": "not_materialized",
        "reason": detail[0] if detail else "mklink failed",
    }


def _remove_generated_tree(out: Path) -> None:
    """Remove a generated corpus without ever following a reparse point."""

    for current, directories, _files in os.walk(out):
        for name in list(directories):
            child = Path(current) / name
            if child.is_junction() or child.is_symlink():
                os.rmdir(child)
                directories.remove(name)
    shutil.rmtree(out)


def _write(out: Path, cases: list[CorpusCase], folder: FolderCase) -> dict[str, object]:
    if out.exists():
        if not (out / MARKER_NAME).is_file():
            raise SystemExit(
                f"refusing to replace {out}: it was not created by this generator "
                f"(missing {MARKER_NAME})"
            )
        _remove_generated_tree(out)
    out.mkdir(parents=True)
    (out / MARKER_NAME).write_text(
        "S11-T1 generated synthetic corpus; safe to delete and regenerate.\n",
        encoding="utf-8",
        newline="\n",
    )

    for case in cases:
        target = out / case.path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(case.content)

    codex_clean = next(case.content for case in cases if case.case_id == "codex-v1-clean")
    omp_partial = next(
        case.content for case in cases if case.case_id == "omp-v3-partial-tail"
    )
    notes = out / "folders/tree/notes"
    (notes / "draft").mkdir(parents=True, exist_ok=True)
    (notes / "codex-a.jsonl").write_bytes(codex_clean)
    (notes / "summary.md").write_text(
        "# S11-T1 synthetic folder note\n\n"
        "This markdown file exists only to exercise folder enumeration and "
        "original-format preservation. It carries no private content.\n",
        encoding="utf-8",
        newline="\n",
    )
    (notes / "ignore-me.tmp").write_text(
        "synthetic .tmp file; include/exclude rules belong to S11-T2\n",
        encoding="utf-8",
        newline="\n",
    )
    (notes / "draft" / "omp-partial.jsonl").write_bytes(omp_partial)
    outside = out / "folders/outside"
    outside.mkdir(parents=True, exist_ok=True)
    (outside / "outside-only.jsonl").write_text(
        '{"synthetic":"outside the enrolled folder root"}\n',
        encoding="utf-8",
        newline="\n",
    )

    reparse_status = _materialize_reparse_point(out, folder)
    manifest = _manifest(cases, folder, reparse_status)
    (out / MANIFEST_NAME).write_text(
        json.dumps(manifest, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
        newline="\n",
    )
    return manifest


# --- Validation ------------------------------------------------------------


def _coverage(file_bytes: int, expectation: Expectation) -> int:
    """Covered bytes = consumed prefix plus preserved spans (merged)."""

    intervals: list[tuple[int, int]] = []
    if expectation.consumed_offset > 0:
        intervals.append((0, expectation.consumed_offset))
    intervals.extend(expectation.preserved_spans)
    intervals.sort()
    covered = 0
    current_end = 0
    for start, end in intervals:
        if start > current_end:
            covered += end - start
        elif end > current_end:
            covered += end - current_end
        current_end = max(current_end, end)
    return covered


def _check_expectation(case: CorpusCase, label: str, expectation: Expectation) -> list[str]:
    problems: list[str] = []
    size = len(case.content)
    if expectation.interpretation not in _VALID_INTERPRETATIONS:
        problems.append(f"{case.case_id}/{label}: unknown interpretation {expectation.interpretation}")
    if not 0 <= expectation.consumed_offset <= size:
        problems.append(f"{case.case_id}/{label}: consumed offset outside the file")
    if expectation.event_count < 0:
        problems.append(f"{case.case_id}/{label}: negative event count")

    spans = expectation.preserved_spans
    last_end = -1
    for start, end in spans:
        if not (0 <= start < end <= size):
            problems.append(f"{case.case_id}/{label}: preserved span {start}-{end} outside the file")
        if start < last_end:
            problems.append(f"{case.case_id}/{label}: preserved spans overlap or are unsorted")
        last_end = max(last_end, end)
        if expectation.advances_past_preserved:
            if end > expectation.consumed_offset:
                problems.append(f"{case.case_id}/{label}: preserved span is not inside the consumed prefix")
        elif start < expectation.consumed_offset:
            problems.append(f"{case.case_id}/{label}: preserved span precedes an unadvanced checkpoint")

    if expectation.advances_past_preserved and not spans:
        problems.append(f"{case.case_id}/{label}: claims a preserved advance without a preserved span")
    if expectation.interpretation == "empty" and size != 0:
        problems.append(f"{case.case_id}/{label}: empty interpretation on a non-empty file")
    if expectation.version_barrier and expectation.advances_past_preserved and not spans:
        problems.append(f"{case.case_id}/{label}: a passing version barrier needs a preserved tail")
    if expectation.pending_tail_bytes > expectation.unaccounted_tail_bytes:
        problems.append(f"{case.case_id}/{label}: pending tail exceeds unaccounted tail")

    accounted = _coverage(size, expectation)
    unaccounted = size - accounted
    if unaccounted != expectation.unaccounted_tail_bytes:
        problems.append(
            f"{case.case_id}/{label}: unaccounted tail is {unaccounted}, "
            f"declared {expectation.unaccounted_tail_bytes}"
        )
    if label == "preserved" and unaccounted != expectation.pending_tail_bytes:
        problems.append(
            f"{case.case_id}/{label}: preservation policy leaves {unaccounted} unaccounted bytes "
            f"but {expectation.pending_tail_bytes} are a pending incomplete record"
        )
    if label == "reference_only":
        if spans:
            problems.append(f"{case.case_id}/{label}: reference-only branch must not declare preserved spans")
        if expectation.advances_past_preserved:
            problems.append(
                f"{case.case_id}/{label}: reference-only branch cannot advance past preserved bytes"
            )
        expected_unprocessed = size - expectation.consumed_offset
        if expectation.unaccounted_tail_bytes != expected_unprocessed:
            problems.append(
                f"{case.case_id}/{label}: reference-only unprocessed suffix must be "
                f"{expected_unprocessed}, declared {expectation.unaccounted_tail_bytes}"
            )
        if unaccounted != expected_unprocessed:
            problems.append(
                f"{case.case_id}/{label}: reference-only coverage leaves {unaccounted} unaccounted bytes, "
                f"but the unprocessed suffix is {expected_unprocessed}"
            )
    if expectation.interpretation == "fully_supported" and unaccounted != expectation.pending_tail_bytes:
        problems.append(
            f"{case.case_id}/{label}: fully supported generation leaves {unaccounted} unaccounted bytes "
            f"but only {expectation.pending_tail_bytes} are a pending incomplete record"
        )
    for offset, _code in expectation.diagnostics:
        if not 0 <= offset < max(size, 1):
            problems.append(f"{case.case_id}/{label}: diagnostic offset {offset} outside the file")

    return problems


def _check_folder(out: Path, folder: FolderCase) -> list[str]:
    problems: list[str] = []
    root = out / folder.root_relative_path
    if not root.is_dir():
        return [f"folder case root missing: {folder.root_relative_path}"]

    included: list[str] = []
    for current, directories, files in os.walk(root, followlinks=False):
        current_path = Path(current)
        for name in list(directories):
            child = current_path / name
            if child.is_junction() or child.is_symlink():
                directories.remove(name)
        for name in files:
            included.append((current_path / name).relative_to(out).as_posix())
    included.sort()
    if included != sorted(folder.expected_included):
        problems.append(
            "folder enumeration mismatch: expected "
            + str(sorted(folder.expected_included))
            + " actual "
            + str(included)
        )

    outside = out / folder.outside_relative_path
    if not outside.is_file():
        problems.append("outside folder file missing")
    if folder.outside_relative_path in included:
        problems.append("outside folder file was enumerated inside the enrollment root")

    link = out / folder.link_relative_path
    if os.name == "nt" and link.is_junction():
        resolved = link.resolve()
        expected_target = (out / folder.link_target_relative_path).resolve()
        if resolved != expected_target:
            problems.append("reparse point target does not match the manifest")
    return problems


def _check_queries(cases: list[CorpusCase]) -> list[str]:
    problems: list[str] = []
    contents = {case.case_id: case.content for case in cases}
    for query in _queries():
        needle = query.text.encode("utf-8")
        hits = {case_id: data for case_id, data in contents.items() if needle in data}
        for case_id in query.expected_sources:
            if case_id not in hits:
                problems.append(f"query {query.query_id}: text absent from {case_id}")
        if query.expected_absent and hits:
            problems.append(
                f"query {query.query_id}: expected absent but found in {sorted(hits)}"
            )
    return problems


def _host_reparse_state(out: Path, folder: FolderCase) -> str:
    if os.name != "nt":
        return "platform_unsupported"
    link = out / folder.link_relative_path
    return "created" if link.is_junction() else "not_materialized"


def _validate(out: Path, cases: list[CorpusCase], folder: FolderCase) -> list[str]:
    problems: list[str] = []
    if not (out / MARKER_NAME).is_file():
        return [f"{out} is not a generated corpus (missing {MARKER_NAME})"]
    if not (out / MANIFEST_NAME).is_file():
        return [f"{out} has no {MANIFEST_NAME}"]

    for case in cases:
        target = out / case.path
        if not target.is_file():
            problems.append(f"missing case file: {case.path}")
            continue
        data = target.read_bytes()
        if data != case.content:
            problems.append(f"{case.case_id}: bytes on disk differ from the generator")
        if _sha256(data) != _sha256(case.content):
            problems.append(f"{case.case_id}: digest differs")
        if data.count(b"\n") != case.content.count(b"\n"):
            problems.append(f"{case.case_id}: newline framing differs")
        problems.extend(_check_expectation(case, "reference_only", case.reference_only))
        problems.extend(_check_expectation(case, "preserved", case.preserved))

    problems.extend(_check_folder(out, folder))
    problems.extend(_check_queries(cases))

    try:
        manifest = json.loads((out / MANIFEST_NAME).read_text(encoding="utf-8"))
    except json.JSONDecodeError as error:
        return problems + [f"{MANIFEST_NAME} is not valid JSON: {error}"]

    expected_totals = {
        "case_count": len(cases),
        "case_bytes": sum(len(case.content) for case in cases),
        "decision_pending_count": sum(1 for case in cases if case.decision_pending),
    }
    if manifest.get("corpus_schema_version") != CORPUS_SCHEMA_VERSION:
        problems.append("manifest schema version mismatch")
    if manifest.get("totals") != expected_totals:
        problems.append(
            "manifest totals differ from the generator: "
            f"{manifest.get('totals')} != {expected_totals}"
        )

    generator = manifest.get("generator")
    generator_sha = _sha256(Path(__file__).read_bytes())
    if not isinstance(generator, dict):
        problems.append("manifest generator metadata missing")
    else:
        if generator.get("path") != GENERATOR_PATH:
            problems.append("manifest generator path mismatch")
        if generator.get("sha256") != generator_sha:
            problems.append("manifest generator digest does not match the current generator")

    expected_cases = {entry["path"]: entry for entry in (_case_entry(case) for case in cases)}
    disk_cases = manifest.get("cases")
    if not isinstance(disk_cases, list):
        problems.append("manifest cases missing")
    else:
        disk_by_path = {
            entry.get("path"): entry for entry in disk_cases if isinstance(entry, dict)
        }
        if len(disk_by_path) != len(disk_cases) or set(disk_by_path) != set(expected_cases):
            problems.append("manifest case path set mismatch")
        for path in sorted(set(disk_by_path) & set(expected_cases)):
            if disk_by_path[path] != expected_cases[path]:
                problems.append(
                    f"manifest case {path} does not match the generator expectations"
                )

    expected_queries = [_query_entry(query) for query in _queries()]
    if manifest.get("queries") != expected_queries:
        problems.append("manifest queries differ from the fixed query baseline")

    disk_folder = manifest.get("folder_case")
    expected_excluded = [
        {"path": entry.relative_path, "reason": entry.reason}
        for entry in folder.expected_excluded
    ]
    if not isinstance(disk_folder, dict):
        problems.append("manifest folder case missing")
    else:
        if disk_folder.get("root") != folder.root_relative_path:
            problems.append("manifest folder root mismatch")
        if disk_folder.get("expected_included") != list(folder.expected_included):
            problems.append("manifest folder include set mismatch")
        if disk_folder.get("expected_excluded") != expected_excluded:
            problems.append("manifest folder exclusion set mismatch")
        reparse = disk_folder.get("reparse_point")
        if not isinstance(reparse, dict):
            problems.append("manifest folder reparse metadata missing")
        else:
            if reparse.get("link") != folder.link_relative_path:
                problems.append("manifest reparse link mismatch")
            if reparse.get("target") != folder.link_target_relative_path:
                problems.append("manifest reparse target mismatch")
            observed_reparse = _host_reparse_state(out, folder)
            declared_reparse = reparse.get("status")
            if declared_reparse != observed_reparse:
                problems.append(
                    f"manifest reparse status is {declared_reparse}, "
                    f"observed {observed_reparse}"
                )
            if declared_reparse == "not_materialized" and not reparse.get("reason"):
                problems.append("manifest reparse status has no failure reason")
    return problems


# --- Entry point -----------------------------------------------------------


def _print_summary(cases: list[CorpusCase]) -> None:
    payload = {
        "cases": len(cases),
        "bytes": sum(len(case.content) for case in cases),
        "decision_pending": [case.case_id for case in cases if case.decision_pending],
        "corpus_digest": _sha256(
            b"".join(case.content for case in cases)
        ),
    }
    print(json.dumps(payload, indent=2))


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("generate", "check"))
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = parser.parse_args(argv)

    out: Path = args.out
    cases = _cases()
    folder = _folder_case()

    if args.command == "generate":
        manifest = _write(out, cases, folder)
        print(f"generated {len(cases)} cases under {out}")
        print(json.dumps(manifest["totals"], indent=2))
        _print_summary(cases)
        return 0

    problems = _validate(out, cases, folder)
    if problems:
        for problem in problems:
            print(f"FAIL {problem}")
        return 1
    print(f"PASS corpus check: {out}")
    _print_summary(cases)
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
