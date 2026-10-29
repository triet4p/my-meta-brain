# CR02 measurement protocol — synthetic corpus, query và environment baseline

- Ngày lập: 2026-10-29.
- Task: S11-T1; các attempt `S11-T1-20261029-A1`, `-A2`, `-A3`; proposal đi kèm [CR02-PROPOSAL.md](CR02-PROPOSAL.md).
- Trạng thái: **Phương pháp đã cố định cho Sprint 11; CR02 đã được owner duyệt (D1=B, D2–D8=A) và corpus schema 3 đã finalize; chưa phải kết quả đo sản phẩm.** Corpus và protocol là input cho T2–T5; không có ngưỡng hiệu năng mới, không thay R38/D086, không tạo PASS.
- Nguyên tắc: đo trên surface thật (CLI/service/UI/MCP tuỳ changed path) với dữ liệu synthetic; test suite không thay runtime smoke; không đọc private transcript; không dùng số đo suy ra chất lượng định tính R37.

## 1. Phạm vi

S11-T1 cố định bốn thứ để các task sau không trôi baseline:

1. Bộ case synthetic và kỳ vọng hợp đồng cho từng branch `reference_only` / `preserved`.
2. Bộ query cố định để đo retrieval khi surface tương ứng tồn tại.
3. Môi trường + cách ghi môi trường cho mỗi lần đo.
4. Định nghĩa metric và thủ tục đo theo từng task (T2–T5).

Không thuộc phạm vi T1: chạy sản phẩm, đo latency/RAM/throughput, đặt ngưỡng, đánh giá owner, migration, publication.

## 2. Môi trường baseline

Mỗi lần đo phải ghi lại các field sau trong artifact của task thực hiện:

| Field | Giá trị baseline quan sát 2026-10-29 |
| --- | --- |
| Repository / target ref | `F:/ai-ml/my-meta-brain`, `main` |
| Base commit (`git rev-parse HEAD`) | `d6abb44e72f9b352f49fcd3967135f8143e33f9e` |
| Worktree state | Có thay đổi docs chưa commit của planning/S11-T1; ghi lại `git status --porcelain` tại thời điểm đo |
| OS | Windows 11, build `10.0.26200.9711` (`Windows-11-10.0.26200-SP0`) |
| .NET SDK | `10.0.403` |
| Python | Hệ thống `3.13.17`; corpus tool chạy qua `uv run --script` dưới `3.14.0` (giá trị thật nằm trong `manifest.json` → `generator.python`) |
| uv | `0.13.5` |
| Corpus generator | `scripts/sprint-11/S11-T1-Corpus.py`; SHA-256 nằm trong `manifest.json` → `generator.sha256` |
| Corpus output | `artifacts/sprint-11/corpus` (Git-ignored) |
| Corpus schema | `corpus_schema_version = 3` |
| Locale/timezone khi đo | Ghi rõ; mọi timestamp trong corpus là UTC cố định, không phụ thuộc giờ máy |
| Vault/data root | Temp/owner-approved synthetic root do task đo tạo; ghi đường dẫn và cách xoá |

Nếu bất kỳ field nào khác baseline, ghi rõ delta và không so sánh trực tiếp với số cũ như cùng điều kiện.

## 3. Corpus synthetic

### 3.1 Sinh và kiểm tra

```powershell
uv run --script scripts/sprint-11/S11-T1-Corpus.py generate
uv run --script scripts/sprint-11/S11-T1-Corpus.py check
```

- `generate` chỉ thay thế thư mục do chính nó tạo (marker `CORPUS-MARKER`); nó bỏ qua reparse point khi dọn và không đi theo junction.
- Không chạy `generate` và `check` đồng thời trên cùng `--out`: `generate` thay thế cả cây nên `check` đọc giữa lúc ghi sẽ báo thiếu file/khớp sai. Chạy tuần tự.
- `check` sinh lại corpus trong bộ nhớ, so bytes/digest/newline từng file, đối chiếu **toàn bộ manifest** với giá trị generator (case entries + expectation hai branch, diagnostics, query baseline, folder include/exclude, generator path/SHA-256, totals, reparse truth của host), kiểm tra accounting offset/preserved-span mức case và enumeration folder. Nó **không** thay coverage verification bằng event span của sản phẩm.
- Corpus hiện tại: **20 case / 70.529 bytes**, schema `3`, digest nội dung `abfce3eb0d6eb4b26421849cfb9b4722ed3f0355eab76f46ce9a76c4b0592166`; `decision_pending_count = 0`. Schema 3 chỉ giải quyết metadata pending sau approval D5=A + D7=A; case bytes và digest không đổi.
- Corpus bytes nằm dưới `artifacts/` (Git-ignored); script định nghĩa corpus là file cần được đưa vào snapshot khi task được commit theo gate. Reviewer/fresh worker tái tạo bằng đúng hai lệnh trên.

