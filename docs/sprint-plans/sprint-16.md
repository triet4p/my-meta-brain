# Sprint 16 — File/folder relations và relational retrieval

## Goal và trạng thái

- Status: **In progress — S16-T1–T4 Complete; S16-T5–T6 unlaunched**. T4 independent evidence R1 PASS and exact-snapshot checkpoint `e7e0433a`; release mục tiêu cuối sprint: **1.6.0**, chưa build/publish.
- Entry: Sprint 15 complete và 1.5.0 published.
- Scope/trace: P2-07, P2-10, P2-12; R08, R14, R15, R23, R25, R36. Các P2 là candidates, không thay baseline khi chưa có approval.
- Root: `F:/ai-ml/my-meta-brain`; target ref `main`; exact base/worktree/ownership resolve khi execution.
- Contract chung: [roadmap 2.0](../ROADMAP-2.0.md), [direction](../POST-1.0-DIRECTION.md), [requirements](../REQUIREMENTS.md), [PLAN](../PLAN.md).

- CR03/D089: CLI owner-complete + MCP; browser chỉ preview/edit endpoint document, không relation/graph workbench. Không WPF hoặc UI installer mới.

## Execution và gates

Full OMP route; task tuần tự T1 → T6. Mỗi task cần worker evidence, independent evidence-review PASS và Bronze exact-snapshot checkpoint trước task độc lập kế tiếp. Cuối sprint assigned differential deep review trước owner-authorized publication; publication smoke trước Complete. Current global flow điều khiển async lifecycle và review rounds. Main không implement/Git hoặc viết evidence records.

Owner authorized launch of **S16-T1 only**, with a Gold worker, independent evidence review and a separate Bronze exact-snapshot commit checkpoint on `main`. Endpoint semantics require owner review before the contract is settled. No authorization for S16-T2–T6, push/tag/upload/deploy/provider access. Tối đa hai active agents, một implementation task. Worker đọc implement-atomic-task và mọi regular file trực tiếp dưới ~/.agents/rules/. Artifacts tại `artifacts/sprint-16/task-<M>.md`, reviews/checkpoints dưới cùng sprint; không tạo trước PASS giả.

Owner authorized **S16-T2** with a Silver worker, full route, independent evidence review and a separate Bronze exact-snapshot commit checkpoint on `main`. Commit scope is the reviewed T2 snapshot only; unrelated changes are excluded. No S16-T3–T6 launch, push or publication is authorized.

Owner-approved T2-only verification exception: the standing normal Codex/OMP MCP-client check is replaced here by actual packaged `MetaBrain.Mcp.exe` stdio proposal/denial smoke with a disclosed synthetic JSON-RPC driver. This unblocks the reviewed T2 checkpoint without claiming real-client proof; normal-client smoke remains mandatory at T6 before publication. No profile/settings/provider changes are authorized.

Owner authorized **S16-T3** with a Silver worker, full route, runtime verification, independent evidence review and a separate Bronze exact-snapshot checkpoint on `main`, including T3 documentation/status. Only reviewed T3 scope may be committed; unrelated changes are excluded. No T4–T6 launch, push or publication; the T2-only MCP verification exception does not extend to T3.

Owner authorized **S16-T4** with a Gold worker, full route, actual changed-surface verification, independent evidence review and a separate Bronze exact-snapshot checkpoint on `main`, including T4 documentation/status. Stage only reviewed T4-owned changes; exclude unrelated changes. No T5–T6 launch, push or publication; the T2-only MCP verification exception does not extend to T4.

Không host mutation/ACL/permissions/profile edits; synthetic data mặc định. Không downgrade baseline security, reset data hoặc lấy test suite thay runtime smoke. Decision còn mở/contract thay đổi phải owner-approved và sync docs trước implementation affected path.

Owner approved the full [S16-T1 proposal](../../artifacts/sprint-16/task-1.md), including A–E, on 2026-11-12 (“Duyệt hết”): workspace endpoint pinning/staleness, pinned contradiction evidence, conservative all-stored-state mixed DAG, scope-limited export and prospective edge history. This approves the contract only, not S16-T2–T6 implementation, migration, push or publication.

