# Sprint 14 — Cognitive memory và owner-controlled lifecycle

## Goal và trạng thái

- Status: **In progress — S14-T3 evidence review**. Release mục tiêu cuối sprint: **1.4.0**; chưa build/publish. Final-source CLI/service smoke passed and bounded real job #5 succeeded. Usage: 5/10 requests, $0.002741448 settled, zero active reservations. Independent review and checkpoint remain pending.
- Entry: Sprint 13 complete và 1.3.0 published.
- Scope/trace: P2-04, P2-05, P2-12; R01, R12, R13, R19–R22, R26–R30. Các P2 là candidates, không thay baseline khi chưa có approval.
- Root: `F:/ai-ml/my-meta-brain`; target ref `main`; exact base/worktree/ownership resolve khi execution.
- Contract chung: [roadmap 2.0](../ROADMAP-2.0.md), [direction](../POST-1.0-DIRECTION.md), [requirements](../REQUIREMENTS.md), [PLAN](../PLAN.md).

- CR03/D089: CLI owner-complete + MCP; browser từ Sprint 13 chỉ preview/edit tài liệu, không cognitive/review console. WPF frozen ở 1.1.0, không UI installer mới.

## Execution và gates

Full OMP route; prerequisite P0 → task tuần tự T1 → T6. Mỗi task cần worker evidence, independent evidence-review PASS và Bronze exact-snapshot checkpoint trước task độc lập kế tiếp. Cuối sprint assigned differential deep review trước owner-authorized publication; publication smoke trước Complete. Current global flow điều khiển async lifecycle và review rounds. Main không implement/Git hoặc viết evidence records.

Owner authorized Sprint 14 execution, prerequisite documentation sync and per-task reviewed-snapshot checkpoints. Selected workers: Bronze P0; Gold T1/T4/T6; Silver T2/T3/T5. Routing choices remain task-specific; replacement/correction uses the selected role. No push/tag/upload/deploy is authorized. Owner approved a T3-only synthetic provider experiment batch: maximum 10 requests including failures/reruns, total $0.054; each request maximum 10,000 framed input tokens, 2,000 output tokens, 60 seconds and $0.0054; no automatic retry or model tools/browsing. Verify existing approved profile, current pricing and ledger/budget via metadata-only owner access on an already-unlocked channel before sends; no self-unlock, credential reads or profile/config changes. Existing job/rolling budget ceilings remain binding. Tối đa hai active agents, một implementation task. Worker đọc implement-atomic-task và mọi regular file trực tiếp dưới ~/.agents/rules/. Artifacts tại `artifacts/sprint-14/task-<M>.md` (P0: `task-P0.md`), reviews/checkpoints dưới cùng sprint; không tạo trước PASS giả.

Không host mutation/ACL/permissions/profile edits; synthetic data mặc định. Không downgrade baseline security, reset data hoặc lấy test suite thay runtime smoke. Decision còn mở/contract thay đổi phải owner-approved và sync docs trước implementation affected path.

## Atomic tasks

