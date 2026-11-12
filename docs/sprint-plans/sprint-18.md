# Sprint 18 — Migration, daily-use acceptance và 2.0 readiness

## Goal và trạng thái

- Status: **Not started — Planned**. Release mục tiêu cuối sprint: **1.8.0**; chưa build/publish.
- Entry: Sprint 17 complete và 1.7.0 published.
- Scope/trace: P2-01–P2-12; R01–R40 retained/approved changes. Các P2 là candidates, không thay baseline khi chưa có approval.
- Root: `F:/ai-ml/my-meta-brain`; target ref `main`; exact base/worktree/ownership resolve khi execution.
- Contract chung: [roadmap 2.0](../ROADMAP-2.0.md), [direction](../POST-1.0-DIRECTION.md), [requirements](../REQUIREMENTS.md), [PLAN](../PLAN.md).

- CR03/D089: CLI owner-complete + MCP; optional browser document preview/edit được kiểm chứng riêng, không tái tạo desktop/web console. WPF 1.1.0 historical/frozen, không UI installer mới hoặc old-UI compatibility promise.

## Execution và gates

Full OMP route; task tuần tự T1 → T6. Mỗi task cần worker evidence, independent evidence-review PASS và Bronze exact-snapshot checkpoint trước task độc lập kế tiếp. Cuối sprint assigned differential deep review trước owner-authorized publication; publication smoke trước Complete. Current global flow điều khiển async lifecycle và review rounds. Main không implement/Git hoặc viết evidence records.

Chưa authorize launch/routing/commit/push/tag/upload/deploy/provider access. Worker role hoặc scoped routing authority phải xác định khi execution; tối đa hai active agents, một implementation task. Worker đọc implement-atomic-task và mọi regular file trực tiếp dưới ~/.agents/rules/. Artifacts tại `artifacts/sprint-18/task-<M>.md`, reviews/checkpoints dưới cùng sprint; không tạo trước PASS giả.

Không host mutation/ACL/permissions/profile edits; synthetic data mặc định. Không downgrade baseline security, reset data hoặc lấy test suite thay runtime smoke. Decision còn mở/contract thay đổi phải owner-approved và sync docs trước implementation affected path.

## Atomic tasks

Mỗi task sau phụ thuộc toàn bộ task trước; release task theo [release contract](../ROADMAP-2.0.md#7-definition-of-release-mỗi-sprint). Status `[ ]` pending; `[~]` active/blocked; `[x]` chỉ sau acceptance và gates.

- [ ] **S18-T1 — Freeze migration và final acceptance contract.**
    - Change: Inventory mappings legacy items/sources/revisions/zones/relations/lifecycle/jobs/catalog/grants; bảng CLI feature parity, requirement/evidence và budgets từ các sprint trước. Review default/API removal riêng cho G2; giữ support-transition evidence installed WPF/shared runtime từ S12.
    - Acceptance: Owner approve migration/cutover impact, rollback limits và acceptance corpus; no fabricated original file cho item chỉ có structured body. Final thresholds frozen trước tuning, không mở lại Sprint 9 wholesale.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [ ] **S18-T2 — Migration preview và ownership transfer.**
    - Change: Dry-run encrypted legacy-to-workspace mapping, conflicts/unknowns, key/format compatibility; execute chỉ sau explicit owner confirm và consistent encrypted backup.
    - Acceptance: Original bytes có nguồn giữ nguyên; generated representation được ghi đúng nhãn; IDs/revisions/attribution/citations/relations/lifecycle giữ mapping truy ngược. Không dual-write/two truth copies hoặc tự broaden permissions.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [ ] **S18-T3 — Interrupted migration và restore.**
    - Change: Implement restart/resume/fail-safe của approved migration, restore backup bằng key, no session/token resurrection và truthful downgrade path.
    - Acceptance: Crash tại write/transfer boundaries không mất saved data hoặc double-import; wrong key/tamper/schema mismatch fail closed. Binary downgrade bằng restore compatible backup, không hứa new format đọc bởi 1.0.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [ ] **S18-T4 — Retention/purge và portability completeness.**
    - Change: Nối purge impact tới originals thuộc workspace, passages/index/embeddings/annotations/relations/snapshots/backups; giữ no-delete-original-session rule.
    - Acceptance: Preview chính xác owner-controlled scope, interrupted purge resume và no resurrection; retention/backups/copies ngoài kiểm soát nêu rõ. Export original + metadata và backup restore giữ owner corrections.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [ ] **S18-T5 — Daily-use và resource acceptance.**
    - Change: Owner walkthrough hoàn toàn qua CLI: file/folder → capture → search → cognitive filters/review → relations → synthesis/jobs → intentions → context sharing; thêm optional browser document preview/edit. Targeted quality/performance/security measurements trên approved representative corpus.
    - Acceptance: Owner ghi nhận useful/wrong/stale/missing; no unresolved serious finding; budgets/held-out relevance đạt hoặc owner-approved requirement change, không đổi số đo để PASS. CLI-only parity và browser keyboard/preview/edit/conflict/lock/exit verified trên actual surfaces; không đòi WPF replay.
    - Check: Packaged CLI/service + normal MCP clients theo changed scope; browser thật cho document lifecycle, không dùng test suite thay runtime proof.

- [ ] **S18-T6 — Release 1.8.0 và G2 handoff.**
    - Change: Freeze cumulative 1.8 candidate, migration/compatibility docs, future major removals, complete evidence/known-limit map và publish theo contract chung.
    - Acceptance: Usable 1.8.0 published trước G2; opt-in migration chạy thật, không migration stub; readiness không phải 2.0 PASS. G2 chỉ được mở khi mọi sprint task/checkpoint/deep/publication complete.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

## Exit và release boundary

- User outcome: Owner hoàn thành daily workflow bằng CLI, context qua MCP; optional browser document preview/edit không tạo authority/cache ngoài contract. Owner acceptance, resources/quality và security negatives có evidence, không WPF gate mới.
- Tất cả task gates/checkpoints PASS, final differential không actionable findings; không commit_pending.
- Candidate chuẩn bị ở release task; review/checkpoint rồi differential trước publication. Owner authorize external actions riêng; exact public artifacts/links/version smoke hoàn tất rồi Main mới Complete. Release pending chặn sprint sau.
- CLI installer + MCP ZIP và guides version 1.8.0, không UI installer mới; browser document assets không là product riêng. Install/upgrade/data preservation theo CR03/shared contract, không overwrite assets.
- Kế thừa unchanged acceptance có trace; kiểm tra changed surfaces thực, không blanket replay 1.0 hoặc giả fresh PASS.

## Gate record

Chưa có worker/evidence/checkpoint/deep/publication result. Xem [roadmap](../ROADMAP-2.0.md) để biết dependency tiếp theo; không tự advance bởi completion message.
