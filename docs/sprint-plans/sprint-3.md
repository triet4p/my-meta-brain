# Sprint 3 — Capture trung thực và session reference-only

## Sprint Goal

Tiếp nhận Codex/OMP và ghi chú research vào cùng core mà không sửa session nguồn, không mất attribution hoặc checkpoint, có policy bảo tồn nguồn rõ ràng.

## Status and Dependencies

- Status: Not started — CR01 updated. Entry: Sprint 2 encrypted core PASS; owner unlock, source allowlist và mẫu được duyệt. Locked không private ingestion/refresh.
- [Global plan](../PLAN.md), [specification](../META-BRAIN.md), [requirements](../REQUIREMENTS.md).
- Requirements: R16–R19; bảo toàn R04, R06, R10–R12, R33, R36.

## Downstream Contracts

Adapters normalized events/source refs, không publish/mint grant hoặc map quyền theo cwd. Original .codex/.omp giữ nguyên và ngoài cam kết bảo mật Meta Brain; các bản import/cache/chunk/checkpoint/locator/snapshot riêng trong vault encrypted. S6 dùng source spans/roles chung, S4/S5 source reader authorize riêng. Lock dừng private reads/refresh/writes, không plaintext queue để chạy nền.

## Atomic Tasks

Status legend: [ ] pending / [~] in progress / [x] done. Artifact cho S3-T<M>: artifacts/sprint-3/task-<M>.md.

- [ ] **S3-T1 — Source enrollment và incremental ingestion coordinator.** Requirements: R16, R17, R18.
    - Scope: allowlist/mapping, encrypted checkpoint/quarantine, idempotence/version dispatch và lock-aware coordinator; không parsing format ở đây.
    - Acceptance: không scan mọi profile; unknown/mixed source không mở transcript; restart/reimport không duplicate, replace/truncate không nối sai generation; unsupported báo rõ. Persisted metadata/events encrypted, locked dừng read/import và resume sau unlock không mất checkpoint.
    - Evidence: owner enroll/revoke một fixture source, incremental restart/replace/partial-read scenarios qua coordinator thật; original bytes và metadata không bị ghi lại.

- [ ] **S3-T2 — Adapter Codex JSONL read-only.** Requirements: R16, R17.
    - Scope: parse các schema Codex thực được phát hiện từ mẫu owner cho phép; không giả định mọi version cùng format.
    - Acceptance: giữ user/assistant/tool attribution, timestamps, session identity và source spans; append/partial tail/resume đúng; tool result không thành user statement; event không hiểu được báo/giữ trạng thái rõ, không silently drop.
    - Evidence: ingest ít nhất một session Codex thật được cho phép cùng fixtures boundary; so source trước/sau; truy xuất đúng đoạn chứng cứ. Báo schema/version đã hỗ trợ.

- [ ] **S3-T3 — Adapter OMP JSONL read-only.** Requirements: R16, R17.
    - Scope: parse schema OMP thực; dùng coordinator chung, không copy pipeline riêng.
    - Acceptance: attribution/context/session branch hoặc event structure nếu có được giữ; append/partial tail/reimport/restart không duplicate hay biến assistant thành owner; unknown schema không giả thành valid.
    - Evidence: ingest session OMP thật được phép và fixtures boundary; source bất biến; source spans mở lại đúng đoạn; báo schema/version đã hỗ trợ.

- [ ] **S3-T4 — Capture văn bản research nguyên trạng.** Requirements: R12, R19.
    - Scope: owner capture command/API text + zone và optional source metadata, dùng source registry; UI ở Sprint 5.
    - Acceptance: khi unlocked save encrypted source trước model job, không cần provider; unknown URL/tác giả/ngày giữ đúng; text còn sau restart/unlock. Locked reject save rõ ràng, UI không báo saved hoặc persist plaintext draft để né khóa.
    - Evidence: ghi chú Việt–Anh có claim, question và link; lưu/read/restart qua service; không tự tải URL hoặc nói đã đọc paper.

- [ ] **S3-T5 — Source availability tracking và selected preservation.** Requirements: R18, R29.
    - Scope: detect moved/missing/changed source và snapshot opt-in; không tự migrate nguồn.
    - Acceptance: reference-only; preservation owner scope/retention và encrypted snapshot, original không migrate/xóa. Source changed/missing đánh dấu đúng; snapshot/source API scope riêng; không theo path mới chưa xác minh; no raw content/key trong logs/temp.
    - Evidence: remove/move/change fixture, truy xuất reference và selected snapshot dưới quyền khác nhau; bảo toàn lineage và không báo bằng chứng cũ là current verified.

- [ ] **S3-T6 — Owner ingestion operations và safe background refresh.** Requirements: R16, R17, R18, R19, R36.
    - Scope: CLI/API status, pause/resume/re-enroll và incremental refresh cho allowlist; không extraction LLM.
    - Acceptance: user thấy nguồn/checkpoint/errors/cache scope; tắt nguồn hoặc lock ngừng đọc, refresh chỉ opt-in khi unlocked; unlock resume checkpoint không duplicates. Encrypted diagnostics không chứa transcript/secret thừa; không dùng autonomous unlock/key lưu plaintext.
    - Evidence: owner walkthrough cả hai runtime sources và research note, pause/append/resume/restart; không poll private filesystem ngoài allowlist; docs phản ánh cách dùng thật.

## Sprint Acceptance Gate

Nhập session Codex/OMP thật được phép và research khi unlocked; append/partial/restart/unlock/reimport không duplicate, lock giữa ingest không persist plaintext hoặc báo saved giả. Changed/missing/preservation giữ đúng, encrypted cached copies và nguồn bất biến. Agent API không source expansion từ item grant; không claim chặn agent tự đọc original với OS quyền sẵn có.

## Notes / Blockers

Cần owner allowlist, không coi nhu cầu sản phẩm là quyền đọc mọi file .codex/.omp. Format không hỗ trợ là lỗi có chẩn đoán, không “best effort success” mất events. Chính sách nguồn không rõ không tự đưa vào shared-engineering.

## Gate Record

Pending. Chưa có session riêng tư nào được nhập bởi tác vụ planning này.
