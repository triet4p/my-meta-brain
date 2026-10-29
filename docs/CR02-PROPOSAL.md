# CR02 — Change proposal: folder enrollment, parser chịu lỗi và raw-preserved states

- Ngày lập: 2026-10-29.
- Task: S11-T1; các attempt `S11-T1-20261029-A1`, `-A2`, `-A3`; artifact evidence [artifacts/sprint-11/task-1.md](../artifacts/sprint-11/task-1.md).
- Trạng thái: **Approved contract — owner đã duyệt ngày 2026-10-29 với D1=B, D2–D8=A** (xem [bản ghi phê duyệt](#bản-ghi-phê-duyệt-owner-2026-10-29) và [D088](../.agents/memory/decisions.md)). Contract đã được đồng bộ vào [REQUIREMENTS](REQUIREMENTS.md), [META-BRAIN](META-BRAIN.md) §8.1 và [AGENTS](../AGENTS.md).
- Giới hạn trạng thái: approval **không** phải implementation hay product PASS. S11-T2–T5 chưa ship; binary 1.0.0 vẫn fail-closed. Không có commit, publication, provider access, host mutation hoặc migration dữ liệu 1.0 nào được hàm ý.
- Căn cứ yêu cầu: [roadmap §3 change-control gates](ROADMAP-2.0.md#3-requirement-candidates-và-traceability) ghi *"Parser/folder enrollment: giải quyết trước S11-T2"*; [direction §8](POST-1.0-DIRECTION.md#8-ingestion-chịu-được-thay-đổi-upstream) mô tả changed parser contract cần chốt; [direction §11](POST-1.0-DIRECTION.md#11-compatibility-migration-và-change-control) yêu cầu impact review và owner approval trước cutover.
- Đo lường đi kèm: [CR02 measurement protocol](CR02-MEASUREMENT-PROTOCOL.md) và corpus sinh bởi `scripts/sprint-11/S11-T1-Corpus.py`.

## 1. Vấn đề

Owner phản hồi sau 1.0.0 rằng nhập folder JSONL OMP gặp lỗi schema mismatch trong khi dữ liệu đã test vẫn dùng được, và định dạng upstream tiếp tục thay đổi. Đây là phản hồi sử dụng, không phải một lần tái hiện mới; proposal này không chạy lại flow 1.0 để "xác nhận lại lỗi".

Hợp đồng hiện hành xử lý đúng theo thiết kế fail-closed nhưng tạo ra hai hệ quả không còn phù hợp với hướng file-first:

1. Một record chưa hiểu chặn vĩnh viễn phần còn lại của file. Checkpoint dừng trước record đó và lần ingest sau dừng ở đúng offset cũ, nên dữ liệu phía sau không bao giờ được đọc dù vẫn còn trên đĩa.
2. Nguồn chỉ được enroll theo từng locator. Không có đơn vị folder, nên owner phải thao tác từng file — đúng vấn đề direction §1 nêu.

Đồng thời, các bảo vệ hiện có phải giữ nguyên: nguồn gốc read-only, attribution chỉ theo khai báo của nguồn, offset/span chính xác, encrypted at rest, lock dừng private ingestion, không scan profile.

## 2. Baseline hợp đồng hiện tại (đọc từ source, không chạy lại flow)

- `src/MetaBrain.Core/Domain/SourceIngestion.cs`: `SourceRecordDecodeKind` chỉ có `Consumed`, `NeedMoreBytes`, `Unsupported`. `SourceRecordContext` mang `(SourceId, Generation, RecordOffset)`; coordinator gán span, normalizer không được tự khai span. `SourceIngestionJournal` schema version 1 chứa `ConsumedOffset`, `Events[]`, `Diagnostics[]`; validator chỉ nhận đúng schema version 1, mỗi offset chỉ một diagnostic, và journal bị giới hạn bởi `MaximumJournalEvents = 4096`, `MaximumJournalBytes = 768 KiB`.
- `src/MetaBrain.Core/Domain/CodexSessionNormalizer.cs` và `OmpSessionNormalizer.cs`: mỗi record chưa quan sát trả `Unsupported(code)` với code dạng `codex_type_unsupported.<token>`, `codex_item_type_unsupported.<token>`, `omp_type_unsupported.<token>`, `omp_message_part_unsupported.<type>`, `omp_session_version_unsupported.<n>`, v.v. Docstring nêu rõ: *"the checkpoint never advances past it"*; không skip, không đoán shape.
- `src/MetaBrain.Application/SourceIngestionCoordinator.cs`: `Unsupported` dừng vòng decode, các event của prefix vẫn được ghi cùng checkpoint tại offset của record lỗi và một diagnostic tại đúng offset đó; `NeedMoreBytes` để tail chưa hoàn chỉnh ở lại unconsumed; truncation/replacement so với anchor digest bị quarantine (`generation_truncated` / `generation_replaced`); journal + checkpoint ghi trong một resource write.
- `src/MetaBrain.Connections/EnrolledSourceFileReader.cs`: chỉ đọc đúng locator đã enroll, so khớp final path của handle với locator, từ chối reparse point và directory, kiểm tra độ dài ổn định trong suốt lần đọc, không bao giờ liệt kê thư mục; trần `MaximumSourceBytes = 16 MiB`.
- Owner surface hiện có (xem [META-BRAIN §8](META-BRAIN.md#8-session-cũ-giữ-nguyên-tham-chiếu-và-chuẩn-hóa-ở-ngoài)): `source-list`, `source-status`, `source-pause`/`source-resume`, `source-refresh-optin`/`optout`, `source-refresh`, `source-snapshot-preserve` cùng retention; preservation là opt-in trên generation đã enroll, encrypted trong vault.

## 3. Thay đổi đề xuất

### 3.1 Trạng thái là bốn facet độc lập, không phải một enum

Direction §8 liệt kê *raw preserved, indexed text, partially interpreted, fully supported* và ghi tên cuối cùng sẽ thiết kế sau. Proposal tách chúng thành bốn facet để một giá trị không bao giờ ngầm khẳng định giá trị khác:

| Facet | Giá trị đề xuất | Ý nghĩa |
| --- | --- | --- |
| `availability` | `available`, `missing`, `changed`, `locked`, `unreadable`, `empty` | Quan sát được trên generation/locator hiện tại; `empty` tách khỏi `unreadable` để file 0 byte không bị báo như lỗi đọc |
| `preservation` | `reference_only`, `preserved(retention)` | `preserved` = bytes gốc của generation/record nằm encrypted trong vault theo retention owner chọn; mặc định `reference_only` |
| `interpretation` | `fully_supported`, `partially_interpreted`, `uninterpreted`, `empty` | Mức hiểu **đã đạt** trên generation, không phải mức sẽ đạt |
| `derivation` | `not_indexed`, `indexed_text` | Có text dẫn xuất phục vụ retrieval hay không; S11 **không** thêm source text indexing, nên giá trị này vẫn `not_indexed` trong 1.1.0 |

Quy tắc trình bày: `preserved` không hàm ý `partially_interpreted`; `partially_interpreted` không hàm ý `preserved`; `fully_supported` chỉ dùng khi mọi record hoàn chỉnh của generation đã được diễn giải và không còn byte chưa xử lý ngoài tail dở dang. Mọi surface (CLI/UI/MCP status) phải hiển thị facet nào đang đúng, không gộp thành một nhãn "ok/error".

### 3.2 Đơn vị lỗi và luật continuation

Đơn vị lỗi là **một record hoàn chỉnh theo framing của format** (với JSONL: một dòng kết thúc bằng `\n`). Không có "file lỗi toàn bộ" ngoại trừ các barrier ở §3.4.

Luật đề xuất:

1. Record được diễn giải → event như hiện tại, span/digest do coordinator gán.
2. Record không diễn giải được nhưng framing xác định → **chỉ được vượt qua khi bytes chính xác của record đó đã được lưu bền vững, encrypted, đọc lại được** trong cùng giao dịch với journal/checkpoint. Khi đó record trở thành preserved-uninterpreted, checkpoint được phép đi qua, và `interpretation` của generation là `partially_interpreted`. Việc preserve/đi qua **không** cấp identity hay attribution cho record; identity chỉ được admit theo ranh giới D5 ở §3.4.
3. Record không diễn giải được và bytes chưa được bảo toàn → dừng đúng như hiện tại: checkpoint đứng trước record, diagnostic tại offset đó, phần sau được báo rõ là chưa xử lý.
4. Không có chế độ "skip bytes rồi tiếp tục" khi chưa preserve. Đây là điều phân biệt tolerant parsing với silent drop.
5. Tail dở dang (`NeedMoreBytes`) không bao giờ được preserve như một record và không bao giờ đẩy checkpoint, kể cả khi owner bật preservation; nó chỉ được ghi nhận là `pending_tail_bytes`.

Preservation là **opt-in theo nguồn/folder** (xem D2). Không có preservation ngầm, không preserve toàn bộ profile, không đổi mặc định reference-only của R18.

### 3.3 Bất biến offset, attribution và coverage

Các bất biến sau là điều kiện nghiệm thu của T3/T4, không phải chi tiết implementation:

- **Offset tuyệt đối theo byte** trong generation đã enroll, không theo ký tự, không theo số dòng. Corpus có case `codex-v1-multibyte-utf8` để chứng minh điều này.
- **Span do coordinator gán**: `[start, end)` với `0 <= start < end <= anchorLength`; normalizer không được tự khai span. Preserved span dùng cùng quy tắc và cùng digest trên đúng bytes.
- **Coverage đã xử lý và suffix chưa xử lý**: với generation kích thước `N`, checkpoint `C`, tập event span đã nhận `E` và preserved span `P`:
  - **Invariant chung (mọi branch)**: `[0, C)` phải được phủ kín bởi các span rời nhau trong `E ∪ P`, không lỗ hổng, không span nào vượt quá `C`. Đây là điều kiện T3 kiểm tra trên sản phẩm; corpus chỉ lưu count nên không thay thế được.
  - **`reference_only`**: không thêm preserved span. Nếu record hoàn chỉnh tại `C` không diễn giải được, checkpoint đứng yên; `[C, N)` là **suffix chưa xử lý hợp lệ**, phải báo `unprocessed_bytes = N - C` (manifest/corpus ghi `unaccounted_tail_bytes = N - C`). `N - C > 0` **không** phải vi phạm coverage và không được coi là mất dữ liệu.
  - **`preserved`**: mọi record hoàn chỉnh chưa diễn giải trước `C` phải có preserved span đọc lại được; `[C, N)` chỉ được còn tail dở dang: `unaccounted_tail_bytes = pending_tail_bytes`. Preserved branch không được để suffix record hoàn chỉnh chưa xử lý.
  - **Barrier (`version_barrier`)**: preserved span có thể nằm trong `[C, N)`; bytes vẫn accounted (đọc lại được) nhưng checkpoint không tiến. Status phải phân biệt `unprocessed_bytes` (chưa preserve) với `preserved_pending_bytes` (đã preserve nhưng checkpoint chưa qua barrier).
  - **Công thức checker**: `accounted = |[0,C) ∪ P|`, `unaccounted = N - accounted`; với `reference_only` (P rỗng) `unaccounted = N - C`; với `preserved` `unaccounted = pending_tail_bytes`. Checker chỉ kiểm tra count/spans; T3 phải kiểm tra `[0,C)` bằng event span thật.
- **Attribution chỉ từ khai báo của nguồn**: record chưa hiểu không có author; không suy `User` từ tên file, folder, cwd, role đoán được hay nội dung text. Bytes preserved-uninterpreted không được trả như memory evidence hay citation; chỉ owner đọc/export nguyên bytes theo span sau unlock.
- **Diagnostic không chứa nội dung**: chỉ code + offset (+ thời điểm UTC). Không echo transcript, không ghi payload. Mỗi record lỗi có tối đa một diagnostic tại offset của nó; journal v2 phải cho phép diagnostic và preserved span cùng tồn tại ở một offset (validator hiện tại chỉ cho một diagnostic mỗi offset, nên cần mở rộng có kiểm soát).

### 3.4 Unknown shape, version và encoding

| Điều kiện | Phát hiện | `reference_only` | `preserved` + continuation | Checkpoint |
| --- | --- | --- | --- | --- |
| Record type chưa quan sát trong envelope đã validate | normalizer trả `Unsupported` | dừng, diagnostic tại offset | preserve record, tiếp tục (id chỉ admitted theo ranh giới D5 bên dưới) | đi qua preserved span |
| Payload/message part chưa quan sát | normalizer trả `Unsupported` | dừng | preserve, tiếp tục | đi qua |
| JSON không hợp lệ trong một dòng hoàn chỉnh | codec | dừng, `*_record_json_invalid` | preserve, tiếp tục | đi qua |
| Dòng trống | codec | dừng, `*_record_blank` | preserve 1 byte, tiếp tục | đi qua |
| UTF-8 không hợp lệ / BOM ở đầu record | coordinator validate UTF-8 trước khi gọi normalizer | dừng, `source_record_encoding_invalid` | preserve đúng bytes, tiếp tục | đi qua |
| Record khai báo version mới của format/root (vd `session.version > 3`) | normalizer trả `*_version_unsupported.<n>` | dừng tại record (barrier) | preserve toàn bộ tail, **không** diễn giải tiếp | **không** đi qua barrier |
| Tail dở dang | `NeedMoreBytes` | không đẩy checkpoint | không đẩy checkpoint | đứng yên |
| File 0 byte | reader | `availability = empty`, không diagnostic | như trái | 0 |

Lý do barrier cho record khai báo version: version đó có thể đổi nghĩa toàn bộ phần sau; diễn giải tiếp là đoán. Preserve tail vẫn cho owner đọc lại bytes và cho phép app version sau resume từ đúng barrier.

Encoding: không transcode lossy, không thay byte lỗi bằng `U+FFFD` rồi coi là đã đọc. BOM được xử lý bởi cùng luật (framing xác định → preservable; không có "BOM tolerance" ngầm).

**Ranh giới admission cho chain identity (D5).** Preserved node chỉ được ghi vào identity set để record sau resolve `parentId` khi **envelope của record đã được validate độc lập** theo đúng grammar envelope của format/version đang hỗ trợ (với OMP: JSON object, `type` là string không rỗng, `id` là token hợp lệ, `parentId` là `null` hoặc token hợp lệ, timestamp envelope hợp lệ). Đây là grammar envelope phổ quát của format/version (mọi event record OMP khai báo `id`/`parentId`), độc lập với `type`/payload đã biết hay chưa; không phải suy đoán shape. Khi envelope không validate được (JSON lỗi, encoding lỗi, version barrier, thiếu/sai `type`/`id`/`parentId`/timestamp), metadata `id`/`parentId` **không được tin**: chỉ bytes của record được preserve, và record sau trỏ tới id đó là refusal `omp_chain_reference_unresolved`, không được coi là hợp lệ. Không suy identity/author từ tên file, folder, cwd, vị trí, text hay nội dung. Refusal này chỉ có hiệu lực khi cross-record chain validation được bật (D7=A); nếu owner chọn D7=B thì không có identity set cross-record, D5 không tạo refusal và giới hạn per-record phải được ghi rõ — các corpus case phụ thuộc mang `decision_pending`.

### 3.5 Raw preservation: consent, phạm vi, retention

- Preservation là hành động owner, không phải hệ quả của ingest. Owner bật theo nguồn hoặc theo folder; mặc định tắt.
- Bytes preserve nằm encrypted trong vault theo đúng cơ chế bảo vệ hiện có (không crypto mới, không key store thứ hai); không plaintext scratch, không log, không temporary file.
- Preserved span gắn `(source, generation, offset, length, digest)`; đọc lại phải trả đúng bytes gốc và verify digest. Đây là điều kiện "không mất khả năng đọc lại nguyên bản" của direction §8.
- Preserved bytes là dữ liệu private bổ sung: phải nằm trong phạm vi purge/backup/export/retention. Không tự mở rộng quyền agent: không grant/scope mới, không đưa bytes preserved vào MCP hay catalog.
- Nếu owner không bật preservation, trạng thái phải nói rõ khả năng đọc lại phụ thuộc nguồn (source-dependent availability), không hứa điều không có.

### 3.6 Checkpoint, resume, crash và thay đổi nguồn

- Journal + checkpoint vẫn là một resource write nguyên tử. Checkpoint chỉ tiến khi mọi byte trong prefix mới hoặc là bytes của event đã nhận, hoặc là preserved span đọc lại được.
- Retry không tạo event trùng: event identity giữ nguyên quy tắc hiện tại (source + ordinal/offset hoặc declared node id); preserved span idempotent theo `(offset, length, digest)`.
- Crash trước commit → giữ revision cũ, không checkpoint nửa vời; ingest lại từ checkpoint cũ.
- Append bình thường: checkpoint cũ vẫn là anchor hợp lệ (prefix digest so khớp).
- Truncation/replacement: giữ nguyên hành vi quarantine hiện tại; **không** splice generation mới vào checkpoint cũ; preserved span của generation cũ không được trình bày như evidence của generation mới.
- Pause/lock: dừng đọc private ngay như hiện tại; preservation cũng dừng theo, không có ghi private khi locked.

### 3.7 Folder enrollment và ranh giới symlink/reparse

Đây là phần mới (T2) nhưng contract phải chốt trước implementation:

- Đơn vị enroll là root do owner chọn tường minh, kèm include/exclude tường minh và bước preview trước khi xác nhận một lần. Không mặc định scan home/profile hay toàn ổ đĩa.
- Enumeration **không đi theo** reparse point (symlink, junction, mount point). Entry như vậy bị loại với lý do `excluded_reparse_point` trong preview/status, không được đưa vào allowlist, không được coi là "file mới trong folder".
- Không admit path ngoài root; target của link không tự trở thành nguồn đã enroll. Đây là chống "nới allowlist qua link" mà T2 acceptance yêu cầu.
- Mỗi file được admit vẫn phải qua đúng reader hiện tại: final-path equality với locator, từ chối reparse/directory, share mode đọc không cho writer, kiểm tra độ dài ổn định. Đây là defense in depth, không thay thế kiểm tra lúc enumeration.
- TOCTOU giữa enumeration và read không được tuyên bố là loại trừ tuyệt đối: final-path check thu hẹp cửa sổ và fail closed khi path/handle lệch; giới hạn này phải ghi trong evidence, không hứa nhiều hơn.
- Path comparison trên Windows là case-insensitive; canonical root được ghi lại; không thay ACL/permission hay cấu hình host.
- Folder membership thay đổi (file mới/xóa) là chủ đề refresh của T4 và sharing scope của S17; proposal này chỉ chốt rằng link không tạo quyền và không tạo membership ngầm.

### 3.8 Trạng thái owner nhìn thấy và remediation

- `source-status`/`source-list` (và surface folder ở T5) phải trả các facet ở §3.1 cùng số liệu: `pending_tail_bytes`, `preserved_bytes`, `uninterpreted_records`, `last_diagnostic {code, offset}`.
- Remediation theo offset: owner thấy chính xác record nào chưa hiểu, có thể (a) bật preservation để đọc tiếp, (b) giữ nguyên trạng thái chờ adapter mới, (c) export bytes preserved.
- Không echo transcript trong diagnostic/status/log. Tên file/folder trong owner UI đang unlock là chủ đề disclosure envelope của S12; proposal này không quyết định nó và không thêm plaintext persistence mới.

## 4. Alternatives

| Phương án | Nội dung | Đánh giá |
| --- | --- | --- |
| A — Giữ fail-closed hiện tại | Không đổi gì; owner chờ adapter mới | Không đáp ứng direction §8 và acceptance T1; vẫn chặn vĩnh viễn phần sau của file |
| B — Tolerant parsing không preservation | Bỏ qua record lạ rồi tiếp tục | **Loại**: silent drop, vi phạm R17/R18 và "không dùng tolerant parsing để giả record chưa hiểu là hợp lệ" |
| C — Record-level, continuation có preservation (đề xuất) | Như §3.2–§3.6 | Đáp ứng outcome; chi phí: thêm dữ liệu private, journal v2, trạng thái phức tạp hơn |
| D — Đoán shape/version | Map record lạ vào shape gần nhất | **Loại**: sai attribution/semantics, vi phạm R12/R17 |

Khuyến nghị: **C**, với barrier cho version/root như §3.4.

## 5. Đối chiếu R01–R40

Ký hiệu: `—` không đổi; `R` làm rõ cách đọc requirement, không đổi outcome; `C` thay đổi contract/acceptance cần owner duyệt.

| R | Tác động | Ghi chú |
| --- | --- | --- |
| R01 | — | Không đổi automation opt-in |
| R02 | — | Owner vẫn giữ key/authority |
| R03 | — | Token/session không liên quan |
| R04 | R | Lock vẫn dừng private ingestion; preserved bytes là ciphertext dưới cùng key/lease |
| R05 | — | Không có authority mới cho agent |
| R06 | R | Preserved bytes không vào catalog/agent read; `derivation` giữ `not_indexed` trong 1.1 |
| R07 | — | Không có provider call |
| R08 | — | Không đổi cross-zone derivation |
| R09 | R | Bytes chưa hiểu không bao giờ là instruction; không được thực thi |
| R10 | — | Item envelope không đổi; source event vẫn là derived record |
| R11 | — | Folder/link không cấp quyền, không đổi collection semantics |
| R12 | R | Nhấn mạnh: author chỉ từ khai báo; uninterpreted → unknown, không thành user statement |
| R13 | — | Không đổi tầng/provenance của item |
| R14 | — | Không đổi relation |
| R15 | — | Không đổi explicit linking |
| R16 | C | Read-only/allowlist giữ nguyên; preservation tạo bản sao encrypted **chỉ khi owner opt-in**, purge/retention phải bao phủ; không sửa/rename/move source |
| R17 | C | Chính là thay đổi chính: unsupported record không còn chặn vĩnh viễn khi bytes được bảo toàn; append/partial/restart/replacement vẫn không trùng/mất im lặng; checkpoint/attribution chính xác |
| R18 | C | Reference-only vẫn mặc định; preservation mở rộng sang record chưa hiểu, vẫn encrypted, lock dừng, availability nói thật |
| R19 | — | Research capture không đổi |
| R20 | — | Không đổi profile semantics |
| R21 | — | Không đổi synthesis |
| R22 | — | Không đổi job/model authority |
| R23 | R | Không đổi retrieval/quality; uninterpreted bytes không thành text index |
| R24 | — | MCP interface không đổi; không surface mới cho agent |
| R25 | — | Brief không đổi |
| R26 | — | Không đổi temporal semantics |
| R27 | — | Không đổi intention |
| R28 | — | Không đổi forgetting |
| R29 | C | Preview/purge phải bao gồm preserved bytes và snapshot liên quan; session nguồn vẫn chỉ xóa khi có quyền riêng |
| R30 | R | Owner surface hiển thị facet/offset remediation (T5); không UI giả |
| R31 | — | Sharing UI không đổi |
| R32 | R | Một domain/policy; adapter vẫn không ghi vault; coordinator giữ quyền gán span |
| R33 | R | Journal vẫn là derived state encrypted; canonical là truth duy nhất |
| R34 | C | Preserved bytes là dữ liệu private không tái tạo từ nguồn sau khi nguồn đổi → phải vào backup/restore/export scope của S12/S8 contract |
| R35 | — | Install/upgrade không đổi; upgrade 1.0 → 1.1 giữ dữ liệu |
| R36 | R | Preserved bytes encrypted, không log/plaintext; diagnostic không chứa body |
| R37 | — | Qualitative acceptance không đổi |
| R38 | R | Chỉ cố định corpus/phương pháp; **không** thêm ngưỡng hiệu năng mới |
| R39 | C | Checkpoint/crash/restart với preserved span phải không mất dữ liệu đã báo saved; truncation/replacement vẫn quarantine |
| R40 | R | Proposal không được tính là PASS; evidence phải nêu rõ kỳ vọng chưa quan sát |

Không có requirement nào bị xóa hay hạ mức. R16–R19, R29, R34, R36, R39 là các mục cần ghi thành CR02 record sau khi owner duyệt.

## 6. Downstream impact

- **S11-T2** folder enrollment: hiện thực boundary §3.7, preview/enroll một lần, reuse reader; acceptance bổ sung negative reparse/outside/duplicate.
- **S11-T3** adapters: coordinator thêm UTF-8 validation, phân loại `recoverable` vs `version_barrier`, registry node preserved cho OMP chain; normalizer vẫn không gán span, không ghi vault.
- **S11-T4** refresh: atomic journal+checkpoint với preserved span, idempotence, quarantine, partial tail; negative crash/cancel.
- **S11-T5** UI/CLI: facet + per-file/offset remediation; lock/disconnect clear view; không echo transcript.
- **S11-T6** release 1.1.0: docs folder/raw-first, troubleshooting partial schema, upgrade 1.0; release contract chung.
- **S12** workspace: preserved bytes là encrypted private data phải vào backup/export/purge/retention và disclosure envelope của tên file; stable identity/rename/move không được làm mất preserved span.
- **S13–S18**: `derivation = indexed_text` chỉ xuất hiện khi có text extraction/indexing được duyệt; uninterpreted bytes không bao giờ thành search text.
- **MCP/agent surfaces**: không thay đổi; không grant/scope mới; preserved bytes không đi qua agent channel.

### Compatibility và migration

- Thay đổi **additive** trong 1.1.0: vault/canonical format không đổi; không migrate dữ liệu 1.0; không reset.
- Với nguồn fully supported, tập event/span/digest phải **giống hệt** hành vi 1.0 — đây là regression invariant của T3.
- Journal: reader 1.1 đọc được v1 và v2; schema lạ (v3+) fail closed, không đoán/rebuild. **v2 chỉ được ghi khi journal đang ghi thực sự chứa ít nhất một phần tử chỉ có ở v2** — preserved span hoặc record status v2-only. Việc owner bật preservation không tự nó ép mọi source sang v2; source reference-only (không có preserved span/status v2-only) tiếp tục ghi v1 đúng như 1.0.
- Khi owner opt-in preservation và coordinator thực sự ghi preserved span đầu tiên cho một generation, lần ghi nguyên tử đó nâng journal của source lên v2; không có migration riêng, không rewrite journal cũ.
- Upgrade 1.0 → 1.1 không rewrite journal sẵn có và không migrate vault. Source đã có journal v1 tiếp tục đọc/ghi v1 nếu không có v2-only state; event/span/digest của source fully supported giữ nguyên hành vi 1.0.
- Sau khi một source đã ghi v2, 1.1 không tự hạ xuống v1 (kể cả khi owner tắt preservation); binary 1.0 gặp journal v2 fail closed ở validator, không silent rebuild/partial read. Đây là phạm vi downgrade duy nhất; release notes 1.1.0 phải nêu.
- "Additive" nghĩa là field API/CLI và compatibility của source chưa preserve; **không** có nghĩa mọi journal chuyển sang v2 và không cho phép hạ cấp journal đã v2.
- CLI field mới là additive; field cũ giữ nguyên.
- Không đổi default/API theo cách phá vỡ 1.x; mọi breaking default để G2.

## 7. Đồng bộ tài liệu sau khi owner duyệt (đã thực hiện phần contract, 2026-10-29)

Owner approval thật đã được ghi; contract đã đồng bộ. Trạng thái từng mục:

1. `.agents/memory/decisions.md`: **đã append [D088](../.agents/memory/decisions.md)** (ID kế tiếp chưa dùng sau D087), nêu alternatives, evidence và giới hạn.
2. `docs/REQUIREMENTS.md`: **đã thêm "Approved Change Record — CR02 (2026-10-29)"** và cập nhật cột evidence/điều kiện của R16–R19, R29, R34, R36, R39, R40; giữ nguyên ID, không xóa/hạ mức requirement.
3. `docs/META-BRAIN.md`: **đã thêm §8.1** với contract adapter/ingestion, bốn facet trạng thái, continuation/preservation, barrier, journal compatibility và folder/reparse boundary.
4. `AGENTS.md`: **đã thêm invariant** parser/folder CR02, ghi rõ implementation S11-T2–T5 chưa ship.
5. `docs/PLAN.md` + `docs/sprint-plans/sprint-11.md`: **chưa cập nhật — Main-owned**. Main cần sync trạng thái T1/acceptance theo contract đã duyệt; proposal không sửa plan.
6. `docs/CR02-PROPOSAL.md`: **đã đổi trạng thái thành approved**, link D088 và các contract đích; `docs/POST-1.0-DIRECTION.md` không đổi (là direction, không phải contract).
7. `docs/OPERATIONS.md`: troubleshooting partial/offset thuộc T5/T6, chưa thực hiện; `CHANGELOG.md`: không thêm entry — chưa có implementation/release, chỉ contract approval.

## 8. Gói quyết định cho owner

Mỗi mục nêu lựa chọn và khuyến nghị. Owner có thể duyệt nguyên gói hoặc sửa từng mục.

**Đã chốt ngày 2026-10-29:** owner chọn **D1=B; D2=A; D3=A; D4=A; D5=A; D6=A; D7=A; D8=A**. Bảng lựa chọn/khuyến nghị dưới đây được giữ nguyên như lịch sử của gói đề xuất; cột khuyến nghị không phải kết quả cuối và không thay bản ghi phê duyệt ở cuối §8.

| ID | Quyết định | Lựa chọn | Khuyến nghị |
| --- | --- | --- | --- |
| D1 | Luật continuation | A: giữ fail-closed; B: chỉ đi qua record khi bytes đã preserve bền vững; C: skip bytes | **B** |
| D2 | Consent preservation | A: reference-only mặc định, opt-in theo nguồn/folder + retention; B: preserve mặc định | **A** |
| D3 | Version/root barrier | A: barrier, không diễn giải tiếp dù có preserve; B: tiếp tục sau version record | **A** |
| D4 | Encoding | A: không lossy transcode; record lỗi encoding chỉ recoverable khi preserve; B: thay `U+FFFD` rồi đọc tiếp | **A** |
| D5 | Chain khi continuation | A: chỉ admit `id`/`parentId` khi envelope được validator độc lập của format/version chấp nhận (kể cả `type` chưa biết); envelope lỗi/barrier không cấp identity; record sau trỏ tới id không admit là refusal (dependency D7=A); B: refuse mọi tham chiếu tới node preserved; C: tin `id`/`parentId` đã parse kể cả từ envelope lạ | **A** |
| D6 | Folder/reparse | A: không follow reparse, loại kèm lý do, final-path check từng file; B: follow trong root; C: follow và auto-enroll target | **A** |
| D7 | Dangling parent (mở) | A: thêm kiểm tra cross-record, diagnostic `omp_chain_reference_unresolved`; B: giữ validate per-record và ghi rõ giới hạn | **A** (nếu owner muốn giữ scope nhỏ: B, corpus case tương ứng được đánh dấu `decision_pending`) |
| D8 | Journal/compat | A: schema v2 chỉ ghi khi journal đang ghi có ít nhất một v2-only element (preserved span/status); source reference-only giữ v1; đọc v1+v2; không auto-downgrade; downgrade chỉ ảnh hưởng source đã ghi v2; B: giữ v1 + store phụ; C: rewrite v1 | **A** |

### Mẫu câu phê duyệt

> "Tôi duyệt CR02 theo D1=B, D2=A, D3=A, D4=A, D5=A, D6=A, D7=&lt;A hoặc B&gt;, D8=A (hoặc liệt kê sửa đổi). Việc duyệt cho phép cập nhật requirements/spec/decision và triển khai S11-T2–T5 theo contract này. Không bao gồm authorization cho push/tag/publication, provider/network access, host/ACL/registry mutation hoặc migration dữ liệu 1.0."

### Non-goals

- Không chọn crypto/envelope/package/thư viện, không chọn UI framework.
- Không đặt ngưỡng hiệu năng/quality mới (R38/D086 giữ nguyên).
- Không thay đổi security model CR01, không nới quyền agent, không thêm egress.
- Không migrate vault/session gốc, không sửa dữ liệu private, không host mutation.
- Không tuyên bố T1 PASS chỉ vì proposal đã viết xong.

### Rủi ro còn lại (phải ghi trong evidence, không che)

- Preserved bytes làm tăng dữ liệu private và bề mặt purge/backup; cần S12 xác nhận retention/backup scope.
- Owner có thể hiểu nhầm `partially_interpreted` là "đã đủ"; status phải tách facet và T5 phải chứng minh trên UI thật.
- Adapter mới có thể hiểu khác đi record đã preserve; preserved bytes chỉ đảm bảo đọc lại, không đảm bảo diễn giải lại nếu không rebuild.
- Cross-record chain validation (D7) thêm chi phí/độ phức tạp; nếu không làm, giới hạn phải ghi rõ thay vì ngầm định.
- TOCTOU giữa enumeration và read được thu hẹp bởi final-path check, không bị loại trừ tuyệt đối.
- Same-OS compromise vẫn ngoài threat model như CR01.

### Bản ghi phê duyệt owner (2026-10-29)

- **Người duyệt:** owner. **Cách ghi nhận:** lựa chọn “Duyệt gói đề xuất” trong `ask cr02approval`; đây là approval thật, không suy từ việc bắt đầu Sprint 11.
- **Kết quả:** D1=B; D2=A; D3=A; D4=A; D5=A; D6=A; D7=A; D8=A.
- **Phạm vi được duyệt:** cập nhật requirements/spec/decision và triển khai S11-T2–T5 theo contract này cùng các gate full-route hiện hành.
- **Không bao gồm:** push/tag/publication, provider/network access, host/ACL/registry mutation, migration dữ liệu 1.0, hoặc bất kỳ thay đổi security model CR01 nào.
- **Đã đồng bộ:** [D088](../.agents/memory/decisions.md), [REQUIREMENTS change record](REQUIREMENTS.md), [META-BRAIN §8.1](META-BRAIN.md), [AGENTS invariant](../AGENTS.md). `docs/PLAN.md` và sprint plan status do Main cập nhật; proposal này không sửa plan.
- **Chưa xảy ra:** implementation T2–T5, runtime/CLI/UI/MCP evidence, commit hay checkpoint. Approval không phải product PASS và không làm 1.0.0 đổi hành vi.

## 9. Liên kết corpus và kiểm chứng

- Corpus: 20 case, 70.529 bytes, schema `3`, digest `abfce3eb0d6eb4b26421849cfb9b4722ed3f0355eab76f46ce9a76c4b0592166`, sinh bởi `scripts/sprint-11/S11-T1-Corpus.py`; mỗi case có kỳ vọng riêng cho `reference_only` và `preserved`. Case bytes và digest không đổi qua bước finalize; chỉ metadata `decision_pending` được giải quyết.
- Sau approval D5=A + D7=A, toàn bộ `decision_pending` đã được giải quyết: `omp-v3-dangling-parent`, `omp-v3-unknown-parent-link` và `omp-v3-invalid-envelope-parent-link` giờ là kỳ vọng binding cho T3, không còn là case chờ quyết định.
- Phương pháp đo, môi trường, query baseline và thủ tục theo từng task nằm trong [CR02 measurement protocol](CR02-MEASUREMENT-PROTOCOL.md).
- Đây là **kỳ vọng hợp đồng**, chưa phải hành vi sản phẩm: T1 không chạy sản phẩm và không claim runtime PASS.

## 10. Tham chiếu

- [ROADMAP-2.0 §3](ROADMAP-2.0.md#3-requirement-candidates-và-traceability), [§5](ROADMAP-2.0.md#5-verification-và-performance-contract), [§6](ROADMAP-2.0.md#6-workflow-thực-thi)
- [POST-1.0-DIRECTION §8](POST-1.0-DIRECTION.md#8-ingestion-chịu-được-thay-đổi-upstream), [§11](POST-1.0-DIRECTION.md#11-compatibility-migration-và-change-control)
- [REQUIREMENTS](REQUIREMENTS.md#requirement-registry) — R16–R19, R29, R34, R36, R39, R40 và Change Control
- [META-BRAIN §8](META-BRAIN.md#8-session-cũ-giữ-nguyên-tham-chiếu-và-chuẩn-hóa-ở-ngoài)
- [Sprint 11](sprint-plans/sprint-11.md) — T1 acceptance
- Decision liên quan: D031, D039, D040 (source registry/availability/preservation/refresh)
