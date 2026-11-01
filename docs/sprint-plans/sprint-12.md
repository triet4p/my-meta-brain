# Sprint 12 — Encrypted original-format workspace

## Goal và trạng thái

- Status: **In progress — S12-T1–T5 complete; S12-T6 candidate preparation active**. Release mục tiêu cuối sprint: **1.2.0**; chưa build/publish release candidate.
- Entry: Sprint 11 complete và 1.1.0 published.
- Scope/trace: P2-02, P2-11, P2-12; R04, R10, R18, R33–R36, R39. Các P2 là candidates, không thay baseline khi chưa có approval.
- Root: `F:/ai-ml/my-meta-brain`; target ref `main`; exact base/worktree/ownership resolve khi execution.
- Contract chung: [roadmap 2.0](../ROADMAP-2.0.md), [direction](../POST-1.0-DIRECTION.md), [requirements](../REQUIREMENTS.md), [PLAN](../PLAN.md).

- Owner-approved CR03/D089 (2026-11-01): từ 1.2.0 chỉ tiếp tục CLI + MCP; WPF 1.1.0 là legacy frozen, không UI installer mới. CLI phải bao phủ mọi owner workflow, browser không là prerequisite. Browser document preview/edit qua `metabrain open` thuộc Sprint 13, không phải workspace web console.

## Execution và gates

Full OMP route; task tuần tự T1 → T6. Mỗi task cần worker evidence, independent evidence-review PASS và Bronze exact-snapshot checkpoint trước task độc lập kế tiếp. Cuối sprint assigned differential deep review trước owner-authorized publication; publication smoke trước Complete. Current global flow điều khiển async lifecycle và review rounds. Main không implement/Git hoặc viết evidence records.

Owner authorized bắt đầu Sprint 12 và auto-routing Bronze/Silver/Gold cho S12-T1–T6 cùng correction trong sprint. Full route giữ independent evidence review và Bronze exact-snapshot commit checkpoint từng task, differential cuối sprint. Storage/key/disclosure changes vẫn cần owner approval trước writes mới; không authorize push/tag/upload/deploy/provider access. Tối đa hai active agents, một implementation task. Worker đọc implement-atomic-task và mọi regular file trực tiếp dưới ~/.agents/rules/. Artifacts tại `artifacts/sprint-12/task-<M>.md`, reviews/checkpoints dưới cùng sprint; không tạo trước PASS giả.

Owner authorized checkpoint cho mọi task Sprint 12, không hỏi lại; bao gồm prior-planning content trong đúng snapshot 8 file T1 đã review. Mọi checkpoint vẫn cần matching evidence PASS, exact snapshot/ownership verification và Bronze executor riêng; không mở rộng sang unrelated changes hoặc push/publication.

Không host mutation/ACL/permissions/profile edits; synthetic data mặc định. Không downgrade baseline security, reset data hoặc lấy test suite thay runtime smoke. Decision còn mở/contract thay đổi phải owner-approved và sync docs trước implementation affected path.

