# AGENTS — Meta Brain

## Read First

1. [Product specification](docs/META-BRAIN.md).
2. [Binding requirements and change control](docs/REQUIREMENTS.md).
3. [Global plan](docs/PLAN.md), rồi sprint plan/task đang được giao.
4. [Architecture decision log](.agents/memory/decisions.md).
5. Relevant shared rules/skills dưới đây; đọc nội dung thật, không chỉ dựa vào tên hoặc bản tóm tắt.

Owner-approved CR01/D016 (2026-10-01) thay baseline bằng encrypted personal vault, approved catalog và single-use token/scoped sessions. S1-CLEAN đã xóa obsolete AppContainer/agent/proof source và qua evidence gate; chỉ owner-only plaintext/ACL status/read còn hoạt động, không encryption/approval/token proof. Sprint 1 replacement tasks Pending/reopened, Sprint 2–8 Not started; old fixture PASS chỉ lịch sử. Không tự implementation khi user chỉ yêu cầu tài liệu.

## Product Invariants

- Meta Brain là bộ nhớ trợ giúp owner, không autonomous thought leader. Không tự chọn agenda, đổi belief/preference, đóng câu hỏi hoặc suy diễn rằng đã nhắc nghĩa là đã làm xong.
- Owner kiểm soát key vault; application chỉ giữ key khi unlocked, không đưa key cho agents. Agents xem catalog owner công bố rồi request memory; token opaque đổi một lần lấy scoped session, không decryption key. Scope IDs/revisions/operations/expiry/egress do owner duyệt, không payload agent tự khai.
- Không sandbox Codex/OMP hoặc bảo mật lại original sessions; không claim chống agent cùng quyền OS owner lấy runtime key/plaintext/token hoặc điều khiển owner UI. Token scope được enforce tại Meta Brain APIs, không bảo vệ dữ liệu đã chia sẻ. Không mở lại AppContainer/provider proof đã superseded để làm gate mới.
- Startup locked; lock/restart vô hiệu pending tokens/live sessions, dừng private reads/ingest/jobs. Encrypt private canonical/metadata/index/cache/temp/snapshots/backup; chỉ approved catalog/minimal nonsensitive format envelope công khai. No plaintext key cạnh vault, no secret args/env/config/log/model context.
- Core, application/UI và connections tách biệt. Một domain/policy implementation; UI và MCP không ghi trực tiếp vault hoặc tự cấp quyền.
- Zone là boundary quyền, collection là tổ chức, level là mức trừu tượng. Link/membership/hierarchy không tự sinh quyền; tầng cao không tự đúng hơn.
- Giữ authorship, executor, câu hỏi/giả thuyết/tổng hợp, nguồn và mức bằng chứng riêng. AI output không được đổi nhãn thành user belief khi được lưu.
- Session .codex/.omp giữ nguyên, import read-only từ allowlist. Không scan/copy/migrate toàn bộ profile vì tiện. Snapshot chỉ opt-in; missing source phải nói rõ.
- Canonical encrypted file-first, human-readable sau unlock/explicit export; private index rebuildable, không dual truth. Revision conflict, crash recovery, key/recovery và migrations là chức năng thật, không reset dữ liệu để né cutover.
- Synthesis liên vùng restrictive mặc định; chỉ owner declassify có preview. Không đưa nội dung/metadata/citation private ra ngoài quyền.
- Local không đồng nghĩa không egress. Grant phải bao phủ provider/model context; không gọi cloud trên nguồn chưa được cho phép.
- Không MVP, mock shipping, no-op hoặc TODO thay hành vi. Mỗi chặng triển khai outcome thật, giữ downstream contracts; không xây speculative framework cho mọi khả năng tưởng tượng.

## Planning and Changes

Dùng skill `manage-plans` trong `~/.agents/skills/manage-plans/SKILL.md` khi lập/đổi kế hoạch. PLAN chỉ giữ overview/milestones/status; atomic tasks nằm trong docs/sprint-plans/sprint-<N>.md. Giữ thứ tự task và dependency, không nhảy gate vì task tiếp theo dễ hơn.

