# Sprint 17 — Folder sharing và evidence context cho agents

## Goal và trạng thái

- Status: **Not started — Planned**. Release mục tiêu cuối sprint: **1.7.0**; chưa build/publish.
- Entry: Sprint 16 complete và 1.6.0 published.
- Scope/trace: P2-08, P2-09, P2-12; R03, R05–R09, R11, R24, R25, R31. Các P2 là candidates, không thay baseline khi chưa có approval.
- Root: `F:/ai-ml/my-meta-brain`; target ref `main`; exact base/worktree/ownership resolve khi execution.
- Contract chung: [roadmap 2.0](../ROADMAP-2.0.md), [direction](../POST-1.0-DIRECTION.md), [requirements](../REQUIREMENTS.md), [PLAN](../PLAN.md).

- CR03/D089: CLI là owner authority surface đầy đủ, MCP là agent surface; browser chỉ document preview/edit, không approval/token/sharing console. Không WPF hoặc UI installer mới.

## Execution và gates

Full OMP route; task tuần tự T1 → T6. Mỗi task cần worker evidence, independent evidence-review PASS và Bronze exact-snapshot checkpoint trước task độc lập kế tiếp. Cuối sprint assigned differential deep review trước owner-authorized publication; publication smoke trước Complete. Current global flow điều khiển async lifecycle và review rounds. Main không implement/Git hoặc viết evidence records.

Chưa authorize launch/routing/commit/push/tag/upload/deploy/provider access. Worker role hoặc scoped routing authority phải xác định khi execution; tối đa hai active agents, một implementation task. Worker đọc implement-atomic-task và mọi regular file trực tiếp dưới ~/.agents/rules/. Artifacts tại `artifacts/sprint-17/task-<M>.md`, reviews/checkpoints dưới cùng sprint; không tạo trước PASS giả.

Không host mutation/ACL/permissions/profile edits; synthetic data mặc định. Không downgrade baseline security, reset data hoặc lấy test suite thay runtime smoke. Decision còn mở/contract thay đổi phải owner-approved và sync docs trước implementation affected path.

## Atomic tasks

Mỗi task sau phụ thuộc toàn bộ task trước; release task theo [release contract](../ROADMAP-2.0.md#7-definition-of-release-mỗi-sprint). Status `[ ]` pending; `[~]` active/blocked; `[x]` chỉ sau acceptance và gates.

- [ ] **S17-T1 — Chốt folder scope và output boundary.**
    - Change: Preview snapshot vs continuous và consent cho file/revision mới, moves/copy/deletion, excluded subfolders, expiry/provider context. Review CR02 impact tới R03/R06/R11/R31; không blanket-expand grants cũ.
    - Acceptance: Owner quyết định semantics và protections/tradeoffs trước implementation; retained token/key separation và egress boundary rõ. CLI/MCP dùng một policy, không file-by-file approval bắt buộc; browser document session không mang sharing authority.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [ ] **S17-T2 — Shared folder grant evaluation.**
    - Change: Implement approved folder selectors/previews/approvals theo concrete chosen semantics, operation/expiry/revoke/lock và encrypted policy generation.
    - Acceptance: One folder approval đủ cho selected scope; new file/revision/move negative hoặc inclusion đúng consent; concurrent revoke/lock/restart không hồi sinh quyền. Old grants giữ frozen semantics.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [ ] **S17-T3 — Owner sharing workflows qua CLI.**
    - Change: CLI chọn vùng, preview effective scope/egress, narrow/approve/reject, handoff/session inspect/revoke qua shared APIs; không browser approval surface.
    - Acceptance: Owner biết ai thấy gì và phạm vi cập nhật ra sao bằng CLI; không auto-publish private names/catalog, raw keys/bearers không hiện args/log/model; handoff dùng đường bảo vệ hiện có, không phải approve từng file.
    - Check: Actual CLI preview → narrow/approve/reject → handoff → session inspect/revoke, negative stale preview/lock; normal clients ở T5.

- [ ] **S17-T4 — Evidence context pack.**
    - Change: Budgeted file/passages + citation/revision/digest + relation rationale/currentness/contradictions/gaps, phân biệt verbatim và generated summary.
    - Acceptance: Budget honored không cắt mất citation attribution, partial/unavailable nêu rõ; reauthorize khi cần, injection không đổi quyền; no double-count same origin, không upload cloud ngầm.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [ ] **S17-T5 — MCP integration qua normal clients.**
    - Change: Expose file/cognitive/hybrid/relational recall, context/source expansion/proposal operations qua versioned MCP cùng policy; guides Codex/OMP không profile auto-edit.
    - Acceptance: Hai client thật request → owner folder approve → redeem → contextual recall/source expansion → revoke/lock denial trên synthetic scope; agent không key/owner authority. Client consent/config steps do owner authorize.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [ ] **S17-T6 — Release 1.7.0.**
    - Change: Publish simplified sharing/context workflows và SDK/protocol compatibility docs.
    - Acceptance: Exact package và normal-client changed paths có evidence; provider inference riêng chỉ khi consent/budget, không suy từ retrieval; release contract chung đạt.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

## Exit và release boundary

- User outcome: Hai client thật request → owner folder approve → redeem → contextual recall/source expansion → revoke/lock denial trên synthetic scope; agent không key/owner authority. Client consent/config steps do owner authorize.
- Tất cả task gates/checkpoints PASS, final differential không actionable findings; không commit_pending.
- Candidate chuẩn bị ở release task; review/checkpoint rồi differential trước publication. Owner authorize external actions riêng; exact public artifacts/links/version smoke hoàn tất rồi Main mới Complete. Release pending chặn sprint sau.
- CLI installer + MCP ZIP và guides version 1.7.0, không UI installer mới; browser document assets không là product riêng. Install/upgrade/data preservation theo CR03/shared contract, không overwrite assets.
- Kế thừa unchanged acceptance có trace; kiểm tra changed surfaces thực, không blanket replay 1.0 hoặc giả fresh PASS.

## Gate record

Chưa có worker/evidence/checkpoint/deep/publication result. Xem [roadmap](../ROADMAP-2.0.md) để biết dependency tiếp theo; không tự advance bởi completion message.