The approved normative [workspace relation contract](../META-BRAIN.md#41-approved-workspace-relation-contract-s16-t1-d107) and [requirements trace](../REQUIREMENTS.md#owner-approved-workspace-relation-contract-s16-t1-d107-2026-11-12) bind T2–T5: retain all seven kinds and legacy behavior, both-endpoint workspace evidence and staleness, the all-stored-state mixed DAG, prospective history, canonical edge durability and scope-limited export. T3–T5 must preserve scope and bounded traversal. This pointer approves no implementation; S17 sharing and S18 migration remain separately gated.

## Atomic tasks

Mỗi task sau phụ thuộc toàn bộ task trước; release task theo [release contract](../ROADMAP-2.0.md#7-definition-of-release-mỗi-sprint). Status `[ ]` pending; `[~]` active/blocked; `[x]` chỉ sau acceptance và gates.

- [x] **S16-T1 — Relation endpoint và revision contract.**
    - Change: Chốt file/folder/passage endpoints, membership vs semantic links, related/supports/contradicts/supersedes/applies_to/derived_from và history. Folder applicability không tự áp evidence cho mọi child.
    - Acceptance: Owner review endpoint semantics; stable identity, pinned evidence và cycle/stale rules rõ; adjacency index rebuildable, explicit edges durable và exportable.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [x] **S16-T2 — Shared relation operations.**
    - Change: Create/inspect/confirm/retract/restore typed edges có provenance và revision preconditions; import file links có origin riêng, AI suggestions pending.
    - Acceptance: Rename/move giữ edges; stale endpoint và cycle bị báo đúng; similarity không thành supports, agent claim không owner acceptance; index rebuild/backup restore giữ edges.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [x] **S16-T3 — Relational candidate expansion.**
    - Change: Từ hybrid seeds mở rộng theo kind/direction và bounded hops/candidates/context; scope checks trên mọi endpoint.
    - Acceptance: Multi-hop evidence/refutation tìm được trên frozen cases, no traversal leak ở titles/counts/paths; cycle, fan-out và irrelevant neighbors không làm context vô hạn.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [x] **S16-T4 — Relational ranking và explanations.**
    - Change: Rank relation-derived hits cùng direct hits, dedupe source/lineage và expose lý do/đường dẫn phù hợp.
    - Acceptance: Câu vì-sao/quyết-định ưu tiên bằng chứng liên quan, vẫn giữ contradictions; không newest-wins hoặc confidence tự tăng vì nhiều link lặp cùng nguồn. Đo quality delta và tài nguyên trước release.
    - Check: Exercise changed CLI/service/MCP paths; actual browser chỉ khi document preview/edit bị ảnh hưởng, không WPF gate. Ghi scenarios/observations/limits, không claim surface chưa chạy.

- [ ] **S16-T5 — Owner relation workflows qua CLI.**
    - Change: CLI nối file/folder và inspect evidence neighborhood/provenance/review state qua shared APIs; đọc endpoint tại revision hoặc optional `open` tài liệu. Không browser graph/relation controls.
    - Acceptance: Actual CLI add → inspect → source revise → needs-review → remove/undo; đọc endpoint đúng revision, lock chặn private responses; cognitive filters và folder listing không tạo duplicate data.
    - Check: CLI/service relation lifecycle và retrieval thật, MCP khi affected; browser chỉ cho changed document path.

- [ ] **S16-T6 — Release 1.6.0.**
    - Change: Publish typed relations/relational search và scope/interpretation limits.
    - Acceptance: Runtime CLI retrieval/relations và changed MCP/API negatives, exact candidate measured; release contract chung đạt.
    - Check: Exercise packaged CLI/service/MCP affected paths; document browser khi changed, không WPF proof.

## Exit và release boundary

- User outcome: CLI add → inspect → source revise → needs-review → remove/undo; endpoint đúng revision và lock denial, không bắt mở browser hoặc graph view.
- Tất cả task gates/checkpoints PASS, final differential không actionable findings; không commit_pending.
- Candidate chuẩn bị ở release task; review/checkpoint rồi differential trước publication. Owner authorize external actions riêng; exact public artifacts/links/version smoke hoàn tất rồi Main mới Complete. Release pending chặn sprint sau.
- CLI installer + MCP ZIP và guides version 1.6.0, không UI installer mới; browser document assets không là product riêng. Install/upgrade/data preservation theo CR03/shared contract, không overwrite assets.
- Kế thừa unchanged acceptance có trace; kiểm tra changed surfaces thực, không blanket replay 1.0 hoặc giả fresh PASS.

## Gate record

S16-T1 Complete: owner approved A–E on 2026-11-12; [contract evidence R1 PASS](../../artifacts/sprint-16/reviews/S16-T1-C1-R1.md), zero actionable findings; [Bronze checkpoint C2](../../artifacts/sprint-16/checkpoints/S16-T1-C2.md) committed exact reviewed snapshot as `9999487812724ffe2f85756f4c9162954fb482e0`. T1 verification was documentation/static only; runtime implementation followed in T2.

S16-T2 Complete: [worker evidence](../../artifacts/sprint-16/task-2.md), [R2 PASS](../../artifacts/sprint-16/reviews/S16-T2-C1-R2.md), zero remaining actionable findings after R1 corrections; [Bronze checkpoint C1](../../artifacts/sprint-16/checkpoints/S16-T2-C1.md) committed exact reviewed snapshot as `24b8ac0b53576cd4714e5c764519a9efed6d3774`. Narrow build, actual CLI/service and packaged-MCP stdio smoke, legacy relation regression passed; normal-client limit/owner exception above.

S16-T3 Complete: [worker evidence](../../artifacts/sprint-16/task-3.md), [R1 PASS](../../artifacts/sprint-16/reviews/S16-T3-C1-R1.md), zero actionable findings; [Bronze checkpoint C1](../../artifacts/sprint-16/checkpoints/S16-T3-C1.md) committed exact reviewed snapshot as `dae708e3d9758e64c910de12be9279128a637cd0`. Narrow build and actual owner CLI/service frozen-case smoke passed: seven-kind directed multi-hop, scope non-disclosure, lifecycle/currentness, cycle/fan-out bounds and lock/agent denial. Literal-seed synthetic proof only; no semantic-model/T4 quality claim. MCP unaffected independently verified; no inherited T2 exception. S16-T4–T6 unlaunched; no push or publication.

S16-T4 Complete: [worker evidence](../../artifacts/sprint-16/task-4.md), [R1 PASS](../../artifacts/sprint-16/reviews/S16-T4-C1-R1.md), zero actionable findings; [Bronze checkpoint C1](../../artifacts/sprint-16/checkpoints/S16-T4-C1.md) committed exact reviewed snapshot as `e7e0433aad2f221f114aeb9938b1875d2e973236`. Narrow build, actual owner CLI/service ranking/explanations smoke and T3 regression passed. Frozen synthetic why-case quality and resource observations are disclosed in worker evidence, not real-corpus/semantic-model proof or release thresholds. MCP/browser unchanged; no T5 launch or publication.