Requirement R01–R40 là baseline cứng theo CR01. Thay scope/threat model cần owner approval, append decision, impact dữ liệu/security/downstream và cập nhật specification/requirements/PLAN/sprints cùng nhau. Không tự nới budget hoặc chuyển historical PASS thành gate mới.

C#/.NET 10, WPF, versioned local named pipes và modular boundaries giữ từ D007/S1-T1; AppContainer bindings/protected launcher được D016 supersede. Crypto/KDF/encrypted format/private-index mechanism chưa chọn thư viện cụ thể, cần evidence và approved-package rules ở task phụ trách. Implementation clean cutover mọi caller/settings/tests/docs, bỏ obsolete paths/shims, giữ decision/artifact lịch sử và không xóa source/vault trái scope.

## Runtime-Specific Workflow

### Inside OMP

Đọc và thực thi skill `omp-subagent-flows` trong `~/.agents/skills/omp-subagent-flows/SKILL.md` cho planned implementation, không áp sprint gates giả cho trao đổi ý tưởng.

- Main làm planning/gate/orchestration, không tự triển khai sprint task. Planning ở current Main model, không giao agent lập top-level plan.
- Trước mỗi launch, kiểm tra effective user/project definitions của task, hard-task, evidence-reviewer và deep-reviewer: blocking: false, async execution enabled. Nếu blocking, async unavailable hoặc runtime fallback synchronous, dừng và báo owner; không tự sửa overrides/config hoặc chạy blocking thay thế.
- Một fresh async worker mỗi task/correction; default task, chỉ hard-task khi có interacting correctness constraints/escalation được ghi rõ. Không dùng scout để sửa code; không revive idle worker cho task khác.
- Brief chỉ rõ project root/repository, target branch/ref, sprint/task ID, unique attempt ID, acceptance, affected boundaries và artifact path; worker đọc implement-atomic-task. Không dùng home/ancestor repository làm target ngầm định.
- Evidence-reviewer async sau mỗi task, không batch reviews. Actionable finding phải sửa bằng fresh worker rồi fresh review cùng level; worker completion hoặc progress không phải gate PASS.
- Project task giữ [~] cho tới evidence PASS và successful exact-snapshot commit checkpoint; chỉ Main đánh [x]. Evidence PASS nhưng commit chưa xác nhận là commit_pending, chặn task độc lập kế tiếp.
- Mỗi task độc lập commit ngay sau PASS, không dồn cuối sprint. Chỉ shared atomic batch được khai báo checkpoint boundary trước implementation mới có thể dùng một commit sau mọi included task PASS; không gộp unreviewed work.
- Exact-snapshot review and ownership follow the current global OMP flow: the evidence reviewer owns the frozen manifest; only after a matching PASS does Main authorize a fresh default-task checkpoint-only executor, which verifies ownership and performs Git. The executor owns its checkpoint record; Main does not inspect implementation details or full worker histories, run Git, or write worker, evidence, review, log, or checkpoint records.
- Main records only concise plan/status links. Mixed ownership, drift, or uncertain scope remains unresolved under the canonical flow; no push is authorized here.
- Deep-reviewer async chỉ cuối sprint sau mọi evidence gate và required commit checkpoint PASS. Differential review dùng accepted evidence và commit records, không lặp passed checks. Correction reopen task, fresh worker/review, separate new commit rồi fresh deep review.
- Tối đa hai active subagents tổng cộng, một flow agent mỗi call; chỉ một sprint implementation task tại một thời điểm. Main dùng delivered completion/message events, functions.wait chỉ khi không còn việc hữu ích; không poll hoặc eval wait/barriers.
- Main record task/attempt/agent/job IDs và role; chỉ matching completed attempt được advance gate. Replacement/cancel invalidates old IDs; receipt, progress, partial handoff và stale event không completion/verdict.
- Mỗi active attempt có một finite background shell timer 10–30 phút, adaptive theo context/progress, không deadline. Completion ưu tiên và cancel/invalidate timer; timer event chỉ tạo một outstanding correlated progress_request. Sau matching reply mới rearm, không hot loop hoặc thay worker chỉ vì thời gian.
- Main interrupt không tự cancel detached worker; muốn dừng phải explicit job cancellation. Không hứa durability qua process exit hoặc tự resume sau interrupt.
- Advisor criteria, request fields/correlation, deduplication, and consultation lifecycle follow the current global OMP flow. A worker may submit one bounded, evidence-backed request for unresolved technical uncertainty even before repeated failures or a timer; Main brokers it only after confirming the effective advisor definition is non-blocking and async is available. Workers never spawn an advisor or change routing/depth, and advice is diagnostic only, not implementation evidence.
- Advisor default chính xác opencode-go/deepseek-v4.1-flash:max; effective GPT-6.1 Sol route ở bất kỳ provider/effort nào cần owner approval riêng từng consultation trước launch. Không unauthorized fallback/config edits; resolve/cancel consultation trước review/commit và ignore stale advice.
- Mọi brief yêu cầu subagent enumerate/read tất cả regular files trực tiếp trong ~/.agents/rules/ trước substantive work, kể cả file mới; báo conflict/inaccessible. Dự kiến quá 5 phút thì chủ động nhắn task ID, attempt ID, stage và lý do.
- Preserve artifacts/findings qua worker mới. Tại 250K tokens phải partial handoff và end attempt, không vượt 300K; áp dụng đầy đủ lifecycle/progress payload/commit rules trong global skill, không thay thế chúng bằng bản tóm tắt này.
- Global skill là contract workflow hiện hành; các blocking/PASS-only records cũ chỉ lịch sử, không retroactive async/commit proof. Ngoại lệ user-global configuration rollout của skill không áp cho Meta Brain project tasks.