Mỗi task sau phụ thuộc toàn bộ task trước; release task theo [release contract](../ROADMAP-2.0.md#7-definition-of-release-mỗi-sprint). Status `[ ]` pending; `[~]` active/blocked; `[x]` chỉ sau acceptance và gates.

- [x] **S14-P0 — Đồng bộ trạng thái tài liệu sau release 1.3.0.**
    - Change: Correct stale planning/prepublication wording in current project documentation using accepted Sprint 12/13 release records; preserve historical decisions/evidence and normative contracts.
    - Acceptance: Current status consistently distinguishes shipped 1.2/1.3 behavior, frozen WPF 1.1 and pending Sprint 14+; no feature, requirement, budget or authority changes; no product changelog entry for docs-only sync.
    - Check: Targeted stale-status and Markdown/link checks against accepted release records; no runtime replay or fabricated fresh product PASS.
    - Gate: Independent evidence review and separate Bronze exact-snapshot checkpoint before T1.
    - Accepted: [evidence PASS](../../artifacts/sprint-14/reviews/S14-P0-C1-R1.md), [checkpoint](../../artifacts/sprint-14/checkpoints/S14-P0-C1.md), commit `8734bf06`; no push.

- [x] **S14-T1 — Taxonomy và provenance contract.**
    - Change: Chốt World/Experience/Opinion/Habit/Intention/Action–Effect dưới dạng facets, multi-label passage anchors, uncertainty/author/executor và mapping decision/lesson/question/synthesis/L0–L3 hiện có.
    - Acceptance: Owner review taxonomy; không ép một file một type, không xóa existing kinds hoặc coi reported claim là verified fact. Mapping thể hiện thiếu nguồn/thời gian là unknown.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.
    - Accepted: Owner approved six-facet proposal; [evidence PASS](../../artifacts/sprint-14/reviews/S14-T1-C1-R1.md), [checkpoint](../../artifacts/sprint-14/checkpoints/S14-T1-C1.md), commit `0afb0015`; in-memory contract and local CLI vocabulary, persistence remains T2.

- [x] **S14-T2 — Durable annotations và owner review.**
    - Change: Persist owner annotations/classification corrections versioned encrypted, độc lập derived index, review/accept/reject và export/backup.
    - Acceptance: Rebuild index giữ owner edits, multi-view không nhân bản file; AI suggestion khác owner-confirmed, passage source đổi báo stale; optimistic conflicts không overwrite.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.
    - Accepted: [evidence PASS](../../artifacts/sprint-14/reviews/S14-T2-C1-R1.md), [checkpoint](../../artifacts/sprint-14/checkpoints/S14-T2-C1.md), commit `5ee77fb9`; actual CLI/service annotation lifecycle, rebuild/restart, encrypted backup/restore and selected export verified.

- [~] **S14-T3 — Grounded structuring vào workspace.**
    - Change: Nối coding/research/trend/synthesis jobs hiện có vào source file revisions và pending annotations/proposals; giữ raw-first capture trước model.
    - Acceptance (owner revised after live diagnosis): Real bounded job khi có provider consent/budget, output trỏ đúng source/revision/excerpt/anchor; malformed, wrong excerpt/scope và stale revisions bị từ chối. Title/body được paraphrase nhưng luôn là unverified AI suggestion pending owner review trước publication, không tự thành verified fact hoặc owner belief; không claim tự động chứng minh semantic entailment hay từ chối mọi suy luận sai trong prose. Provider lỗi/cancel không mất raw hoặc owner edits. Không mock model proof.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.
    - [Worker evidence](../../artifacts/sprint-14/task-3.md): owner-approved unverified AI paraphrases pending review implemented, D096/spec/changelog synced, temporary capture removed, final-source CLI/service smoke passed. Real job #5 succeeded with pending AI proposal/annotation, exact anchor and raw/owner preservation; 5/10 requests, $0.002741448 settled, zero active reservations. Live proof exposed a creation-method label mismatch; candidate correction is locally smoke-verified, while the existing pending live proposal retains its historical label. Independent review will assess this evidence limit; no checkpoint yet.

- [ ] **S14-T4 — Temporal/intention/recall-policy mapping.**
    - Change: Nối history/as-of/validity/review-after, intention due/context/ack/done và pin/demote/archive/restore vào file/passage memory theo mapping.
    - Acceptance: Restart/offline/lock không duplicate due notifications; mentioned/acknowledged khác completed; reads không nâng truth; history giữ nguồn và superseded semantics.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [ ] **S14-T5 — Cognitive queries và review qua CLI.**
    - Change: CLI list/query/filter cho sáu facets, decisions/questions/lessons và review queue; inspect nguồn/revision rồi sửa/bác phân loại qua shared APIs, tạo ghi chú không cần model. `open` chỉ hiển thị tài liệu đã chọn, không chứa review controls.
    - Acceptance: Owner tìm trải nghiệm/dự định/quan điểm, đọc đúng nguồn và sửa nhãn/review hoàn toàn bằng CLI; source availability và AI-origin rõ, không bắt điền taxonomy trước lưu file.
    - Check: Actual CLI query → source inspect → correction/accept/reject → restart/rebuild; browser chỉ exercise nếu document path thay đổi.

- [ ] **S14-T6 — Release 1.4.0.**
    - Change: Publish cognitive/lifecycle workflows và attribution/review guides, nối jobs/source/operations hiện có.
    - Acceptance: Changed flows chạy thật trên candidate; nếu provider consent thiếu thì model task/release blocked, không dùng mocked echo làm acceptance; release contract chung đạt.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

## Exit và release boundary

- User outcome: Owner tìm trải nghiệm/dự định/quan điểm, inspect nguồn và sửa nhãn/review bằng CLI; source availability/AI-origin rõ, không yêu cầu browser/WPF.
- Tất cả task gates/checkpoints PASS, final differential không actionable findings; không commit_pending.
- Candidate chuẩn bị ở release task; review/checkpoint rồi differential trước publication. Owner authorize external actions riêng; exact public artifacts/links/version smoke hoàn tất rồi Main mới Complete. Release pending chặn sprint sau.
- CLI installer + MCP ZIP và guides version 1.4.0, không UI installer mới; browser document assets không là product riêng. Install/upgrade/data preservation theo CR03/shared contract, không overwrite assets.
- Kế thừa unchanged acceptance có trace; kiểm tra changed surfaces thực, không blanket replay 1.0 hoặc giả fresh PASS.

## Gate record

S14-P0 accepted with [review](../../artifacts/sprint-14/reviews/S14-P0-C1-R1.md) and [checkpoint](../../artifacts/sprint-14/checkpoints/S14-P0-C1.md) `8734bf06`; S14-T1 owner-approved taxonomy with [review](../../artifacts/sprint-14/reviews/S14-T1-C1-R1.md) and [checkpoint](../../artifacts/sprint-14/checkpoints/S14-T1-C1.md) `0afb0015`; S14-T2 with [review](../../artifacts/sprint-14/reviews/S14-T2-C1-R1.md) and [checkpoint](../../artifacts/sprint-14/checkpoints/S14-T2-C1.md) `5ee77fb9`. S14-T3 [candidate evidence](../../artifacts/sprint-14/task-3.md): final-source local smoke passed, real job #5 succeeded, 5/10 requests/$0.002741448 settled; independent review and exact-snapshot checkpoint pending. No T3 PASS, T4–T6 gates, final deep or 1.4.0 publication result yet. No push.
