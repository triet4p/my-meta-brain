# Sprint 4 — Retrieval và MCP/tool interface

## Sprint Goal

Codex/OMP chạy bình thường khám phá approved catalog, yêu cầu memory, nhận user-approved token/session rồi retrieval/proposal/link qua MCP thật với scope thống nhất và citations đúng.

## Status and Dependencies

- Status: Not started — CR01 updated. Entry: Sprint 3 PASS, encrypted core/key/session APIs sẵn, owner-approved corpus và normal official clients. Không protected runtime prerequisite.
- [Global plan](../PLAN.md), [specification](../META-BRAIN.md), [requirements](../REQUIREMENTS.md).
- Requirements: R06, R15, R23, R24, R25; bảo toàn R03–R05, R07, R12, R14, R16, R26.

## Downstream Contracts

UI S5 dùng catalog/request/redeem/session và private search APIs, không query index/direct decrypt. Catalog chỉ metadata được publish; brief/private cache gắn grant IDs/revisions/generation. Agent/cloud disclosure theo owner-reviewed provider context trước trả memory; ứng dụng không kiểm soát agent gửi tiếp plaintext đã nhận. Key không tới MCP/model; no standalone search bypass.

## Atomic Tasks

Status legend: [ ] pending / [~] in progress / [x] done. OMP role selection, evidence review, record ownership, and exact-snapshot checkpoints follow the [canonical execution contract](../PLAN.md#planning-and-execution-contract). Worker artifact for S4-T<M>: `artifacts/sprint-4/task-<M>.md`; reviewers and checkpoint executors own their separate records under the sprint artifact tree.

- [ ] **S4-T1 — MCP catalog/request/token-session bridge cho Codex và OMP.** Requirements: R03–R07, R24.
    - Scope: real transport/capability dispatch, dùng S1 catalog/request/approval/redemption APIs, no key/storage/policy trong bridge.
    - Acceptance: cả hai clients bình thường initialize, discovery không private body, submit request, owner CLI approve/narrow, token local handoff/redeem và scoped calls; no owner tools/mint/unlock trên agent surface. Replay/spoof/expired/locked denies; no token/key vào argv/URI/model prompts.
    - Evidence: actual MCP initialize/calls trên official normal Codex/OMP, request/token/session/revoke flow và failed cases; không ACP/helper/mock hoặc patched AppContainer binary proof.

- [ ] **S4-T2 — Scoped retrieval với lexical/metadata và bilingual relevance.** Requirements: R06, R23, R26.
    - Scope: scope-filtered lexical/metadata/ranking/time queries trên protected private index; approved catalog search là surface riêng, no embeddings mặc định.
    - Acceptance: candidate filter theo ID/revision snapshot trước private retrieval; Việt–Anh/symbol/paraphrase/current có nghĩa; no hidden counts/facets; pagination phù hợp snapshot. Catalog query chỉ published labels/descriptions, không private ranking/snippets; lock/revoke deny cache.
    - Evidence: tình huống recall thật từ corpus được phép, negative scope queries và measured latency so với envelope; ghi search tradeoff/embedding decision, không tự chọn benchmark làm mục tiêu.

- [ ] **S4-T3 — Item detail và source expansion có quyền độc lập.** Requirements: R06, R16, R18, R23.
    - Scope: get-by-ID, citations, revision/source span rendering và availability warnings.
    - Acceptance: readable item không kéo raw/source/link ngoài scope; ID guessed không private existence; đúng approved revision/spans và missing/changed warnings; new revisions cần approve lại, không silently replace body. Internal paths không lộ vô cớ, locked deny.
    - Evidence: mở kết quả search sang source bằng owner và hai grants khác nhau; revoke giữa search và get được chặn, không dùng cached item để vượt quyền.

- [ ] **S4-T4 — Brief/context và feedback không bóp nghĩa.** Requirements: R12, R25.
    - Scope: context budget, selection/projection và feedback storage, không synthesis LLM thường trực.
    - Acceptance: brief ngắn có citations/applicability/uncertainty, dựng lại theo grant hiện tại; không thành system instructions; read count/usefulness không tăng verification; feedback wrong/stale không tự viết lại belief của owner.
    - Evidence: quay lại project, budget nhỏ, conflict/stale entries và revoke sau cache; trace rõ nội dung được chọn và phần chưa chắc.

- [ ] **S4-T5 — Proposal write interface cho agent.** Requirements: R03, R10, R12, R24.
    - Scope: propose create/revise với source references và revision preconditions, không auto-publish.
    - Acceptance: read-only token không write; agent cần owner-approved proposal operation/đích inbox riêng; preserved executor/AI origin/revision preconditions; errors không published, fake user claim không confirmed, repeated operation không duplicates.
    - Evidence: agent gửi lesson sau task thật trên fixture, revise proposal, denied zone và stale revision; owner CLI đọc đúng proposal để UI sau này dùng.

- [ ] **S4-T6 — Explicit relation commands qua hội thoại và MCP.** Requirements: R14, R15, R24.
    - Scope: resolve/list/create/remove/supersede relations trong quyền; ghi request evidence và undo phù hợp.
    - Acceptance: câu “nối ý này với research X” thực hiện khi endpoints rõ; mơ hồ không nối bừa; có quyền endpoints và quyền mutation; agent không tự khai user confirmation để share; link/relation metadata không mở raw hoặc zone khác.
    - Evidence: hội thoại thực trên cả clients, explicit related-not-supports, unlink/undo, ambiguous target và cross-zone denied scenarios. Chat interpretation không được core coi là authorization.

- [ ] **S4-T7 — Agent workflow integration và cache/revocation hardening.** Requirements: R06, R23, R24, R25.
    - Scope: discovery → request → owner approval/token handoff → scoped recall/evidence → explicitly granted proposal/link; cache/lock/revocation toàn workflow.
    - Acceptance: cả clients chạy thật; không memory thành policy; new member/revision/link không tăng scope; lock/revoke chặn next calls/counts/cache, no key/token model disclosure. Withdrawal catalog chặn serving tiếp, không xóa copies đã nhận.
    - Evidence: actual command/chat workflow và hostile direct API calls, concurrent/replayed token, different scopes và lock; redact transcripts, record actual disclosure vs unverified OS attack limits.

## Sprint Acceptance Gate

Cả Codex/OMP: approved catalog → request → user narrow/approve → local token redeem → search Việt/Anh/get/source đúng scope → separately authorized proposal/link → revoke/lock/denied. No private index trước approval hoặc existence leak qua counts/relations/citations/cache; new IDs/revisions không auto scope. No owner/key-management tools trên MCP, no OS-isolation promise.

## Notes / Blockers

Không coi MCP roots, tool description hay hidden UI button là access control. Semantic search là lựa chọn có evidence; không bỏ khả năng tìm paraphrase đã yêu cầu chỉ vì FTS dễ hơn. Không dùng cloud embeddings khi zone chưa cho egress.

## Gate Record

Pending. Chưa có MCP runtime verification.