### Outside OMP

Không dịch tên flow agents, async lifecycle hoặc artifact URLs của OMP sang Codex CLI hay runtime khác. Dùng manage-plans và skill `implement-atomic-task` trong `~/.agents/skills/implement-atomic-task/SKILL.md` với verification/review phù hợp runtime. Không claim đã qua OMP gate khi không chạy trong OMP; owner phải biết gate nào đã thực hiện thực tế.

## Verification and Handoff

- Scope một task, đọc downstream contracts rồi triển khai; không tự thêm retries/telemetry/abstractions ngoài nhu cầu.
- Exercise changed surface thật. CLI/service: launch và observe; UI: actual surface/visual proof; MCP: real Codex/OMP bình thường; security: crypto/lock/catalog/token redemption/scope/revoke negatives. Không dùng AppContainer OS negatives thay new crypto/token proof; test suite không thay runtime smoke.
- Permanent tests chỉ bảo vệ observable behavior/boundary/state transitions có nguy cơ regression; không kiểm tra wiring, copied defaults, source text hoặc mock echoes.
- Artifact when implementing: `artifacts/sprint-<N>/task-<M>.md` is the worker-owned record of CR01 baseline, task/attempt/agent/job IDs, changed scope, exact scenarios/observations/limits/open findings, and bounded advisor evidence. Append corrections to an existing task artifact rather than overwriting history or fabricating PASS. The evidence reviewer owns its report and manifest; the checkpoint executor owns its checkpoint record. Main writes only concise plan/status links, not evidence records. Graph maintenance is retired: no graph searches/checks/absence checks/update, even if a stale skill or agent instruction asks for them.
- Giữ artifacts/, vault, raw sessions, secrets, token, runtime cache và build outputs ngoài Git. Tạo/cập nhật .gitignore trước khi sinh những dữ liệu đó. Owner-authorized planned implementation theo flow gồm per-task reviewed-snapshot commit checkpoint; ngoài phạm vi đó không tự commit, và không push nếu chưa được yêu cầu. Documentation-only planning không tự launch implementation hoặc tạo checkpoint PASS giả.
- Cập nhật docs/changelog khi có product change. Documentation-only planning không thêm changelog entry giả về tính năng chưa có.
- Không đọc private transcripts, gọi model/network provider, đổi ACL, cài service hoặc phá hủy dữ liệu ngoài consent/allowlist đã có. User-visible error là evidence, không rerun chỉ để phủ nhận báo cáo.