### 3.2 Kỳ vọng theo case

Cột state/events/checkpoint là kỳ vọng **theo CR02 proposal**, không phải hành vi 1.0.

| Case | Format | Bytes | reference_only (state / events / checkpoint) | preserved (state / events / checkpoint) | Diagnostic |
| --- | --- | --- | --- | --- | --- |
| `codex-v1-clean` | `codex.session.jsonl` | 3344 | fully_supported / 14 / 3344 | fully_supported / 14 / 3344 | — |
| `codex-v1-unknown-envelope-type` | `codex.session.jsonl` | 3473 | partially_interpreted / 14 / 3344 | partially_interpreted / 14 / 3473 | codex_type_unsupported.future_envelope_v2@3344 |
| `codex-v1-unknown-payload-type` | `codex.session.jsonl` | 3494 | partially_interpreted / 14 / 3344 | partially_interpreted / 14 / 3494 | codex_item_type_unsupported.mystery_payload_v2@3344 |
| `codex-v1-invalid-json` | `codex.session.jsonl` | 3406 | partially_interpreted / 14 / 3344 | partially_interpreted / 14 / 3406 | codex_record_json_invalid@3344 |
| `codex-v1-blank-line` | `codex.session.jsonl` | 3345 | partially_interpreted / 14 / 3344 | partially_interpreted / 14 / 3345 | codex_record_blank@3344 |
| `codex-v1-partial-tail` | `codex.session.jsonl` | 3568 | fully_supported / 14 / 3344 | fully_supported / 14 / 3344 | — |
| `codex-v1-crlf` | `codex.session.jsonl` | 3358 | fully_supported / 14 / 3358 | fully_supported / 14 / 3358 | — |
| `codex-v1-invalid-utf8` | `codex.session.jsonl` | 3538 | partially_interpreted / 14 / 3344 | partially_interpreted / 14 / 3538 | source_record_encoding_invalid@3344 |
| `codex-v1-bom-prefix` | `codex.session.jsonl` | 3347 | uninterpreted / 0 / 0 | partially_interpreted / 13 / 3347 | source_record_encoding_invalid@0 |
| `codex-v1-multibyte-utf8` | `codex.session.jsonl` | 3719 | partially_interpreted / 15 / 3597 | partially_interpreted / 15 / 3719 | codex_type_unsupported.future_multibyte_envelope@3597 |
| `omp-v3-clean` | `omp.session.jsonl` | 3809 | fully_supported / 14 / 3809 | fully_supported / 14 / 3809 | — |
| `omp-v3-session-version-4` | `omp.session.jsonl` | 3809 | partially_interpreted / 1 / 131 | partially_interpreted / 1 / 131 | omp_session_version_unsupported.4@131 |
| `omp-v3-unknown-record` | `omp.session.jsonl` | 3929 | partially_interpreted / 14 / 3809 | partially_interpreted / 14 / 3929 | omp_type_unsupported.checkpoint_v2@3809 |
| `omp-v3-unknown-parent-link` (D5/D7=A) | `omp.session.jsonl` | 4202 | partially_interpreted / 14 / 3809 | partially_interpreted / 15 / 4202 | omp_type_unsupported.checkpoint_v2@3809 |
| `omp-v3-invalid-envelope-parent-link` (D5/D7=A) | `omp.session.jsonl` | 4179 | partially_interpreted / 14 / 3809 | partially_interpreted / 14 / 4179 | omp_record_json_invalid@3809; omp_chain_reference_unresolved@3906 |
| `omp-v3-unknown-message-part` | `omp.session.jsonl` | 4073 | partially_interpreted / 14 / 3809 | partially_interpreted / 14 / 4073 | omp_message_part_unsupported.audio@3809 |
| `omp-v3-dangling-parent` (D7=A) | `omp.session.jsonl` | 3809 | partially_interpreted / 4 / 563 | partially_interpreted / 13 / 3809 | omp_chain_reference_unresolved@563 |
| `omp-v3-partial-tail` | `omp.session.jsonl` | 4077 | fully_supported / 14 / 3809 | fully_supported / 14 / 3809 | — |
| `omp-v3-invalid-utf8` | `omp.session.jsonl` | 4050 | partially_interpreted / 14 / 3809 | partially_interpreted / 14 / 4050 | source_record_encoding_invalid@3809 |
| `empty-generation` | `codex.session.jsonl` | 0 | empty / 0 / 0 | empty / 0 / 0 | — |

