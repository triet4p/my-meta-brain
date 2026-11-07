#!/usr/bin/env python
# /// script
# requires-python = ">=3.13,<3.14"
# dependencies = []
# ///

from __future__ import annotations

import argparse
import hashlib
import json
import platform
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

MODEL_REVISION = "614241f622f53c4eeff9890bdc4f31cfecc418b3"
TOKENIZER_SHA256 = "0b44a9d7b51c3c62626640cda0e2c2f70fdacdc25bbbd68038369d14ebdf4c39"
CORPUS_SHA256 = "4ec9b83ef3b0483c70cdaa51be2cedd611d31430643f8e0b97aacfb8908fa806"
QUERIES_SHA256 = "261c017433882d54aece88f5740007ce026e9320d26ad96dbceeb30cb4a5f9be"
PROTOCOL_SHA256 = "dcfd438916bf2365c79430ace8ff5483143a4d9b474ee80fbe21db066da89493"
MAXIMUM_SEQUENCE_LENGTH = 512
BOS_ID = 0
EOS_ID = 2
PAD_ID = 1


def sha256_bytes(value: bytes) -> str:
    return hashlib.sha256(value).hexdigest()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def load_frozen_inputs(repository: Path) -> tuple[list[tuple[str, str]], dict[str, str]]:
    fixture_root = repository / "tests" / "MetaBrain.S1T4.Smoke" / "Fixtures" / "S15T1" / "lexical-v2"
    corpus_path = fixture_root / "corpus.json"
    queries_path = fixture_root / "queries.json"
    protocol_path = fixture_root / "protocol.md"
    fixture_hashes = {
        "corpus": sha256_file(corpus_path),
        "queries": sha256_file(queries_path),
        "protocol": sha256_file(protocol_path),
    }
    if fixture_hashes != {
        "corpus": CORPUS_SHA256,
        "queries": QUERIES_SHA256,
        "protocol": PROTOCOL_SHA256,
    }:
        raise ValueError(f"Frozen fixture identity changed: {fixture_hashes}")

    corpus = json.loads(corpus_path.read_text(encoding="utf-8"))
    queries = json.loads(queries_path.read_text(encoding="utf-8"))
    if corpus["datasetId"] != "s15-t1-synthetic-search-v2" or queries["datasetId"] != corpus["datasetId"]:
        raise ValueError("Frozen lexical-v2 dataset identity changed")
    if not corpus["syntheticOnly"] or len(queries["queries"]) != 35 or len(corpus["files"]) != 12:
        raise ValueError("Frozen lexical-v2 input counts or synthetic-only declaration changed")
    if corpus["distractorFileCount"] != 100 or corpus["distractorBytesPerFile"] != 1024:
        raise ValueError("Frozen lexical-v2 distractor protocol changed")

    inputs: list[tuple[str, str]] = []
    for query in queries["queries"]:
        inputs.append((f"query/{query['id']}", "query: " + query["query"]))

    passages: list[tuple[str, str]] = []
    for file in corpus["files"]:
        content = file.get("revision2Content") or file["revision1Content"]
        path = file["folder"] + "/" + file["name"]
        passages.append((path, content))
    for index in range(1, corpus["distractorFileCount"] + 1):
        token = f"qzxvnoise{index:03d} ".encode("ascii")
        repeated = (token * ((corpus["distractorBytesPerFile"] + len(token) - 1) // len(token)))[: corpus["distractorBytesPerFile"]]
        passages.append((f"{corpus['allowedFolderName']}/distractor-{index:03d}.txt", repeated.decode("ascii")))
    passages.sort(key=lambda item: item[0])
    for path, content in passages:
        inputs.append((f"passage/{path}", "passage: " + content))

    inputs.extend(
        [
            ("edge/whitespace", " \t  repeated\u00a0whitespace\r\n line  "),
            ("edge/unicode", "T\u00f4i mu\u1ed1n t\u00ecm l\u1ea1i th\u00f4ng tin \u2014 caf\u00e9 e\u0301; \u6771\u4EAC \U0001F600"),
            ("edge/special", "<s> <pad> </s> <unk> <mask>"),
        ]
    )
    for count in (509, 510, 511):
        inputs.append((f"edge/truncate-{count}-tokens", " ".join(["a"] * count)))

    case_ids = [case_id for case_id, _ in inputs]
    if len(inputs) != 153 or len(set(case_ids)) != len(case_ids):
        raise ValueError(f"Expected 153 unique oracle inputs, found {len(inputs)}")
    return inputs, fixture_hashes


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Generate pinned Hugging Face token IDs for the frozen S15-T1 synthetic fixture.")
    parser.add_argument("repository", type=Path)
    parser.add_argument("model_directory", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("task_id")
    parser.add_argument("attempt_id")
    parser.add_argument("agent_id")
    parser.add_argument("oracle_site", type=Path)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    tokenizer_path = args.model_directory / "onnx" / "tokenizer.json"
    if sha256_file(tokenizer_path) != TOKENIZER_SHA256:
        raise ValueError("Pinned tokenizer.json SHA-256 mismatch")
    inputs, fixture_hashes = load_frozen_inputs(args.repository)
    sys.path.insert(0, str(args.oracle_site.resolve()))
    from tokenizers import Tokenizer

    if Tokenizer.__module__.split(".")[0] != "tokenizers":
        raise ValueError("Imported Tokenizer is not from the approved Hugging Face tokenizers package")
    tokenizer = Tokenizer.from_file(str(tokenizer_path))
    raw_cases: list[dict[str, Any]] = []
    tokenization_started = time.perf_counter()
    for case_id, text in inputs:
        encoding = tokenizer.encode(text, add_special_tokens=True)
        raw_ids = [int(token_id) for token_id in encoding.ids]
        if len(raw_ids) < 2 or raw_ids[0] != BOS_ID or raw_ids[-1] != EOS_ID:
            raise ValueError(f"HF tokenizer.json special-token template mismatch in {case_id}")
        model_ids = raw_ids[:MAXIMUM_SEQUENCE_LENGTH]
        if len(raw_ids) > MAXIMUM_SEQUENCE_LENGTH:
            model_ids[-1] = EOS_ID
        raw_cases.append(
            {
                "caseId": case_id,
                "inputSha256": sha256_bytes(text.encode("utf-8")),
                "rawIds": raw_ids,
                "modelIds": model_ids,
                "rawTokenCount": len(raw_ids),
                "truncatedTokenCount": len(raw_ids) - len(model_ids),
            }
        )
    tokenization_milliseconds = (time.perf_counter() - tokenization_started) * 1000.0

    args.output.parent.mkdir(parents=True, exist_ok=True)
    payload: dict[str, Any] = {
        "schema": "s15-t1-hf-token-ids-v1",
        "taskId": args.task_id,
        "attemptId": args.attempt_id,
        "agentId": args.agent_id,
        "generatedUtc": datetime.now(timezone.utc).isoformat(),
        "modelRevision": MODEL_REVISION,
        "tokenizer": {
            "library": "Hugging Face tokenizers",
            "version": "0.22.1",
            "runtime": platform.python_version(),
            "tokenizerJsonSha256": TOKENIZER_SHA256,
            "tokenizerJsonPath": "onnx/tokenizer.json",
            "loadApi": "tokenizers.Tokenizer.from_file(local pinned tokenizer.json)",
            "encodeApi": "Tokenizer.encode(text, add_special_tokens=True)",
            "specialTokenSemantics": "tokenizer.json TemplateProcessing adds BOS <s>=0 and EOS </s>=2; literal special-token strings follow the pinned tokenizer.json pipeline",
            "prefixSemantics": "frozen query inputs prepend exactly 'query: '; current passage inputs prepend exactly 'passage: '; boundary inputs have no added prefix",
            "truncationSemantics": "right-truncate the full special-token-inclusive ID list to 512; when truncated replace the final retained ID with EOS=2",
            "paddingSemantics": f"oracle arrays are unpadded; ONNX evaluator uses tokenizer.json pad ID {PAD_ID} and attention-mask value 0 only for batch/static padding",
            "maximumSequenceLength": MAXIMUM_SEQUENCE_LENGTH,
            "bosTokenId": BOS_ID,
            "eosTokenId": EOS_ID,
            "padTokenId": PAD_ID,
        },
        "inputs": {
            "datasetId": "s15-t1-synthetic-search-v2",
            "corpusManifestSha256": fixture_hashes["corpus"],
            "queriesManifestSha256": fixture_hashes["queries"],
            "protocolSha256": fixture_hashes["protocol"],
            "queryCount": 35,
            "currentPassageCount": 112,
            "boundaryCount": 6,
            "caseCount": len(raw_cases),
        },
        "timing": {
            "tokenizationInputCount": len(inputs),
            "tokenizationMilliseconds": tokenization_milliseconds,
        },
        "cases": raw_cases,
    }
    write_started = time.perf_counter()
    args.output.write_text(json.dumps(payload, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    write_milliseconds = (time.perf_counter() - write_started) * 1000.0
    print(
        json.dumps(
            {
                "schema": payload["schema"],
                "caseCount": len(raw_cases),
                "queryCount": 35,
                "currentPassageCount": 112,
                "boundaryCount": 6,
                "tokenizerVersion": "0.22.1",
                "tokenizerJsonSha256": TOKENIZER_SHA256,
                "tokenizationMilliseconds": tokenization_milliseconds,
                "writeMilliseconds": write_milliseconds,
                "outputBytes": args.output.stat().st_size,
                "outputSha256": sha256_file(args.output),
            },
            sort_keys=True,
        )
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
