# Sprint 3 — Capture trung thực và session reference-only

## Sprint Goal

Tiếp nhận Codex/OMP và ghi chú research vào cùng core mà không sửa session nguồn, không mất attribution hoặc checkpoint, có policy bảo tồn nguồn rõ ràng.

## Status and Dependencies

- Status: In progress — owner tiếp tục S3-T3 sau forced stop; fresh worker phục hồi từ prior history/Main handoff vì worker handoff thiếu. S3-P0 và S3-T1–T2 verification/evidence/checkpoints complete, S3-T3 giữ [~], evidence/checkpoint pending; S3-T4–T6/final differential chưa dispatch. M2 complete, owner xác nhận mọi thay đổi Sprint 2 đã commit. Private-source ingestion chỉ bắt đầu với owner unlock, source allowlist và mẫu được duyệt; locked không private ingestion/refresh.
- [Global plan](../PLAN.md), [specification](../META-BRAIN.md), [requirements](../REQUIREMENTS.md).
- Requirements: R16–R19; bảo toàn R04, R06, R10–R12, R33, R36.

## Downstream Contracts

Adapters normalized events/source refs, không publish/mint grant hoặc map quyền theo cwd. Original .codex/.omp giữ nguyên và ngoài cam kết bảo mật Meta Brain; các bản import/cache/chunk/checkpoint/locator/snapshot riêng trong vault encrypted. S6 dùng source spans/roles chung, S4/S5 source reader authorize riêng. Lock dừng private reads/refresh/writes, không plaintext queue để chạy nền.

## Atomic Tasks