Owner đã duyệt **D5=A + D7=A** ngày 2026-10-29, nên cả ba case từng mang `decision_pending` giờ là kỳ vọng **binding** cho T3:

- `omp-v3-dangling-parent` (D7): cross-record parent validation phải refusal `omp_chain_reference_unresolved`.
- `omp-v3-unknown-parent-link` (D5/D7): envelope validate độc lập nên `id`/`parentId` được admit dù `type` chưa biết; record sau resolve được.
- `omp-v3-invalid-envelope-parent-link` (D5/D7): envelope không validate được không cấp identity; record sau trỏ tới id đó phải refusal.

Task đo không được tự đổi các kỳ vọng này; nếu output khác, ghi lệch và mở finding theo change control.

### 3.3 Invariant corpus checker thực thi

- Bytes trên đĩa khớp generator và SHA-256 trong manifest.
- Manifest phải khớp generator: case path/ID/format/version/bytes/sha256/newline, cả hai expectation, diagnostics, query baseline, folder include/exclude, generator path + SHA-256 và totals. Reparse status phải khớp trạng thái host hiện tại (`created` / `not_materialized` / `platform_unsupported`); lý do `not_materialized` không rỗng.
- `0 <= checkpoint <= file size`; preserved span nằm trong file, không chồng lấn.
- **Accounting mức case, không phải event-span coverage:** checker coi `[0, consumed_offset)` là prefix đã xử lý, cộng preserved span, rồi so `unaccounted_tail_bytes` với kích thước file. `Expectation` chỉ mang `event_count`, không mang event span, nên checker **không** chứng minh từng event span phủ kín `[0, checkpoint)`. Điều kiện phủ kín prefix bằng event span thật là yêu cầu product-level của T3/T4 và phải được đo trên output thật; corpus không thay thế.
- `reference_only`: `[checkpoint, file size)` là **suffix chưa xử lý hợp lệ**; checker bắt buộc khai `unaccounted_tail_bytes = file size - checkpoint` và cấm khai preserved span/`advances_past_preserved`. Đây không phải vi phạm coverage.
- `preserved`: suffix chỉ được còn đúng `pending_tail_bytes` (tail dở dang); `unaccounted_tail_bytes == pending_tail_bytes`. Preserved span sau checkpoint khi barrier vẫn accounted nhưng checkpoint không tiến.
- `fully_supported` chỉ hợp lệ khi `unaccounted_tail_bytes == pending_tail_bytes`; mọi record hoàn chỉnh khác phải được diễn giải hoặc (ở branch preserved) có preserved span.
- Diagnostic offset nằm trong file; mỗi case chỉ khai báo diagnostic mà proposal dự kiến.
- Query baseline: text phải có trong các case được khai báo, query âm phải vắng.
- Folder: enumeration thực tế khớp danh sách include, file ngoài root không bị liệt kê, junction trỏ đúng target.

## 4. Query baseline

Bộ query cố định dùng cho các surface retrieval khi chúng tồn tại (S13+ cho workspace text, S15 cho hybrid search). Trong S11 **không** có surface nào phục vụ các query này vì nguồn còn reference-only và `derivation = not_indexed`; T1 không claim kết quả retrieval.

