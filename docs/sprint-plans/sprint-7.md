# Sprint 7 — Thời gian, intentions và quên có kiểm soát

## Sprint Goal

Nhớ đúng thời điểm, nhắc đúng ý định đã được yêu cầu, archive/purge đúng phạm vi và giữ lịch sử mà không tự diễn giải lại suy nghĩ của owner.

## Status and Dependencies

- Status: Not started — CR01 updated. Entry: Sprint 6 PASS; encrypted domain/lineage và lock/session APIs. Private scheduler/notifications không tự unlock vault.
- [Global plan](../PLAN.md), [specification](../META-BRAIN.md), [requirements](../REQUIREMENTS.md).
- Requirements: R26–R29; bảo toàn R01, R03–R06, R08, R12, R14, R18, R25, R30, R36.

## Downstream Contracts

Lifecycle không sửa authored content cho ranking. Private state encrypted; locked pause private scheduler/query/notifications và catch up đúng sau user unlock, không key persisted hoặc sensitive due metadata plaintext. Purge gồm catalog projection, request/grant/cache/index/snapshots trong product; originals/exports/external copies riêng. Restore không hồi sinh used tokens/live grants hoặc deleted knowledge; MCP không owner purge.

## Atomic Tasks

Status legend: [ ] pending / [~] in progress / [x] done. Artifact cho S7-T<M>: artifacts/sprint-7/task-<M>.md.

- [ ] **S7-T1 — Temporal recall và lịch sử quyết định end-to-end.** Requirements: R26, R23, R30.
    - Scope: hoàn thiện current/as-of/history query và UI timeline trên validity/revision đã có; không đổi temporal schema meaning.
    - Acceptance: import hôm nay không làm event cũ thành mới; unknown/approximate time không bị làm chính xác giả; current tránh superseded, historical giữ lý do cũ; contradiction chưa phân giải không newest-wins; timezone boundaries đúng.
    - Evidence: quyết định cũ/mới và conflicting research theo nhiều mốc, query qua MCP và thao tác UI; source evidence ở đúng revision/time.

- [ ] **S7-T2 — Intention due scheduling bền vững.** Requirements: R27, R01.
    - Scope: time triggers, due state và acknowledgement độc lập completion; lịch không cần LLM.
    - Acceptance: explicit create/edit/cancel, timezone/clock policy đúng; offline hoặc locked qua deadline rồi restart/unlock vẫn due, không notified=done hoặc notification storm. Locked không lộ intention/title/body hoặc tự unlock; trạng thái encrypted bền vững.
    - Evidence: actual controlled-time scheduler, offline/lock/restart/unlock/cancel/reschedule, actual UI due list và no private notifications khi locked; không long sleep tests.

- [ ] **S7-T3 — Contextual intention recall và owner controls.** Requirements: R27, R01, R24, R30.
    - Scope: trigger theo project/task context user chỉ định, surfaced qua brief/MCP và timeline UI.
    - Acceptance: enabled trigger và approved ID/revision session scope mới hiện, read không done; explicit owner controls; no private notification khi locked hoặc outside scope. New intention/member/revision không tự vào grant; disable ngừng trigger.
    - Evidence: mở hai project contexts với grants khác nhau; due/context reminder → acknowledge → explicit complete, hỏi lịch sử vẫn thấy motivation gốc.

- [ ] **S7-T4 — Forgetting policy, pin/archive/restore.** Requirements: R28, R25, R01.
    - Scope: recall salience và archive states theo policy user; không xóa dữ liệu.
    - Acceptance: ít đọc chỉ giảm ưu tiên theo lựa chọn, không giảm độ đúng hoặc auto-delete; pinned rare lesson còn tìm được; archived tìm qua history/explicit expansion; restore khôi phục visibility thích hợp không mở quyền.
    - Evidence: rare important lesson, noisy repeated item và archive/restore qua UI/MCP queries; ranking reason rõ, read loop không self-reinforce truth.

- [ ] **S7-T5 — Owner purge và derivative impact handling.** Requirements: R29, R08, R18.
    - Scope: dry-run impact, confirm, durable deletion bookkeeping và purge trong vault/index/cache/snapshots; backup lifecycle integration contract cho S8.
    - Acceptance: owner preview impact, encrypted deletion bookkeeping không giữ removed payload; xử lý private copies/lineage/cache/request scopes và published catalog withdrawal. Original session không xóa mặc định; invalidate session access to purged IDs, no restore/reindex resurrection. Không claim secure physical erase hoặc xóa external/retained-backup copies.
    - Evidence: seed encrypted source/summary/snapshot/index/cache và approved catalog, owner purge, read/search/catalog/rebuild deny, interrupted resume; live scoped session không còn đọc removed item, agent không owner purge.

- [ ] **S7-T6 — Review-after và lifecycle reconciliation.** Requirements: R26, R27, R28, R29, R39.
    - Scope: due-for-review, source-change invalidation và reconciliation của các lifecycle transitions đã có; không auto-rewrite tri thức.
    - Acceptance: nguồn đổi, unavailable hoặc item review_after tới hạn hiện rõ trong UI, không tự phủ định hoặc sửa belief; source reimport/index rebuild không hồi sinh purged items; restart giữ intention/archive/review states và xử lý deletion policy với source enrollment rõ.
    - Evidence: source change → stale synthesis → owner review; purge → reingest/reindex → không resurrection trái policy; restart kết hợp due/archive/review state.

## Sprint Acceptance Gate

Temporal/question/intention/rare lesson qua unlocked UI/MCP đúng nghĩa; locked/offline catch-up sau unlock không leak notification hoặc auto-complete. Archive/restore không mở scope; purge/catalog withdrawal không hồi sinh qua cache/reindex/reimport/restore. State encrypted, external copies/backup retention không xóa tuyệt đối; no auto-belief rewrite.

## Notes / Blockers

Retention mặc định không được phá hủy dữ liệu chỉ vì lâu không dùng. Purge là owner operation có confirm, không cho agent rộng quyền vì tiện. Date parsing mơ hồ cần giữ precision/unknown hoặc hỏi user, không đoán lịch gây tác động.

## Gate Record

Pending. Chưa có scheduler/lifecycle runtime verification.