## Shared Skills — References

Các link tuyệt đối dưới đây trỏ thư viện local của owner (~/.agents). Nếu chuyển máy và link không tồn tại, resolve lại home rồi báo prerequisite; không bỏ qua im lặng.

| Skill | Khi dùng |
| --- | --- |
| `manage-plans` (`~/.agents/skills/manage-plans/SKILL.md`) | Global/sprint planning và status |
| `omp-subagent-flows` (`~/.agents/skills/omp-subagent-flows/SKILL.md`) | Planned delivery chỉ trong OMP; async agents, evidence gates và per-task commit checkpoints |
| `implement-atomic-task` (`~/.agents/skills/implement-atomic-task/SKILL.md`) | Một task, verification và evidence handoff |
| `log-decision` (`~/.agents/skills/log-decision/SKILL.md`) | Lựa chọn kiến trúc/API/pattern/tradeoff; append decision log |
| `log-lesson` (`~/.agents/skills/log-lesson/SKILL.md`) | Sau bug/quirk thật, không ghi planned feature như bài học đã xảy ra |
| `drawio-architecture-diagram` (`~/.agents/skills/drawio-architecture-diagram/SKILL.md`) | Khi cần .drawio/architecture flow, không vẽ thêm vì trang trí |
| `scientific-diagram` (`~/.agents/skills/scientific-diagram/SKILL.md`) | Hình nghiên cứu/conceptual map khi được yêu cầu |
| `remote-server-execution` (`~/.agents/skills/remote-server-execution/SKILL.md`) | Chỉ nếu có task SSH thật, không cho local execution |

## Shared Rules — References

Mọi subagent phải enumerate/read tất cả regular files trực tiếp trong `~/.agents/rules/` trước substantive work. Danh sách này là index hiện tại, không thay việc phát hiện rule mới. Main đọc tất cả rule liên quan trước thao tác.

| Rule | Phạm vi cần áp dụng |
| --- | --- |
| `omp-shell.md` (`~/.agents/rules/omp-shell.md`) | OMP/Windows; ưu tiên native file tools; shell đúng môi trường |
| `codex-shell.md` (`~/.agents/rules/codex-shell.md`) | Codex execution permissions, chỉ trong runtime hỗ trợ |
| `markdown.md` (`~/.agents/rules/markdown.md`) | Docs: blank line trước list/table, indentation đúng; không tự cài MkDocs chỉ để sửa Markdown |
| `python.md` (`~/.agents/rules/python.md`) | Nếu chọn Python: uv, typing, I/O, approved dependencies và tests liên quan |
| `git.md` (`~/.agents/rules/git.md`) | Git/commits nếu được yêu cầu; không stage toàn bộ hoặc commit secrets |
| `changelog.md` (`~/.agents/rules/changelog.md`) | Keep a Changelog cho product changes; không entry docs-only |

Áp dụng rule theo relevance và precedence. Nội dung chuyên biệt của dự án khác trong shared rules (ví dụ LatentSpace, theory deployment hoặc Arrow interop) không tự tạo feature cho Meta Brain. Nếu có xung đột thực sự giữa rule đang áp dụng và file-first contract, báo rõ để giải quyết, không tự lờ hoặc đổi requirement.

Một số shell rules nhắc sandbox_permissions: require_escalated. Chỉ dùng khi runtime/tool schema hỗ trợ; không bịa tham số, tự thêm quyền hoặc coi chạy sandbox là kiểm chứng host. Higher-priority runtime/tool instructions thắng; báo giới hạn và dùng cơ chế authorization hợp lệ.