| Query | Text | Kind | Expected |
| --- | --- | --- | --- |
| `q01-vi-exact` | Mã nguồn gốc phải giữ nguyên byte | vietnamese | `codex-v1-clean`, `codex-v1-multibyte-utf8` |
| `q02-en-exact` | raw preservation is opt-in | english | `codex-v1-clean` |
| `q03-symbol` | source_span_offsets | exact_symbol | `omp-v3-clean` |
| `q04-en-checkpoint` | checkpoint must not pass uncommitted bytes | english | `codex-v1-clean` |
| `q05-vi-partial` | phần chưa hiểu vẫn đọc được | vietnamese | `omp-v3-clean` |
| `q06-timestamp` | 2026-10-12T00:00:04.000Z | timestamp | `codex-v1-clean`, `omp-v3-clean` |
| `q07-negative` | zzz-no-such-phrase-in-corpus-zzz | english | absent |

Quy ước: query không đổi trong Sprint 11; thay đổi query set cần owner approval vì đây là baseline đã cố định. `expected` là nguồn chứa evidence, không phải thứ hạng.

## 5. Folder case và reparse boundary

Corpus sinh cây sau dưới `folders/`:

```text
folders/tree/notes/codex-a.jsonl          (codex-v1-clean)
folders/tree/notes/summary.md             (markdown synthetic)
folders/tree/notes/ignore-me.tmp          (text synthetic)
folders/tree/notes/draft/omp-partial.jsonl (omp-v3-partial-tail)
folders/tree/linked-outside -> ../outside (junction; tạo bằng mklink /J)
folders/outside/outside-only.jsonl        (chỉ tới được qua junction)
```

Kỳ vọng khi enroll `folders/tree`:

- Include đúng 4 file thường ở trên.
- `folders/tree/linked-outside` bị loại với lý do `excluded_reparse_point`.
- `folders/outside/outside-only.jsonl` **không** xuất hiện trong enumeration và không trở thành nguồn đã enroll.
- Nếu host không tạo được junction, `manifest.json` ghi `reparse_point.status = not_materialized` + lý do; task đo phải ghi rõ negative này chưa chạy được và dùng đường khác (ví dụ junction do owner tạo) hoặc báo blocker, không giả PASS.

Lệnh tạo thủ công khi cần:

```powershell
cmd /c mklink /J artifacts\sprint-11\corpus\folders\tree\linked-outside artifacts\sprint-11\corpus\folders\outside
```

Xoá junction bằng `rmdir` trên chính link, không xoá target.

## 6. Thủ tục đo theo task

Mỗi task ghi scenario, command, output quan sát, negative và giới hạn vào `artifacts/sprint-11/task-<M>.md`.

### S11-T2 — folder enrollment

1. Preview folder synthetic: so enumeration với manifest; xác nhận preview không đọc nội dung file.
2. Enroll một lần sau confirm; kiểm tra membership = 4 file, không có link.
3. Negative: enroll lặp cùng folder (idempotent hoặc lỗi rõ), folder không tồn tại, file ngoài root, junction, vault locked, quyền đọc bị từ chối.
4. Kiểm tra không scan profile/home: chỉ root đã chọn được đọc.

### S11-T3 — adapter/partial parsing

1. Với từng case, ingest ở `reference_only`: so `interpretation`, `checkpoint`, `event_count`, `diagnostic code@offset` với bảng §3.2.
2. Bật preservation: ingest lại; so như trên; kiểm tra preserved bytes đọc lại **byte-identical** và digest khớp; kiểm tra checkpoint chỉ đi qua span đã preserve.
3. Negative bảo mật: lock giữa ingest, wrong key, tamper ciphertext, source bị truncate/replace, event attribution không bị đổi (tool output không thành user).
4. Negative D5: dùng `omp-v3-invalid-envelope-parent-link`; record có envelope không validate được (JSON/encoding/version barrier/thiếu field) không được cấp `id`/`parentId`, record sau trỏ tới id đó phải refusal `omp_chain_reference_unresolved`, không được chain-valid. Case `omp-v3-unknown-parent-link` là positive admission chỉ khi envelope validate độc lập; không lấy id từ tên file/folder/text/vị trí.
5. Regression: hai case clean phải cho cùng event/span/digest như 1.0 (so trên snapshot trước/sau; không replay toàn bộ Sprint 9/10).

### S11-T4 — incremental refresh và recovery

