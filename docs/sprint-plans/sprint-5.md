# Sprint 5 — Owner application và UI workbench

## Sprint Goal

Owner unlock encrypted vault, quản lý recovery/catalog/access requests/scoped sessions và dùng capture/review/library/relations qua UI thật; không cần agent hoặc sửa config thủ công, không protected launcher.

## Status and Dependencies

- Status: In progress — S5-T1–T3 complete với accepted evidence và exact-snapshot checkpoints; S5-T4 đang triển khai, S5-T5…S5-T7 pending. Entry Sprint 4 PASS. UI không tạo security policy hoặc key store riêng.
- [Global plan](../PLAN.md), [specification](../META-BRAIN.md), [requirements](../REQUIREMENTS.md).
- Requirements: R30, R31; bảo toàn R02–R06, R11, R12, R14, R19, R32, R34, R36.

## Downstream Contracts

UI dùng shared encrypted core và key/lock/approval APIs; không direct decrypt/filesystem writes. Key không tới untrusted renderer/model; không claim chống same-owner OS malware điều khiển UI. Proposal/revision/source model tái dùng S6, không AI-note truth riêng. Locked screen không private cached content, pending requests không tự approve; preview dùng core snapshot/disclosure policy.

## Atomic Tasks

Status legend: [ ] pending / [~] in progress / [x] done. OMP role selection, evidence review, record ownership, and exact-snapshot checkpoints follow the [canonical execution contract](../PLAN.md#planning-and-execution-contract). Worker artifact for S5-T<M>: `artifacts/sprint-5/task-<M>.md`; reviewers and checkpoint executors own their separate records under the sprint artifact tree.

Workflow: Full route. Mỗi task cần independent evidence PASS và fresh Bronze exact-snapshot checkpoint trước task độc lập tiếp theo; sprint differential review sau mọi task gate/checkpoint. S5-T1–S5-T4 được owner giao Silver; worker không commit, chỉ checkpoint executor commit reviewed task-owned scope trên `main`, không push. Gate records dưới `artifacts/sprint-5/`; Main chỉ ghi concise outcome links.

- [x] **S5-T1 — Owner UI host và authenticated application boundary.** Requirements: R02, R05, R30, R32.
    - Scope: runtime UI đã chọn, owner session/auth, navigation và lỗi kết nối; không dashboard giả bằng mocks.
    - Acceptance: UI nối service thật, thể hiện locked/unavailable rõ, agent API/session không gọi owner operations hoặc suy body/Origin/localhost thành consent. Owner workflow explicit; không claim OS protection khỏi same-account process.
    - Evidence: actual UI/service connection, agent-channel owner calls denied, stop/restart/startup locked; visual proof, không mocks.

- [x] **S5-T2 — Unlock/lock và key/recovery UI.** Requirements: R02, R04, R31, R34, R36.
    - Scope: owner key provisioning/unlock, recovery guidance và lock lifecycle qua S1 APIs; không key hierarchy hoặc autonomous unlock.
    - Acceptance: wrong/missing key không mở vault; recovery material user tự bảo quản, không persist plaintext secret; mất key không fake reset/recovery. Lock clear private views/cache, revoke sessions/tokens, pause jobs; restart locked. Key không agent/model/log.
    - Evidence: actual UI unlock/wrong-key/lock/restart/recovery flow trên synthetic vault, read/session bị chặn sau lock; visual proof và limits managed-memory erasure.

- [x] **S5-T3 — Library và evidence detail.** Requirements: R10, R11, R12, R23, R30.
    - Scope: search/browse/filter theo zone/collection/kind/level/time và detail source/revision; không tự làm ranking frontend.
    - Acceptance: unlocked UI tìm Việt–Anh/evidence/attribution/current/history/missing nguồn đúng; collections không quyền ngầm, keyboard cơ bản. Lock giữa search/detail xóa private view và chặn refresh; không plaintext UI cache persist.
    - Evidence: actual UI flow search → detail → source/revision, sửa title vẫn cùng ID; không chỉ component snapshots/tests.

- [~] **S5-T4 — Capture và proposal review/editor.** Requirements: R12, R19, R30.
    - Scope: text capture vào source, proposal inbox, side-by-side source/editor và publish/reject qua core.
    - Acceptance: unlocked nhập text+zone, encrypted save/revision conflict/authorship/undo đúng; AI save không user-written. Locked không báo saved hoặc persist plaintext draft; unsaved input xử lý/cảnh báo rõ trước lock, không fake fallback.
    - Evidence: thao tác UI thật, restart sau save, concurrent edit conflict; malformed input không mất văn bản user; không bắt provider để ghi nhớ.

- [ ] **S5-T5 — Explicit relations workbench.** Requirements: R14, R15, R30.
    - Scope: list/add/remove/inspect relations và proposed links với lý do/provenance; không cần graph visualization.
    - Acceptance: owner nối hai items, chọn related_to thay vì supports, xem nguồn/cycle error và undo; change revision hiển thị stale dependency; relation tạo từ MCP hiển thị executed_by/confirmation đúng.
    - Evidence: UI operations kết hợp link đã tạo bởi agent thật; cross-zone visibility preview không lộ endpoint agent không có quyền.

- [ ] **S5-T6 — Catalog/access request approval và session console.** Requirements: R02–R07, R31.
    - Scope: publish/withdraw catalog metadata, pending request/mục đích, owner preview/narrow/approve/reject, token local handoff, effective scope/expiry/revoke qua S1 APIs.
    - Acceptance: private index khác approved catalog; publication preview không auto title/path/citations. Request không permission; user chọn concrete IDs/revisions/operations/raw/provider disclosure. Read-only default, added member/revision không auto scope; no master key/token qua chat/log/args; không protected launcher.
    - Evidence: actual UI publish catalog → agent request → owner narrow/reject/approve → connector redeem/read → revoke/lock/deny; stale preview và used token negatives, catalog withdrawal và consent được quan sát.

- [ ] **S5-T7 — Source và organization settings cho vận hành thường ngày.** Requirements: R11, R16, R18, R19, R30.
    - Scope: quản lý zone/collection, enroll/pause nguồn, preservation consent và xem ingestion health/errors; không model settings chưa có implementation.
    - Acceptance: owner thực hiện từ UI không sửa config file; thêm collection không cấp quyền; source chưa rõ mapping vào quarantine; source missing/unknown format có hành động khắc phục; preservation có phạm vi/retention rõ, không tự migrate transcript.
    - Evidence: UI capture research + enroll allowed session + pause/resume + source loss + chọn snapshot; actual source bytes không thay đổi và accessibility/keyboard workflow được quan sát.

## Sprint Acceptance Gate

Owner không coding agent vẫn unlock/lock/recovery, encrypted capture/search/evidence/review/link và quản lý nguồn/catalog/request/scoped sessions được. Hai câu hỏi “vì sao nhớ?” và “agent nào thấy?” dùng core policy, phân biệt metadata công bố và body đã cấp. Actual UI/backend, no key disclosure hoặc automatic consent; same-owner OS compromise ngoài threat model.

## Notes / Blockers

Không áp phong cách thương hiệu khi user chưa yêu cầu. Native/web host đã chốt qua S1 evidence; verify surface thực tương ứng. Scheduler/model/purge controls được triển khai thật ở sprint phụ trách, không đặt nút no-op để giả completeness.

## Gate Record

S5-T1 complete: [worker evidence](../../artifacts/sprint-5/task-1.md), [independent evidence PASS](../../artifacts/sprint-5/reviews/S5-T1-S5T1-REVIEW-20261015-A1.md) và [Bronze exact-snapshot checkpoint PASS](../../artifacts/sprint-5/checkpoints/S5-T1-S5T1-CHECKPOINT-20261015-A1.md); commit `591973e32c541d719aba17adb2b07012614562ae`. Actual WPF/service và visual proof accepted; zero actionable findings. No push.

S5-T1 nonblocking residuals được giữ trong evidence review: pre-existing MCP non-string argument crash (fail closed, ngoài scope S5-T1); transient `locking` và protocol-mismatch branches chưa được exercise động.

S5-T2 complete: [worker evidence](../../artifacts/sprint-5/task-2.md), [independent evidence PASS](../../artifacts/sprint-5/reviews/S5-T2-S5T2-REVIEW-20261015-A1.md) và [Bronze exact-snapshot checkpoint PASS](../../artifacts/sprint-5/checkpoints/S5-T2-S5T2-CHECKPOINT-20261015-A1.md); commit `68302cea0b89067497139572e23c7f991a86dc98`. Actual WPF provisioning/unlock/lock/recovery, preserved-data recovery, credential-safe visual proof và runtime denials accepted; zero actionable findings. No push.

S5-T2 limits được giữ trong evidence review: service-disappears-mid-act race, transient `locking` và protocol-mismatch chưa được exercise động; managed-memory/OS compromise không có secure-erasure claim.

S5-T3 complete: [worker evidence](../../artifacts/sprint-5/task-3.md), [independent evidence PASS](../../artifacts/sprint-5/reviews/S5-T3-S5T3-REVIEW-20261015-A1.md) và [Bronze exact-snapshot checkpoint PASS](../../artifacts/sprint-5/checkpoints/S5-T3-S5T3-CHECKPOINT-20261015-A1.md); commit `389819a085406bf153f33fe43d9f46ddd90f6c6c`. Actual WPF bilingual search/filter/detail/source/revision, stable-ID title update, keyboard và lock-clearing proof accepted; zero actionable findings. No push.

S5-T3 limits được giữ trong evidence review: late-reply rejection được kiểm chứng tại deterministic application seam, không bằng wall-clock race; canonical item chỉ phục vụ live revision, không claim đọc archive revision trước S7. S5-T4…S5-T7 và sprint differential gate còn pending; không suy task PASS thành Sprint 5 complete.
