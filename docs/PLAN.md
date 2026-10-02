# Meta Brain — Global Project Plan

## Overview

Meta Brain là bộ nhớ thứ hai local-first cho owner, encrypted personal vault với key user kiểm soát. Agents chạy bình thường, khám phá owner-published catalog, request memory và đổi token một lần lấy scoped session qua MCP; không sandbox hoặc bảo mật lại original agent sessions. Provenance, durability, owner control và selective synthesis giữ nguyên.

- Baseline: 2026-09-28; owner-approved CR01/D016 ngày 2026-10-01 thay security model. S1-CLEAN source cleanup đã qua evidence gate PASS. Owner authorized full replacement Sprint 1 delivery 2026-10-02; S1-T2 active, S1-T3–T9 Pending, no replacement gate passed yet. S1-T1 stack/envelope evidence retained. Sprint 2–8 Not started; old AppContainer fixture PASS historical only.
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
| [ ] M1 — Encrypted vault và scoped sharing | User key/unlock/lock; ciphertext/tamper proof; approved catalog/request review; single-use token/session/scope/revoke thật trên normal Codex/OMP | Không; reopen CR01 | [Sprint 1](sprint-plans/sprint-1.md) |
| [ ] M2 — Durable encrypted core | Item/source/zone/revision/relations/lifecycle canonical mã hóa; protected index rebuild/crash recovery | Replacement M1 | [Sprint 2](sprint-plans/sprint-2.md) |
| [ ] M3 — Faithful encrypted capture | Read-only allowlisted Codex/OMP sources, encrypted import/cache/snapshot và lock-aware refresh | M2 | [Sprint 3](sprint-plans/sprint-3.md) |
| [ ] M4 — Scoped agent memory access | Catalog/request/token + retrieval/evidence/brief/proposal/link qua MCP clients bình thường | M3 | [Sprint 4](sprint-plans/sprint-4.md) |
| [ ] M5 — Owner workbench | Unlock/recovery UI, library/review/relations, catalog publication/request approval/token/session console | M4 | [Sprint 5](sprint-plans/sprint-5.md) |
| [ ] M6 — Grounded structuring | Profile code/research/trend, synthesis phân tầng, provider controls và share có owner duyệt | M5 | [Sprint 6](sprint-plans/sprint-6.md) |
| [ ] M7 — Time and lifecycle | Intention, temporal recall, review-after, forgetting, archive và purge đúng lineage | M6 | [Sprint 7](sprint-plans/sprint-7.md) |
| [ ] M8 — Daily production | Install startup locked, encrypted backup/recovery/restore/export/migration, CR01 security replay và owner acceptance | M7 | [Sprint 8](sprint-plans/sprint-8.md) |

## Active Sprints

Sprint 1 replacement implementation active theo owner authorization 2026-10-02; S1-T1 evidence retained. S1-CLEAN removed superseded agent/AppContainer/proof sources and passed agent://CR01CleanupEvidenceGate with focused build and real owner CLI/service smoke. S1-T2 key/unlock/lock is active; S1-T3–T9 Pending. Each independent task requires evidence PASS and an exact-snapshot commit before the next task. Historical old PASS is not encrypted-vault proof; downstream starts only after the replacement sprint gate.

| Sprint | Trạng thái | Task range | Gate |
| --- | --- | --- | --- |
| 1 | In progress — CR01 replacement implementation | S1-T1 retained; S1-T2 active; S1-T3…S1-T9 Pending | Replacement gate Pending; old PASS historical only |
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
- Trong OMP, global omp-subagent-flows là workflow hiện hành: Main planning/gate/orchestration ở exact current model; một fresh async task worker mỗi task/correction, hard-task chỉ khi có documented interacting correctness constraints/escalation. Main không triển khai sprint task.
- Trước mỗi launch kiểm tra effective user/project definitions: task, hard-task, evidence-reviewer, deep-reviewer có blocking: false và async enabled. Nếu blocking/unavailable/inline fallback, dừng báo owner, không tự sửa runtime configuration. Tối đa hai active subagents, một flow agent mỗi call; không dùng flow ngoài OMP.
- Evidence-reviewer riêng sau từng completed task, actionable findings cần fresh correction và fresh review cùng level. Main chỉ [x] sau evidence PASS và successful exact-snapshot commit; commit chưa xác nhận/lỗi là commit_pending, giữ [~] và chặn task độc lập tiếp theo.
- Commit từng independent task ngay sau PASS, không dồn cuối sprint; shared atomic batch chỉ khi boundary đã khai báo trước implementation và mọi included task PASS. Freeze base/ref/reviewed tree/diff với review reference; inspect staged/unstaged ownership từng path, chỉ wholly task-owned content khớp snapshot được commit. Mixed ownership/drift cần resolve/re-review.
- Main dùng standard scoped Git porcelain, giữ identity/signing/hooks; không broad add, synthetic index, force-add ignored artifacts, automatic stash/reset/checkout hoặc rewrite history. Xác nhận resulting ref/parent/SHA/content và remaining staged diff/status; ghi target/base/snapshot/review/commit evidence trong task artifact. Không project repository được assigned/authorized thì commit_pending, không tự init ancestor/home repository.
- Deep-reviewer chỉ cuối sprint sau mọi task gate và required checkpoint PASS, differential trên established evidence/commit records. Deep correction reopen affected task, fresh worker/review và separate new commit trước fresh deep review; không amend. Sprint complete cần không findings/commit_pending, status và artifacts khớp evidence, mọi relevant jobs completed/cancelled.
- Main track task/attempt/agent/job IDs, chỉ matching completion được dùng làm gate; ignore cancelled/replaced stale events. Supervision qua delivered events, functions.wait chỉ khi blocked; không poll/eval barriers. Một adaptive finite 10–30 phút shell timer mỗi attempt, một outstanding correlated progress_request; completion cancel/invalidate timer, reply mới rearm, không timeout-based escalation. Interrupt không tự cancel detached worker.
- Advisor consultations do Main broker, tối đa một mỗi worker và nằm trong hai active-agent slots; read-only bounded request không thay verification/gate. Kiểm tra async definition/effective model; default opencode-go/deepseek-v4.1-flash:max, GPT-6.1 Sol route cần explicit per-consultation owner approval. Resolve/cancel trước review/commit, ignore stale advice; không đổi persistent config/depth.
- Mọi subagent đọc toàn bộ regular files trực tiếp trong ~/.agents/rules/; báo conflict/inaccessible. Nếu dự kiến quá 5 phút, chủ động báo Main task ID, attempt ID, stage, lý do. Tại 250K tokens partial handoff/end attempt, không vượt 300K; replacement fresh attempt giữ artifact/findings.
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