1. Append sau checkpoint: không trùng event, checkpoint tiến đúng.
2. Partial tail hoàn chỉnh sau append: tail được xử lý đúng một lần.
3. Truncation/replacement: quarantine đúng, không splice, preserved span của generation cũ không thành evidence mới.
4. Restart/cancel/lock giữa chừng: checkpoint không vượt dữ liệu chưa commit; preserved bytes đã báo saved không mất.
5. Chạy lại cùng input 2 lần: kết quả/offset/digest không đổi (idempotence).

### S11-T5 — owner surface

1. CLI và UI (khi có) cùng hiển thị bốn facet cho cùng một source state.
2. Per-file/offset remediation hiển thị đúng case lỗi; không echo transcript; mở rộng/xem raw chỉ sau unlock.
3. Lock/disconnect xoá private view.
4. MCP: xác nhận không có surface/grant mới cho agent.

## 7. Định nghĩa metric (phương pháp, không ngưỡng)

| Metric | Cách đo | Ghi chú |
| --- | --- | --- |
| Classification match | So từng field §3.2 với output thật của surface | Đếm case khớp/không; ghi case lệch |
| Byte coverage | Union `[0, checkpoint)` + preserved span; so với kích thước file | `reference_only`: `unaccounted = file size - checkpoint` là suffix chưa xử lý hợp lệ, phải khai `unprocessed_bytes`; `preserved`: `unaccounted = pending_tail_bytes` và không được có byte không giải thích khác |
| Digest equality | SHA-256 bytes đọc lại từ preserved span so với digest lưu | Phải bằng nhau tuyệt đối |
| Determinism | Chạy cùng scenario hai lần; so checkpoint/event set/digest | Không phụ thuộc thứ tự không xác định |
| Resume/idempotence | Restart rồi ingest lại; đếm event trùng/mất | Trùng = 0, mất = 0 |
| Preservation round-trip | Export/read bytes theo span và so byte-by-byte với nguồn | Chỉ khi owner bật preservation |
| Projection consistency | CLI so UI/MCP trên cùng state | Không surface nào tự tính lại policy |
| Negative behavior | Lock/wrong key/tamper/cross-scope/reparse | Fail closed, diagnostic rõ, không lộ dữ liệu |

Không đặt p95, RAM, throughput hay relevance threshold trong Sprint 11. R38 và D086 giữ nguyên; threshold cho S13/S15/S18 do các task đó chốt với owner.

## 8. Ghi evidence

- Artifact path: `artifacts/sprint-11/task-<M>.md`.
- Bắt buộc: environment §2, command đã chạy, output quan sát, case ID liên quan, negative, giới hạn, requirement/CR02 decision liên quan.
- Không dán nội dung private/real session; corpus synthetic có thể tham chiếu bằng case ID + digest.
- Kỳ vọng trong proposal không phải kết quả. Nếu output khác kỳ vọng, ghi lệch và mở finding; không sửa corpus cho khớp implementation mà không có change control.

## 9. Tái lập nhanh

```powershell
uv run --script scripts/sprint-11/S11-T1-Corpus.py generate
uv run --script scripts/sprint-11/S11-T1-Corpus.py check
python -c "import json,pathlib; m=json.loads(pathlib.Path('artifacts/sprint-11/corpus/manifest.json').read_text(encoding='utf-8')); print(m['totals'])"
```

Kỳ vọng quan sát 2026-10-29: `PASS corpus check`, `case_count = 20`, `case_bytes = 70529`, `decision_pending_count = 0`, digest `abfce3eb0d6eb4b26421849cfb9b4722ed3f0355eab76f46ce9a76c4b0592166`.

## 10. Change control cho baseline này

- Corpus schema version tăng khi thêm/bớt case hoặc đổi kỳ vọng; ghi lý do và không sửa case đã dùng để đo mà không nêu.
- Schema 2 (2026-10-29, attempt A2): thêm `omp-v3-invalid-envelope-parent-link`, đánh dấu `omp-v3-unknown-parent-link` là D5/D7, thắt invariant reference-only/fully-supported; không đổi expectation của case cũ.
- Schema 3 (2026-10-29, attempt A3): owner duyệt CR02 với D1=B, D2–D8=A nên toàn bộ `decision_pending` được giải quyết (D5=A + D7=A); schema number và metadata pending đổi, case bytes/digest/expectation không đổi.
- Query set và environment field chỉ đổi khi có owner approval hoặc task được giao rõ ràng.
- Không dùng việc "cập nhật corpus" để né một FAIL của T2–T5.
