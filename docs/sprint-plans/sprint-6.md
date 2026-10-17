# Sprint 6 — Cấu trúc hóa, synthesis và chia sẻ có kiểm soát

## Sprint Goal

Cấu trúc hóa code/research/trend theo yêu cầu, tổng hợp phân tầng có bằng chứng và xuất bản liên vùng do owner kiểm soát, không thay suy nghĩ của user bằng kết luận AI.

## Status and Dependencies

- Status: In progress — S6-T1 complete with accepted evidence review and exact-snapshot checkpoint `74e2b1f0`; S6-T2 in progress; S6-T3–T7 not started. Owner authorizes necessary provider calls for assigned work without repeated per-call confirmation, using the existing provider/model and synthetic or explicitly approved source scope under S6-T1 consent/egress/cost controls. This does not authorize new private-session access, secret disclosure, provider changes, or push.
- [Global plan](../PLAN.md), [specification](../META-BRAIN.md), [requirements](../REQUIREMENTS.md).
- Requirements: R01, R07, R08, R09, R13, R20, R21, R22; bảo toàn R03–R06, R12, R14, R19, R30, R36.

## Downstream Contracts

Jobs dùng encrypted source/proposals/revision/lineage chung và review UI; không second truth store. Scope snapshot/provider consent kiểm tra trước mỗi source access/provider send/commit; lock/revoke cancel và chặn bước tiếp. Key/token không vào model. Already-sent provider context không thu hồi; declared agent provider không là chứng cứ kiểm soát onward egress ngoài ứng dụng.

## Atomic Tasks

