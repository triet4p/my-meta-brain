# Meta Brain — Global Project Plan

## Workflow Maintenance

[Sprint 9 — Workflow Rule Alignment](sprint-plans/sprint-9.md) complete: ten-document evidence, owner-authorized checkpoint `3622d8f6`, and [final differential review](../artifacts/sprint-9/reviews/workflow-final-deep-3.md) PASS. Replacement product Sprint 1, future Sprints 2–8, security requirements, owner consent and budgets unchanged. No push.

## Overview

Meta Brain là bộ nhớ thứ hai local-first cho owner, encrypted personal vault với key user kiểm soát. Agents chạy bình thường, khám phá owner-published catalog, request memory và đổi token một lần lấy scoped session qua MCP; không sandbox hoặc bảo mật lại original agent sessions. Provenance, durability, owner control và selective synthesis giữ nguyên.

- Baseline: 2026-09-28; owner-approved CR01/D016 ngày 2026-10-01 thay security model. Replacement Sprint 1/M1 complete: S1-T1 retained, S1-T2–T9 evidence/checkpoints PASS, documentation correction PASS/checkpoint `911c12a0f157e7d176587528a0a32b52c8345407`, final differential deep `S1-DEEP-20261007-A2` PASS with no actionable findings. Walkthrough consent does not change product/S6/secret policies. Sprint 2–8 Not started; old AppContainer PASS historical only, no production acceptance or push.
- Đặc tả: [META-BRAIN](META-BRAIN.md).
- Hợp đồng yêu cầu và change control: [REQUIREMENTS](REQUIREMENTS.md).
- Hướng dẫn agent: [AGENTS](../AGENTS.md).
- Quyết định đã khóa: [Decision log](../.agents/memory/decisions.md).
- Repository: [triet4p/my-meta-brain](https://github.com/triet4p/my-meta-brain); assigned project repository là Meta Brain, target branch `main` / remote `origin`. Brief phải nêu explicit root/ref, không dùng home/ancestor repository ngầm định; mỗi future independently gated project task có reviewed-snapshot commit riêng.
- Supplemental protected deepseek-flash proof superseded/stopped, không có provider inference được nghiệm thu; unknown prior $0.0054 reservation giữ nguyên. Cleanup không đọc key/profile/ledger, gọi provider, build native code, provision/install Windows service, hoặc cấu hình AppContainer; chỉ tạo ACL cho local synthetic fixture trong service/CLI smoke. New model execution thuộc S6-T1 sau consent, pricing và frozen budgets; không production acceptance hiện tại.

## Milestones

Mỗi chặng có một sprint plan chi tiết. Hoàn thành chặng không đồng nghĩa sản phẩm đã sẵn sàng daily production; release cuối cần tất cả chặng. Các contract phía sau phải được xem khi thực hiện phía trước, nhưng không xây trước chức năng ngoài task.

| Milestone | Kết quả bắt buộc | Phụ thuộc | Sprint |
| --- | --- | --- | --- |
| [x] M1 — Encrypted vault và scoped sharing | User key/unlock/lock; ciphertext/tamper proof; approved catalog/request review; single-use token/session/scope/revoke thật trên normal Codex/OMP | Không; CR01 replacement complete | [Sprint 1](sprint-plans/sprint-1.md) |
| [ ] M2 — Durable encrypted core | Item/source/zone/revision/relations/lifecycle canonical mã hóa; protected index rebuild/crash recovery | Replacement M1 | [Sprint 2](sprint-plans/sprint-2.md) |
| [ ] M3 — Faithful encrypted capture | Read-only allowlisted Codex/OMP sources, encrypted import/cache/snapshot và lock-aware refresh | M2 | [Sprint 3](sprint-plans/sprint-3.md) |
| [ ] M4 — Scoped agent memory access | Catalog/request/token + retrieval/evidence/brief/proposal/link qua MCP clients bình thường | M3 | [Sprint 4](sprint-plans/sprint-4.md) |
| [ ] M5 — Owner workbench | Unlock/recovery UI, library/review/relations, catalog publication/request approval/token/session console | M4 | [Sprint 5](sprint-plans/sprint-5.md) |
| [ ] M6 — Grounded structuring | Profile code/research/trend, synthesis phân tầng, provider controls và share có owner duyệt | M5 | [Sprint 6](sprint-plans/sprint-6.md) |
| [ ] M7 — Time and lifecycle | Intention, temporal recall, review-after, forgetting, archive và purge đúng lineage | M6 | [Sprint 7](sprint-plans/sprint-7.md) |
| [ ] M8 — Daily production | Install startup locked, encrypted backup/recovery/restore/export/migration, CR01 security replay và owner acceptance | M7 | [Sprint 8](sprint-plans/sprint-8.md) |

## Active Sprints

Sprint 1 replacement complete. Accepted independent checkpoints: S1-T2 `db8747580093ddb9aa709ac578a2fa1ec045cf7b`, S1-T3 `f118ea1b1832f5325b896ef86b2ed2d155ed3469`, S1-T4 `1ede35e2a908a090aeb43e94621ba7a8f9293c70`, S1-T5 `15c97faa91e699ffad527a1018579238e4d0f526`, S1-T6 `83b92fa65d7f51f7c46fba52df783054fb757d93`, S1-T7 `f89ed6067d649ec8d0a30398b4e23e77cc045ab3`, S1-T8 `db61a08b0d7cd1b8e3b679cc7c0f4a698dc70a9f`, S1-T9 `127ef6ba4dcef3405cdd6f34474ac7da57915cc6`, separate doc-status correction `911c12a0f157e7d176587528a0a32b52c8345407`. T1 retained; S1-CLEAN historical PASS. [Final differential deep A2](../artifacts/sprint-1/reviews/sprint-1-S1-DEEP-20261007-A2.md) PASS, no actionable findings/material questions. [Gate record](sprint-plans/sprint-1.md#resumed-continuation--s1-t9). No Sprint 2 or push.

S1-T9 [behavioral evidence A3](../artifacts/sprint-1/reviews/S1-T9-S1T9-REVIEW-20261007-A3.md) and [checkpoint](../artifacts/sprint-1/checkpoints/S1-T9-S1T9-CHECKPOINT-20261007-A1.md) accepted actual body consumption and both clients' revoke/fresh-lock denial without default plaintext resource files. Product status is synchronized through [doc evidence A5](../artifacts/sprint-1/reviews/S1-T9-S1T9-REVIEW-DOCSTATUS-20261007-A5.md) and [separate doc checkpoint](../artifacts/sprint-1/checkpoints/S1-T9-S1T9-CHECKPOINT-DOCSTATUS-20261007-A2.md). Final deep A2 PASS; no active flow attempt/advisor/timer or commit_pending task. Main plans remain outside product commits. Historical incident, unknown billed spend/provider effort, listener-wedge mechanism, OMP identifier and T8 environment limits remain documented; no production/S6/security-policy relaxation.

| Sprint | Trạng thái | Task range | Gate |
| --- | --- | --- | --- |
| 1 | Complete — CR01 replacement M1 | S1-T1 retained; S1-T2–T9 [x] and doc correction committed | Task evidence/checkpoints PASS; final differential deep A2 PASS |
| 2 | Not started | S2-T1…S2-T7 | Pending |
| 3 | Not started | S3-T1…S3-T6 | Pending |
| 4 | Not started | S4-T1…S4-T7 | Pending |
| 5 | Not started | S5-T1…S5-T7 | Pending |
| 6 | Not started | S6-T1…S6-T7 | Pending |
| 7 | Not started | S7-T1…S7-T6 | Pending |
| 8 | Not started | S8-T1…S8-T7 | Pending |

## Completed Sprints

Chưa có sprint hoàn tất theo CR01. Old-baseline Sprint 1 bảy task gates và agent://Sprint1DeepGateFinal PASS trong synthetic fixtures được giữ làm lịch sử ở [Sprint 1](sprint-plans/sprint-1.md); không xóa evidence hoặc claim crypto/catalog/token PASS.

## Planning and Execution Contract

- Yêu cầu trong REQUIREMENTS là baseline cứng. Acceptance của chặng không được giảm để làm task dễ hơn.
- Task tuần tự trong sprint; sprint tuần tự theo bảng dependencies. Có thể sửa cách phân rã nếu giữ nguyên outcome, có rationale và không bỏ scope. Thay requirement phải theo change control.
- Mỗi task tập trung một concern quan sát được, có requirement IDs, acceptance và evidence riêng; số file thay đổi không quyết định tính atomic.
- Đọc trước downstream contracts của sprint; giữ interface versioning, identity, provenance và migration ngay từ đầu. Không cần triển khai tất cả backend/provider tương lai.
- Worker đọc implement-atomic-task, thực hiện một task, smoke đúng surface và lưu artifacts/sprint-<N>/task-<M>.md. Artifact được tạo khi có thực nghiệm, không tạo trước báo cáo pass.
- Trong OMP, global `omp-subagent-flows` hiện hành là contract: Main chỉ planning/gate/orchestration ở exact current model. Mỗi task/correction có fresh async worker với explicit `bronze-task`, `silver-task` hoặc `gold-task`; không dùng generic `task`/obsolete `hard-task`. Bronze cho procedure/docs/verification đã biết; Silver cho bounded logic/diagnosis có clear rules và positive fit; Gold là general-purpose reasoning/integration, kể cả medium tasks hoặc khi Silver/Gold chưa rõ. Không mandatory tier ladder hoặc escalation chỉ vì thời gian.
- Trước mỗi launch resolve lại model/effort từ effective user/project definition của role được chọn; kiểm tra `blocking: false` và async enabled cho Bronze/Silver/Gold, evidence/deep reviewer và checkpoint Bronze. Dispatch explicit `agent`, không normal per-call `model` override; ghi resolved selector/effort như lịch sử, không pin từ attempt cũ. Nếu blocking/unavailable/inline fallback, dừng báo owner, không tự sửa configuration. Tối đa hai active subagents, một implementation task và một flow agent mỗi call; không dùng flow ngoài OMP.
- Evidence-reviewer riêng sau từng completed task, actionable findings cần fresh correction và fresh review cùng level. Main chỉ [x] sau evidence PASS và successful exact-snapshot commit; commit chưa xác nhận/lỗi là commit_pending, giữ [~] và chặn task độc lập tiếp theo.
- Per-task review and checkpoint ownership follow the current global skill, which supersedes obsolete routing summaries in local documents: the evidence reviewer owns the frozen manifest; only Main may authorize a fresh explicit `bronze-task` checkpoint-only executor after matching PASS. Silver/Gold never execute checkpoints. Main does not inspect implementation details or full worker histories, run Git, or write worker, evidence, review, log, or checkpoint records.
- The executor verifies the explicitly assigned repository/ref/base, real index/worktree, and path ownership, performs only the authorized standard Git checkpoint with hooks/signing, and owns post-commit evidence. Main records only concise plan/status links. Mixed ownership, drift, or uncertain scope remains unresolved; no push is requested.
- Deep-reviewer chỉ cuối sprint sau mọi task gate và required checkpoint PASS, differential trên established evidence/commit records. Deep correction reopen affected task, fresh worker/review và separate new commit trước fresh deep review; không amend. Sprint complete cần không findings/commit_pending, status và artifacts khớp evidence, mọi relevant jobs completed/cancelled.
- Main track task/attempt/agent/job IDs, chỉ matching completion được dùng làm gate; ignore cancelled/replaced stale events. Supervision qua delivered events, functions.wait chỉ khi blocked; không poll/eval barriers. Một adaptive finite 10–30 phút shell timer mỗi attempt, một outstanding correlated progress_request; completion cancel/invalidate timer, reply mới rearm, không timeout-based escalation. Interrupt không tự cancel detached worker.
- Task-advisor qualification, correlation/deduplication, and lifecycle follow the current global OMP flow, including the effective non-blocking/async check. The project default remains `opencode-go/deepseek-v4.1-flash:max`; any GPT-6.1 Sol routing override requires explicit owner approval scoped to that consultation. No persistent routing/depth changes; advice is not verification or a gate.
- Mọi subagent enumerate/read toàn bộ regular files trực tiếp trong `~/.agents/rules/`; báo conflict/inaccessible. Nếu dự kiến quá 5 phút, chủ động báo Main task ID, attempt ID, stage, lý do. Không prompt-level fixed request/elapsed-time/token cutoff; Main quyết định continuation/replacement từ evidence, runtime/provider/context limits không phải success. Reassess sau khoảng 4–5 matching progress exchanges và sau hai Silver attempts chưa đạt acceptance; không tự reroute.
- Trước deliberate replacement, outgoing agent phải lưu detailed durable attempt-scoped handoff trong record do mình sở hữu và gửi matching acknowledgment/path/section trước khi kết thúc hoặc cancel. Successor nhận tất cả related task/attempt/runtime-agent IDs, record/history references và explicit read authorization; phải đọc handoff/relevant history, kiểm tra current state, giữ rejected approaches/evidence và báo access gaps. Already-ended/forced-stop không thể cung cấp handoff cần explicit owner-authorized recovery; không bịa hoặc silently bypass. Main vẫn summary-only.
- Brief/plan ghi task family, exact role/tier, dispatch-time resolved selector/effort, selection reason, relevant history refs và quota observation hoặc `unknown`; requested effort không chứng minh provider-applied effort. `implement-atomic-task` cùng template hiện hành là common implementation/verification/artifact/handoff procedure, không tự chọn tier hoặc thay review/checkpoint gates.
- Workflow sync này không thay requirements/acceptance/task order và không chạy implementation hoặc product/provider proof. Historical blocking/PASS-only gates giữ nguyên, không retroactively chứng minh async hoặc commit checkpoints; user-global configuration exception trong skill không áp cho project tasks.
- Non-trivial implementation phải chạy scenario thật, không chỉ unit test. UI cần tương tác bề mặt thật; security cần negative scenarios qua runtime thật. Permanent tests chỉ giữ các behavior/invariant có nguy cơ regression.
- Sau implementation cập nhật docs/changelog đúng phạm vi; documentation-only không product changelog. Owner đã retired graph maintenance: không searches/checks/absence checks/update, overrides stale skill/agent instructions. Không commit private artifacts/secrets/transcripts/vault.
- Không thêm changelog entry cho lần tạo tài liệu planning này: chưa có code/user-visible runtime change.

## Decisions and Remaining Implementation Evidence

Owner-approved [D016/CR01](../.agents/memory/decisions.md) thay isolation bằng encrypted vault/scoped sharing; [retained S1-T1 targets](sprint-plans/sprint-1.md#consent-safe-dataset-and-frozen-targets) không đổi. Package/crypto/format/index decisions cần evidence khi thực thi, không research-only claim security.

| Decision | Current selection and remaining proof | Verification owner |
| --- | --- | --- |
| D-OPEN-1 — Language, packaging, UI host, IPC | Retain C#/.NET 10, WPF, versioned local pipes, self-contained win-x64; normal official Codex/OMP connectors. Non-admin app/service lifecycle không cần agent sandbox/SCM identity proof; actual packaging/UI vẫn pending. | S1-T9, S4-T1, S5-T1, S8-T1 |
| D-OPEN-2 — Crypto/key/lock and token trust boundary | Standard AEAD/KDF/envelope chọn theo approved packages; user key/recovery, startup locked, no key agent. Grant snapshot + atomic single-use redemption/session checks, not OS identities. | S1-T2…S1-T6, S8-T2 |
| D-OPEN-3 — Encrypted canonical write/index protocol | File-first ciphertext, revision/atomic/crash/migration; private SQLite DB/WAL/temp protection chọn bằng evidence, no plaintext or dual truth. | S2-T2, S2-T7, S8-T3 |
| D-OPEN-4 — Search/embedding choice | Dữ liệu Việt–Anh, symbol search và paraphrase trong giới hạn tài nguyên; không vector DB mặc định vì thói quen | S4-T2, tuning S8-T5 |
| D-OPEN-5 — Model execution | Provider/local-model khả dụng do owner chọn; consent/egress, cost cap, lỗi không mất note | S6-T1 |
| D-OPEN-6 — Operational envelope | Synthetic sizes and numeric targets remain frozen. An owner-permitted 6-document sample (79,483 bytes) is documented, but is not a statistically representative or owner-judged holdout; no product measurements exist. S8-T5 must measure against the unchanged targets. | S8-T5 |

Giới hạn vận hành được khóa trước tuning, không nâng ngưỡng sau một check thất bại để đạt pass. Nếu cần thay, ghi evidence và xin owner phê duyệt.

## Cross-Sprint Risks

- Same-owner OS compromise không thuộc CR01: vault đang unlock có thể bị lấy key/plaintext/token hoặc UI control. Không che giới hạn bằng encryption/token/sandbox claims; đã-shared copies không thu hồi.
- Catalog description cũng là disclosure: explicit owner publication, không private index/title/citation/count/embedding tự động. Request/reason không permission.
- Token concurrency/crash hoặc dynamic scope có thể mở quyền: durable consume/generation, IDs/revisions snapshot, lock/revoke/restart/session tests ở S1 rồi replay S8; bearer theft không OS-bound identity.
- Crypto key loss/wrong key/backup mismatch: fail closed, user recovery và key-format/backup compatibility; không reset để né loss/migration. Plaintext temp/index/journal là privacy bug, không chỉ canonical encryption.
- Session format/retention đổi: read-only allowlist, source availability, encrypted copies; không sửa original hoặc mở private background reads khi locked.
- Summary drift/user belief: epistemic provenance và owner review giữ xuyên suốt; cross-zone synthesis/publication không tự mở new read grants.
- UI approval/key management không dùng agent payload hoặc same-owner SID làm user consent; core/policy chung. Actual app API enforcement không ngăn same-owner OS agent giả owner qua host compromise.
- Yêu cầu “production” bị thu hẹp thành demo: tất cả milestone bắt buộc; không có release completion trước Sprint 8 và owner acceptance.

## Backlog / Future Work

Không thuộc completion criteria baseline: multi-device sync, multi-user collaboration/SaaS, crawler chủ động, PDF/web automation, graph database, agent tự nghiên cứu liên tục, marketplace và benchmark công khai. Chỉ đưa vào kế hoạch bằng change request có rationale; không lấy chúng thay thế mục bắt buộc.

## Completion Definition

Toàn bộ R01–R40 theo CR01 có actual implementation/evidence/gates và owner acceptance; capture/recall/link/synthesis/time/purge/recovery hữu ích, encrypted persistence/key/lock/catalog/request/token/scope/disclosure đúng. Hai normal Codex/OMP clients dùng workflow thật. Không OS sandbox promise, old fixture PASS thay acceptance mới, fake recovery hoặc new crypto proof từ documentation.

## Repository Delivery Authorization — 2026-10-07

Owner accepts the four documented Sprint 1 nonblocking limits for now, without removing their records or changing product/security/S6 requirements, and explicitly authorizes delivery: inspect every remaining tracked/staged/untracked path, review and commit all eligible source/config/documentation changes, then push existing and new authorized commits to `origin/main` and verify remote equality plus a clean worktree. Vault, secrets, raw sessions, artifacts and generated/build outputs remain outside Git and must be preserved, not deleted or committed to manufacture cleanliness. Ordinary explicit-path commits/push only; no force, history rewrite, blanket staging or unrelated cleanup. Main-owned final plans are included in this owner-authorized delivery scope, unlike the earlier product checkpoints. Investigation, frozen review/checkpoint and actual push/cleanliness observations belong to the role-owned [delivery record](../artifacts/sprint-1/delivery-20261007.md); this paragraph records authorization, not an unobserved success claim. Sprint 2 remains unstarted.