**Approved implementation contract:** [D090](../META-BRAIN.md#134-approved-workspace-contract--d090) governs T2–T6, including ordinal case-sensitive exact-scalar sibling equality, destination filesystem collision refusal, separate content/tree preconditions, encrypted tombstone/purge/backup recovery, and fail-closed mixed installer and wire/version transition. This decision is not a task gate PASS, implementation, migration, host action or publication authorization. T5's retained workflow inventory and bounded gaps are in [T1 evidence](../../artifacts/sprint-12/task-1.md#legacy-owner-workflow-coverage-map); command dispatch is not interactive/runtime parity. In particular T5 must close secure credential prompting (the existing `--from-env` path does not satisfy the no-env/plaintext credential condition), root/pipe selection, revision-pinned review, scope/token handoff, backup/purge and complete new tree operations without WPF/browser/config edits. T4 owns original-byte export; S16 owns later workspace relation evolution.

## Atomic tasks

Mỗi task sau phụ thuộc toàn bộ task trước; release task theo [release contract](../ROADMAP-2.0.md#7-definition-of-release-mỗi-sprint). Status `[ ]` pending; `[~]` active/blocked; `[x]` chỉ sau acceptance và gates.

- [x] **S12-T1 — Chốt workspace/key/format contract.**
    - Change: So sánh reuse vault với master-key hierarchy, file/folder identity, metadata confidentiality, schema version và owner-authored metadata storage. Chốt copy/move/replacement/conflict, deletion và recovery semantics; review dependencies theo package rules. Lập mapping toàn bộ owner workflows legacy sang CLI và chốt transition installed WPF/shared-runtime trước 1.2.0.
    - Acceptance: Owner duyệt storage/key/disclosure changes trước writes mới; decision/spec/requirements đồng bộ, master key không tới agents, không custom crypto/second key store vô cớ. Legacy vault không auto-migrate và chỉ có một authoritative owner cho mỗi tài liệu. Compatibility plan không hứa WPF 1.1.0 dùng runtime/data mới; mismatch phải refusal/remediation thật, không silent replacement hay xóa component/data.
    - Check: Exercise changed CLI/service và MCP paths; không browser/WPF prerequisite trước S13, trừ installed legacy-component transition. Ghi scenarios/observations/limits, không claim surface chưa chạy.
    - Owner approved D1–D6; synchronized D090. [Evidence R1 PASS](../../artifacts/sprint-12/reviews/S12-T1-C1-R1.md), [checkpoint PASS](../../artifacts/sprint-12/checkpoints/S12-T1-C1.md), commit `f39d41bb31daa6a21c60d7653a3b7a3419b4ccf5`. Documentation-only; no persistence/runtime PASS claimed.

- [x] **S12-T2 — Encrypted file và folder persistence.**
    - Change: Implement thư mục logic và file revision original bytes qua approved encrypted format/shared key lifecycle; atomic writes, conflict và crash recovery.
    - Acceptance: Markdown/CSV/JSONL/text cùng opaque binary giữ bytes/encoding sau restart; private tên/path/metadata/scratch không plaintext; wrong key/tamper/truncation từ chối, saved data không mất.
    - Check: Exercise changed CLI/service và MCP paths; không browser/WPF prerequisite trước S13, trừ installed legacy-component transition. Ghi scenarios/observations/limits, không claim surface chưa chạy.
    - [Evidence R2 PASS](../../artifacts/sprint-12/reviews/S12-T2-C1-R2.md), [checkpoint PASS](../../artifacts/sprint-12/checkpoints/S12-T2-C1.md), commit `d08784d56edc17f366e446bc4b7ecdad80b0a06a`. Actual CLI/service byte-fidelity, restart/lock, conflict, tamper/truncation và real-kill recovery smoke accepted; limits trong evidence.

- [x] **S12-T3 — Stable identity và revision operations.**
    - Change: Owner create/rename/move/copy/revise/delete theo semantics T1, identity tách path và citation anchors tách display.
    - Acceptance: Rename/move giữ ID/relations, copy nhận identity đúng contract; concurrent edits/replacement conflict không overwrite; lock chặn mutations. Không write ngược original Codex/OMP sessions.
    - Check: Exercise changed CLI/service và MCP paths; không browser/WPF prerequisite trước S13, trừ installed legacy-component transition. Ghi scenarios/observations/limits, không claim surface chưa chạy.
    - [Evidence R1 PASS](../../artifacts/sprint-12/reviews/S12-T3-C1-R1.md), [checkpoint PASS](../../artifacts/sprint-12/checkpoints/S12-T3-C1.md), commit `097a0867543b431794bc7e1fd589e1b8b56c8a4e`. Actual CLI/service mutation, pinned-anchor, tombstone/restore và T2-format compatibility proof accepted; no automatic format conversion.

- [x] **S12-T4 — Original read/export và portability.**
    - Change: Thêm owner read/export file/folder có preview đích và scope; metadata/relations export có format version, không nhét vào original bytes.
    - Acceptance: Hash equality cho unmodified export sau restart/unlock; modified file có revision riêng. Explicit plaintext disclosure, không temp plaintext ngầm hoặc private citations ngoài selected scope.
    - Check: Exercise changed CLI/service và MCP paths; không browser/WPF prerequisite trước S13, trừ installed legacy-component transition. Ghi scenarios/observations/limits, không claim surface chưa chạy.
    - [Evidence R1 PASS](../../artifacts/sprint-12/reviews/S12-T4-C1-R1.md), [checkpoint PASS](../../artifacts/sprint-12/checkpoints/S12-T4-C1.md), commit `d46fbacc48c42d37b9993d79b63bb84da9b24ef6`. Actual pinned file/folder export, scoped sidecar, destination/refusal và lock/restart proof accepted.

- [x] **S12-T5 — Workspace backup/restore và CLI owner coverage.**
    - Change: Nối shared encrypted backup/recovery với workspace; CLI quản lý cây/create/import/read/export/rename/move. Bổ sung mọi owner workflow còn thiếu từ mapping T1 (unlock/recovery, capture/library/source operations, proposal review, relations, jobs/lifecycle, catalog/request/token/session, backup/export/purge), không viết WPF mới.
    - Acceptance: Owner dùng folder/file thật và mọi retained workflow bằng CLI không cần browser/WPF/config edit; encrypted backup restore tái hiện bytes/identity/metadata. Lock/restart không hồi sinh sessions; credential không vào args/env/log/plaintext file. Exercise transition từ installed 1.1 CLI/UI/shared runtime với refusal hoặc explicit owner transition an toàn; không claim old UI/binary compatible với format mới.
    - Check: Exercise changed CLI/service và MCP paths; không browser/WPF prerequisite trước S13, trừ installed legacy-component transition. Ghi scenarios/observations/limits, không claim surface chưa chạy.
    - [Evidence R1 PASS](../../artifacts/sprint-12/reviews/S12-T5-C1-R1.md), [checkpoint PASS](../../artifacts/sprint-12/checkpoints/S12-T5-C1.md), commit `884dc6db58e5fb3b74a61ce00ffe1835d0fff5eb`. Backup/purge/consumed-token and real project-local installed-1.1 transition proof accepted; inherited interactive prompt lacks fresh PTY proof, as disclosed in review.

- [~] **S12-T6 — Release 1.2.0.**
    - Change: Package CLI installer + MCP ZIP, workspace opt-in, CLI parity/support-transition, key/recovery/format/export guides và upgrade/restore compatibility; ngừng tạo UI installer mới nhưng giữ nguyên downloads 1.0/1.1.
    - Acceptance: End-to-end CLI import → encrypted store → restart/unlock → original-format export/backup restore trên exact candidate; retained owner workflows CLI-complete, installed-WPF transition có evidence; release contract chung đạt.
    - Check: Exercise packaged CLI/service và MCP changed paths; browser `open` chưa shipped, không đòi WPF proof hoặc ghi browser PASS trước Sprint 13.

## Exit và release boundary

- User outcome: Owner dùng workspace và retained workflows bằng CLI độc lập; encrypted backup restore tái hiện bytes/identity/metadata, lock/restart không hồi sinh sessions; WPF legacy transition không làm hỏng installation/data.
- Tất cả task gates/checkpoints PASS, final differential không actionable findings; không commit_pending.
- Candidate chuẩn bị ở release task; review/checkpoint rồi differential trước publication. Owner authorize external actions riêng; exact public artifacts/links/version smoke hoàn tất rồi Main mới Complete. Release pending chặn sprint sau.
- CLI installer + MCP ZIP và guides tương ứng version 1.2.0; không UI installer mới. Install/upgrade/data preservation theo CR03/shared contract; không overwrite assets hoặc ship breaking data/API change ngoài approval.
- Kế thừa unchanged acceptance có trace; kiểm tra changed surfaces thực, không blanket replay 1.0 hoặc giả fresh PASS.

## Gate record

S12-T1–T5 evidence/checkpoint PASS tại các link trên. S12-T6 candidate preparation active; differential và publication chưa có gate result. Xem [roadmap](../ROADMAP-2.0.md) cho dependency tiếp theo; không tự advance bởi completion message.