Status legend: [ ] pending / [~] in progress / [x] done. OMP role selection, evidence review, record ownership, and exact-snapshot checkpoints follow the [canonical execution contract](../PLAN.md#planning-and-execution-contract). Worker artifact for S6-T<M>: `artifacts/sprint-6/task-<M>.md`; reviewers and checkpoint executors own their separate records under the sprint artifact tree.

S6-T1 workflow: full route; owner-selected Gold implementation, independent evidence review, then separate Bronze-only exact-snapshot checkpoint on `main` before S6-T2. Worker has no commit permission; checkpoint includes only reviewed S6-T1-owned scope, with no push. Required evidence and checkpoint are accepted; final Main status-only plan updates follow the product checkpoint.

S6-T2 workflow: full route; owner-selected Gold implementation and same-task corrections, independent evidence review, then separate Bronze-only exact-snapshot checkpoint on `main` before S6-T3. Worker has no commit permission; checkpoint is limited to reviewed task-owned scope and explicitly owned Main plan updates, preserving unrelated changes. Actual model evidence and owner source/output comparison are required; no new provider send or private-session read is authorized by role selection.

- [x] **S6-T1 — Scoped model job execution và provider controls.** Requirements: R07, R09, R22.
    - Scope: bounded job lifecycle, provider adapter/configuration, consent/cost limit và validated output boundary; không định nghĩa nhiều profile ở đây.
    - Acceptance: exact provider/model/pricing, whole-job 10k input/2k output, $0.25/job và $5/rolling30days pre-send caps; unknown cost/context deny. Input/output scope và status thật, no shell/grant/key authority. Lock/revoke/cancel/fail/restart chặn send/commit tiếp, không source loss/fake success; encrypted job/temp/output, no raw key/model context.
    - Evidence: actual owner-consented provider call trên synthetic/non-sensitive note, numeric usage/cost/reservation, denied/unknown egress, malformed/timeout/cancel/lock/restart; no fake summary fallback hoặc kế thừa historical test-key allowance.

- [~] **S6-T2 — Coding-session structuring profile.** Requirements: R12, R20, R22.
    - Scope: normalized source spans → proposals về problem/attempt/result/decision/lesson/applicability.
    - Acceptance: assistant claim khác observed tool result; không nói deployed vì test pass; giữ điều kiện/commit/version khi biết và unknown khi không; mỗi proposition quan trọng có đoạn nguồn; output chưa verified không nâng trạng thái tự động.
    - Evidence: chạy real model trên session được phép có thử thất bại rồi thành công; owner review đối chiếu nguồn, ghi lỗi nghĩa và sửa profile nếu cần; output injection không có authority.
    - Current status: [R3 FAIL](../../artifacts/sprint-6/reviews/S6-T2-S6T2-REVIEW-20261017-R3.md) verified corrected live grounding/attribution and snapshot lifecycle but requires owner comparison and fixture alignment. Fixture correction and local smoke are recorded in [worker evidence](../../artifacts/sprint-6/task-2.md); owner explicitly deferred reviewing [authentic comparison v3](../../artifacts/sprint-6/s6-t2-owner-review-packet-v3.md) and may paste a verdict later. Fresh evidence PASS and Bronze checkpoint remain pending; no task commit/push or S6-T3 launch.

- [ ] **S6-T3 — Research và trend structuring profiles.** Requirements: R12, R19, R20.
    - Scope: text notes → source claims/limitations/user hypotheses/questions/observations/intention proposals; không crawler.
    - Acceptance: paper claim không thành project result; câu hỏi user không thành belief; URL không thành bài đã đọc; trend giới hạn corpus, nhiều bản nhắc cùng nguồn không thành nhiều xác nhận; provenance và thời điểm/độ chính xác giữ đúng.
    - Evidence: real model + UI review ghi chú Việt–Anh có mixed voices, mâu thuẫn và duplicate citation; ghi nguyên văn input vẫn còn khi job fail.

- [ ] **S6-T4 — Explicit bounded synthesis nhiều tầng.** Requirements: R13, R21.
    - Scope: user chọn nguồn/items và mục tiêu, tạo synthesis proposal L2/L3+; không tự tìm agenda hoặc viết lại toàn vault.
    - Acceptance: đa nguồn, trace xuống nguồn gốc được phép, điều kiện/ngoại lệ/unknown/conflict hiện rõ; không infer độ đúng từ level hoặc read count; giữ phiên bản và lineage; nguồn đổi khiến output cần review, không tự sửa conclusion của user.
    - Evidence: tổng hợp qua hai projects được cấp rồi tổng hợp tầng cao hơn; compare source grounding, change/revoke một nguồn giữa job, cycle rejection và stale marking đúng.

- [ ] **S6-T5 — Owner declassification và publish liên vùng.** Requirements: R06, R08, R13.
    - Scope: publish reviewed derivative sang zone đích bằng owner channel; không chỉ copy ACL hoặc gắn tag shared.
    - Acceptance: default synthesis access restrictive theo sources; owner preview body/title/metadata/citations/names, explicit publication và encrypted private lineage. Catalog publication riêng không mở body, shared lesson mới không tự vào grant snapshot cũ; raw private source/backlinks vẫn deny.
    - Evidence: A+B private → owner-reviewed shared lesson → agent chỉ có shared đọc được bản an toàn, không mở source; request agent tự khai owner/user intent bị chặn; sửa nguồn đánh dấu derivative cần xem lại.

- [ ] **S6-T6 — Owner job/review workflow và automation opt-in.** Requirements: R01, R07, R21, R22, R30.
    - Scope: UI chạy từng note/batch, job status/cancel, model/egress settings và lịch tổng hợp được user bật; dùng review UI S5.
    - Acceptance: note save không gọi LLM; opt-in bật/tắt lịch đúng, chỉ chạy private jobs khi unlocked; locked paused không autonomous unlock/burst catch-up ngoài policy. Encrypted outputs/uncertainty/provenance còn khi publish; không spam/auto-resolve/belief change.
    - Evidence: UI capture → structure → compare → publish/reject → cancel, restart lịch opt-in và disable; quan sát network/provider calls để chứng minh không egress ngoài lựa chọn.

- [ ] **S6-T7 — Adversarial knowledge/authority boundary validation.** Requirements: R01, R07, R08, R09, R12, R20.
    - Scope: harden đường nguồn/model/UI/MCP đã có trước việc dùng synthesis thật; không thêm generic security framework.
    - Acceptance: hostile source/model/request không unlock, mint/redeem thêm scope, sửa grant, public catalog/body, declassify/purge hoặc fake owner confirmation. Shared application validators chặn tác động, không dựa chỉ prompt; không claim ngăn shell của external same-owner agent, LLM never influenced hoặc thu hồi context đã gửi.
    - Evidence: actual provider và deterministic hostile-output scenarios, cả UI review lẫn agent read; test regression bảo vệ authority transition, không assert prompt wording. Không claim LLM không bao giờ bị ảnh hưởng nội dung; chứng minh quyền/tác động bị chặn.

## Sprint Acceptance Gate

Coding lesson/research question/trend note qua actual provider, encrypted proposals, actual UI review và multi-level synthesis grounded. Cross-zone restrictive, catalog/body publication riêng, user-approved new token scope; no key/token provider disclosure. Egress pre-send caps và lock/revoke/injection negatives thật; no autonomous agenda và no OS-isolation claim.

## Notes / Blockers

Cần provider/local model thực khả dụng và user egress consent. Không có chúng thì task phụ thuộc bị blocked, không thay bằng canned response. Model chất lượng kém là finding phải giải quyết bằng profile/provider evidence, không hạ chuẩn provenance. Unknown claims không được auto-publish dưới nhãn verified.

## Gate Record

S6-T1 accepted: [worker evidence](../../artifacts/sprint-6/task-1.md), [final review R6 PASS](../../artifacts/sprint-6/reviews/S6-T1-S6T1-REVIEW-20261017-R6.md), and [Bronze checkpoint](../../artifacts/sprint-6/checkpoints/S6-T1-S6T1-CHECKPOINT-20261017-C1.md), commit `74e2b1f03c37b9c8f42728b41b35290e1bd67157`. Authentic same-job/proposal/source/ledger readback across restart observed; earlier failed attempts remain historical. Nonblocking limits remain in the review: live runner exited on a subsequently corrected source-read assertion despite observed readbacks, and conservative ledger accounting is not invoice reconciliation. No push, further provider send or S6-T2 launch; other Sprint 6 task gates remain pending.