Status legend: [ ] pending / [~] in progress / [x] done. OMP role selection, evidence review, record ownership, and exact-snapshot checkpoints follow the [canonical execution contract](../PLAN.md#planning-and-execution-contract). Worker artifact for S3-T<M>: `artifacts/sprint-3/task-<M>.md`; reviewers and checkpoint executors own their separate records under the sprint artifact tree.

Workflow: full route cho S3-P0 và S3-T1–T6 theo project gates. Mỗi task có worker verification, fresh evidence review và separate Bronze-only exact-snapshot checkpoint trước task kế tiếp; worker không commit. Final differential deep review được giao cho rủi ro tương tác coordinator/adapters/checkpoints/lock/source preservation sau tất cả task gates/checkpoints. Không shared checkpoint batch, không push. Role do owner chọn hoặc Main chọn trong scoped authority; quota unknown. Records chỉ ghi role và work IDs, không model/provider/selector/effort; coordination/handoffs không ghi vào plan.

### Prerequisite — Documentation State Synchronization

- [x] **S3-P0 — Đồng bộ trạng thái tài liệu trước capture implementation.**
    - Scope: current status/entry-gate prose trong specification, requirements, AGENTS, README và các tài liệu liên quan thực sự stale; Main quản lý PLAN và sprint plans. Đồng bộ Sprint 1/M1 và Sprint 2/M2 complete, Sprint 2 đã commit theo owner, Sprint 3 preparing; Sprint 4–8 vẫn not started.
    - Acceptance: không còn current-state claim replacement gates Pending/Sprint 2 not started/owner-only plaintext-only; accepted evidence/checkpoints/final reviews và nonblocking limits được giữ đúng. Current workflow dùng explicit Bronze/Silver/Gold, role-only reporting, reviewer-owned manifest và Bronze-only checkpoint; giữ nguyên project-required gates, R01–R40, CR01, budgets, source consent và task order.
    - History: không rewrite append-only decision/artifact records hoặc historical gate narratives; stale historical trạng thái được phân biệt với current status. Không product/source changes, fake runtime PASS, product changelog entry hoặc rereun Sprint 2 checks.
    - Verification: targeted documentation/current-state/link checks và independent evidence review; reviewer freezes exact eligible snapshot, fresh Bronze checkpoint chỉ wholly-owned reviewed paths. Main plan changes cho prerequisite thuộc S3-P0 scope; worker không chỉnh plans.
    - Records: `artifacts/sprint-3/task-P0.md`, separate reviewer report/manifest và checkpoint dưới `artifacts/sprint-3/`.
    - Outcome: six-document synchronization accepted; [worker artifact](../../artifacts/sprint-3/task-P0.md), [evidence PASS](../../artifacts/sprint-3/reviews/S3-P0-S3P0-REVIEW-20261012-A1.md), [frozen manifest](../../artifacts/sprint-3/reviews/S3-P0-S3P0-REVIEW-20261012-A1.manifest.json), [checkpoint](../../artifacts/sprint-3/checkpoints/S3-P0-S3P0-CHECKPOINT-20261012-A1.md); commit `835e440373a9f228e1d296b8952a9dea6072f9e6`, no push.

### Capture Tasks

- [x] **S3-T1 — Source enrollment và incremental ingestion coordinator.** Requirements: R16, R17, R18.
    - Scope: allowlist/mapping, encrypted checkpoint/quarantine, idempotence/version dispatch và lock-aware coordinator; không parsing format ở đây.
    - Acceptance: không scan mọi profile; unknown/mixed source không mở transcript; restart/reimport không duplicate, replace/truncate không nối sai generation; unsupported báo rõ. Persisted metadata/events encrypted, locked dừng read/import và resume sau unlock không mất checkpoint.
    - Evidence: owner enroll/revoke một fixture source, incremental restart/replace/partial-read scenarios qua coordinator thật; original bytes và metadata không bị ghi lại.
    - Outcome: coordinator real-surface smoke accepted; no shipped Codex/OMP normalizers yet. [Worker evidence](../../artifacts/sprint-3/task-1.md), [durable worker handoff](../../artifacts/sprint-3/handoffs/S3-T1-S3T1-IMPL-20261012-A1.md), [evidence PASS and carrier erratum](../../artifacts/sprint-3/reviews/S3-T1-S3T1-REVIEW-20261012-A1.md), [frozen manifest](../../artifacts/sprint-3/reviews/S3-T1-S3T1-REVIEW-20261012-A1.manifest.json), [checkpoint with all-23-path proof](../../artifacts/sprint-3/checkpoints/S3-T1-S3T1-CHECKPOINT-20261012-A1.md); commit `f70cc44aa000144d2aedff48bba9229b2f550537`, no push.

- [x] **S3-T2 — Adapter Codex JSONL read-only.** Requirements: R16, R17.
    - Scope: parse các schema Codex thực được phát hiện từ mẫu owner cho phép; không giả định mọi version cùng format.
    - Acceptance: giữ user/assistant/tool attribution, timestamps, session identity và source spans; append/partial tail/resume đúng; tool result không thành user statement; event không hiểu được báo/giữ trạng thái rõ, không silently drop.
    - Evidence: ingest ít nhất một session Codex thật được cho phép cùng fixtures boundary; so source trước/sau; truy xuất đúng đoạn chứng cứ. Báo schema/version đã hỗ trợ.
    - Source consent: owner explicit approval cho bất cứ project nào; T2 đã dùng đúng một completed Codex session, read-only, encrypted/private import; timeout auto-selection trước đó không cấp consent.
    - Outcome: Codex adapter accepted qua real owner pipe và coordinator; [worker evidence](../../artifacts/sprint-3/task-2.md), [handoff](../../artifacts/sprint-3/handoffs/S3-T2-S3T2-IMPL-20261012-A1.md), [evidence PASS và errata](../../artifacts/sprint-3/reviews/S3-T2-S3T2-REVIEW-20261012-A1.md), [frozen manifest](../../artifacts/sprint-3/reviews/S3-T2-S3T2-REVIEW-20261012-A1.manifest.json), [checkpoint](../../artifacts/sprint-3/checkpoints/S3-T2-S3T2-CHECKPOINT-20261012-A1.md); commit `33b177789a496e48ed7cd8768ba0d346fa628f52`, all 12 reviewed paths proved, no push. Mẫu thật không có tool events: tool attribution được chứng minh qua fixtures và upstream wire schema; manual Roslyn build, không SDK build.

- [~] **S3-T3 — Adapter OMP JSONL read-only.** Requirements: R16, R17.
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

Owner source consent cho T2/T3: T2 Codex được owner cho phép chọn từ bất cứ project nào; giữ phạm vi tối thiểu metadata tối đa 10 candidate mới nhất và đọc body đúng một completed session được chọn, ưu tiên candidate đã xác định. T3 OMP giữ consent trước: chỉ một completed session trong thư mục sessions riêng của Meta Brain, không đọc OMP project khác. Không whole-profile scan/copy/migration; chỉ read-only originals, import thử nghiệm encrypted/private, không provider egress hoặc raw transcript trong artifacts. Allowed roots theo approval trong routing/consent chat; không public runtime locators/filenames.

Runtime process ownership: từ owner steer hiện hành, mỗi Codex/OMP process worker tự start phải có exact handle/job hoặc PID kèm start time/identity; dùng xong end đúng process đã chứng minh ownership, kiểm tra PID reuse nếu stop bằng PID. Không kill theo process name/broad match hoặc dọn process cũ/pre-existing khi không chắc ownership. Worker giữ actual launch/cleanup evidence trong artifact; không suy delivery receipt thành cleanup proof.

Owner tiếp tục S3-T3 sau khi đã được báo worker bị kill và chưa lưu handoff; fresh recovery phải đọc prior history và xác minh current state/process ownership, không dùng progress cũ làm PASS. S3-T3 giữ [~], full-route gates/consent không đổi. [Main forced-stop handoff](../../artifacts/sprint-3/state/MAIN-S3-T3-20261012.md) giữ compact observations, missing worker handoff và process-cleanup limits; [boundary trước T3](../../artifacts/sprint-3/state/MAIN-S3-T2-20261012.md) và [handoff trước T2](../../artifacts/sprint-3/handoffs/MAIN-S3-after-T1-20261012.md) giữ lịch sử/role choices.

## Gate Record

S3-P0 và S3-T1–T2 evidence/checkpoints complete; latest checkpoint `33b177789a496e48ed7cd8768ba0d346fa628f52`, parent `f70cc44aa000144d2aedff48bba9229b2f550537`. S3-T2 [evidence PASS](../../artifacts/sprint-3/reviews/S3-T2-S3T2-REVIEW-20261012-A1.md) và [all-12-path checkpoint](../../artifacts/sprint-3/checkpoints/S3-T2-S3T2-CHECKPOINT-20261012-A1.md) match immutable reviewed snapshot; Git CRLF normalization và report wording/count errata được ghi trong records của owners. Owner tiếp tục chỉ S3-T3; task giữ [~] tới matching evidence PASS và successful checkpoint. S3-T4–T6/final sprint gate chưa dispatch. Final Main plan/status-only carryover sau T2 checkpoint và current T3 status thuộc Main, được giao vào T3 reviewed checkpoint nếu whole-path ownership xác minh; không product drift hoặc push authorization.
