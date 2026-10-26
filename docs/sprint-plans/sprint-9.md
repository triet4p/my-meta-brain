# Sprint 9 — ACL Cleanup và Three-Product Delivery

## Sprint Goal

Không thêm hoặc đổi chức năng domain sau Sprint 8. Xóa code ACL/permission hardening và code không còn dùng trong phạm vi cutover; đóng gói ba sản phẩm dùng được thật: **CLI**, **Owner UI**, **MCP Server**, cùng một core/application/policy và cùng vault khi owner chọn cùng instance.

Owner không phải chạy đoạn PowerShell để tạo settings/data, khởi động service/UI, ráp request hoặc hoàn thành phần còn thiếu của một workflow. Chạy lệnh của CLI trong PowerShell/cmd/terminal là hợp lệ; viết/chạy script PowerShell phụ trợ bắt buộc không phải là sản phẩm CLI.

## Status and Dependencies

- Status: **Complete — S9-T1–T7 accepted; final differential PASS**. Owner walkthrough feedback, per-task independent evidence and Bronze checkpoints are recorded; [final differential PASS](../../artifacts/sprint-9/product/reviews/S9-DEEP-D1.md) has no actionable findings. Latest checkpoint `635af2a6a6ffb116fe196bdde95b287085f01db2`; no push. Dependency: [Sprint 8 complete](sprint-8.md), owner acceptance và các giới hạn đã công bố giữ nguyên.
- Owner scope clarification 2026-10-24: chọn **độc lập theo quyền hiện có**. CLI và UI mỗi bên hoàn tất owner workflows; MCP hoàn tất agent workflows. Owner unlock/approval là security prerequisite của MCP, không phải bước agent tự làm và không được bỏ để đạt parity.
- Baseline: [specification](../META-BRAIN.md), [R01–R40/change control](../REQUIREMENTS.md), [global plan](../PLAN.md), CR01/D016 và D086 trong [decision log](../../.agents/memory/decisions.md). Không mở lại nghiệm thu Sprint 8 ngoài changed paths hoặc concrete regression.
- Sprint 9 trước đây là workflow maintenance đã complete. Giữ nguyên lịch sử ở cuối file; không tính historical PASS/checkpoint vào các task S9-T1–T7 mới.

## Scope and Non-Goals

- Cho phép: xóa code không dùng/obsolete và callers/tests/dependencies tương ứng; hoàn thiện adapter/command/input/launcher/startup/lifecycle/help/package cho các chức năng đã có; UI/MCP không shell-out sang CLI hoặc PowerShell để thực hiện domain operations.
- Không cho phép: đổi semantics capture/search/ranking/relations/proposals/jobs/lifecycle; thêm domain capability, authority, storage/policy implementation, schema/key/index format hoặc migration/reset; sửa performance theo ngưỡng mới; provider calls hoặc scan private sessions không có consent.
- Không diễn giải “cleanup” thành chỉ xóa vài helper nhưng giữ prerequisite ACL ở đường khác. Cũng không xóa DPAPI, cryptography, path/reparse validation, authenticated owner/agent channel identity, grant/scope/revision checks chỉ vì chúng nằm trong file security-related.
- Không ACL/DACL/owner/permission mutation, kể cả fixture và tạo custom security descriptor trong code chạy thử; không host/system/profile/config mutation. Windows áp default permissions không đồng nghĩa ứng dụng tự triển khai ACL hardening. Thay đổi transport phải giữ application authorization, local-only và endpoint/process validation cần thiết.
- Không tự xóa runtime/vault/raw session/backup hoặc artifacts lịch sử. Script test còn cần để tạo prior-format fixture chỉ là developer tooling; không được trở thành user prerequisite. Chỉ xóa script khi thật sự obsolete và đã migrate caller.
- Nếu inventory cho thấy một workflow đòi domain/API capability mới, ghi gap và xin owner quyết định trước khi mở rộng; không tự thêm chức năng hoặc đánh N/A để tránh acceptance.

## Research Basis and Boundaries

Read-only research, không runtime verification hoặc implementation PASS:

| Finding | Source pointer | Planning impact |
| --- | --- | --- |
| CLI/service/MCP còn chung executable với modes `serve-console`, `serve-service`, `owner`, `agent-mcp`; host cần config | `src/MetaBrain.Connections/Program.cs:18-51` | Cần entrypoint sản phẩm rõ ràng và lifecycle/config không dựa script |
| Review script publish, tạo settings, start service, mở UI rồi stop service | `scripts/owner/start-owner-review.ps1:60-138` | Không dùng script này làm lối vào nghiệm thu UI cuối sprint |
| Daily runbook tách installer start và Owner UI | `docs/OPERATIONS.md:48-116` | UI phải tự hoàn tất startup/setup trong sản phẩm, không bắt người dùng chuyển CLI |
| File reader vẫn đọc filesystem owner SID | `src/MetaBrain.Connections/PrivateFile.cs:19-29,54-65` | Trace callers, bỏ filesystem ownership prerequisite obsolete; không nhầm với authenticated API principal |
| Named-pipe server tự tạo DACL/SDDL qua native APIs | `src/MetaBrain.Connections/WindowsPipeServer.cs:19-104` | Xử lý đường ACL production này, không kết luận “src hết ACL” chỉ từ `SetAccessControl` |
| Test vẫn có ACL writes và protected-ACL expectations | `tests/MetaBrain.S1T4.Smoke/OwnerServiceSmoke.cs:607-626,1361-1395`; `AgentSessionSmoke.cs:905-912` | Migrate fixture helpers/callers trước khi chạy; không execute old mutators để lấy failing-before evidence |
| Legacy migration fixture script gỡ ACL của old source | `scripts/s8-t3/prepare-old-vaults.ps1:124-151` | Phân loại tooling theo reachability/purpose; giữ prior-format recovery proof cần thiết |
| MCP chỉ phục vụ agent; owner operations bị cấm | `docs/REQUIREMENTS.md:14-18,36-48`; `docs/META-BRAIN.md:154-168` | Independence không có nghĩa owner-agent authority ngang nhau |

Research không phải exhaustive dead-code proof. T1 phải trace caller thực tế và alternative native permission APIs; grep không đủ chứng minh reachability.

## Product Independence Contract

| Product | Entry and workflow contract | Không được bắt user làm |
| --- | --- | --- |
| CLI | Packaged executable, contextual help, owner inputs/prompts và agent diagnostics phù hợp quyền; tự setup/start/connect qua shared lifecycle; hoàn thành owner workflow trong terminal | Viết PowerShell, tạo internal JSON/config bằng tay, mở UI để làm phần CLI thiếu |
| Owner UI | Mở executable từ package/Explorer; chọn/create/connect instance, start/status/stop theo hành động owner; provision/unlock và mọi owner workflow trong UI | Build từ repo, chạy installer/CLI/script để bootstrap, seed data, tạo zone/ID hoặc hoàn thành workflow |
| MCP Server | Packaged stdio entrypoint cấu hình bằng normal Codex/OMP; start/connect shared locked host theo lifecycle contract; discovery/request/status/redeem và granted operations end-to-end | Chạy CLI/PowerShell để chuẩn bị agent request/session hay làm nốt tool còn thiếu; agent tự unlock/approve hoặc nhận key |

- Service host là runtime dùng chung đi kèm sản phẩm, không phải sản phẩm thứ tư user phải tự orchestration. Không yêu cầu ba core/vault copies hay ba service instances.
- Mỗi entrypoint chạy được khi các frontend còn lại chưa mở. Cùng instance có một authoritative host/key/lock/session state; mở thêm frontend không restart hoặc tự lock/unlock instance đang dùng.
- Fresh start luôn locked/uninitialized phù hợp trạng thái thật. Launch/start khác unlock; MCP startup không auto-unlock hoặc auto-approve.
- Owner có thể chỉ dùng CLI hoặc chỉ dùng UI cho setup và toàn bộ owner workflow. Trong agent-sharing workflow, owner dùng một trong hai để unlock/approve/revoke; client hoàn thành phần agent qua MCP. Đây là phối hợp hai principal, không phải ép owner chuyển frontend giữa chừng.
- External client registration là cấu hình tích hợp một lần do owner thực hiện bằng cơ chế client hỗ trợ; package cung cấp command/args chính xác, không sửa profile/settings của Codex/OMP thay owner.
- Credentials qua secure interactive input của owner; không secret argv/env/config/log/model context. Token/session handoff giữ cơ chế encrypted hiện có; MCP stdout chỉ protocol, diagnostics sanitized trên stderr.

## Coverage and Verification Contract

Mỗi task surface cập nhật một coverage matrix trong worker artifact: workflow → shared operation → CLI path → UI path → MCP path/explicit owner-only refusal → prerequisites → exercised result. Không dùng số command, screenshot, build hoặc “tool listed” thay actual workflow.

Owner coverage bắt buộc gồm: setup/start/connect/status/stop/restart; provision/recovery/unlock/lock; zone/collection và source mapping/enroll/ingest/refresh/preservation; capture/read-back; library/filter/detail/evidence/current-history/revise/index recovery; proposal review/publish/reject/undo; relations; catalog/requests/preview/narrow/approve/reject/handoff/sessions/revoke; provider configuration/jobs/schedules trong consent; intentions/review/lifecycle/archive/purge; audit/diagnostics/retention; encrypted backup/restore/export và supported upgrade/uninstall preservation. Phân biệt workflow công khai với internal/test commands; không ép UI phơi raw diagnostic internals.

Agent coverage gồm toàn bộ existing MCP/application agent contract: discovery/request/status/redemption/session inspection/authorization; scoped read/search/get/source/evidence/brief; proposal, relations, feedback và các exposed lifecycle/query operations thực sự có trong inventory. Owner-only actions phải refusal đúng, không giả support. Không thêm tool domain mới để lấp chức năng chưa tồn tại.

Final smoke chạy từ published package ngoài source/build tree, với synthetic clean data roots: CLI-only, UI-only, MCP + normal Codex, MCP + normal OMP. Tạo dữ liệu bằng surface đang nghiệm thu, không hidden seeding script/direct vault writes. Có một pass dùng cùng instance để kiểm tra frontend consistency/concurrent launch và lock/restart invalidation. Fresh/empty, existing valid vault, unavailable host, stale revision/index, wrong-key, lock và error recovery là trạng thái bắt buộc phù hợp changed path.

Không gọi provider thật nếu chưa có consent/budget: config/refusal paths được exercise; real execution dùng accepted unchanged evidence nếu applicable, ghi rõ limit, không nhận là fresh provider PASS. Human-only approval/UI judgement do owner thực hiện; không bắt owner lặp lại checks agent có thể exercise an toàn.

## Execution Route and Records

- **Full route cho S9-T1–T7** vì cleanup đụng transport/security và product entry/lifecycle interactions: worker behavior proof → independent evidence PASS → fresh Bronze-only exact-snapshot checkpoint cho từng task trước task độc lập tiếp theo. Cuối sprint assigned differential deep review vì ba frontend dùng chung host/state.
- Thứ tự: **T1 → T2 → T3 → T4 → T5 → T6 → T7 → final deep**. Không shared atomic checkpoint batch. T3–T5 dựa trên T2 và đi tuần tự theo flow, không triển khai song song.
- Owner chọn **Gold cho S9-T1–T4, Silver cho S9-T5–T7**. Full route giữ nguyên; owner xác nhận luôn thực hiện per-task Bronze checkpoint, không hỏi lại lựa chọn checkpoint trong Sprint 9. Quota unknown. Không push.
- Project/worktree: `F:/ai-ml/my-meta-brain`, target ref `main`; dispatch phải xác minh exact repository/git-dir/base/ownership theo flow, không dùng ancestor repo. Worker không stage/commit/push. Chỉ checkpoint executor được Main authorize sau matching PASS; không push.
- New records dùng namespace **`artifacts/sprint-9/product/`**: worker `task-<N>.md`, `reviews/`, `checkpoints/`, `handoffs/`, `state/`. Không overwrite maintenance `artifacts/sprint-9/task-1.md` hoặc gate records cũ. Các record private/generated ở ngoài Git theo project rules.
- Canonical OMP skill điều khiển async, hai active agents tối đa, review rounds và checkpoint isolation. Chỉ matching evidence PASS và successful exact-snapshot checkpoint mới cho phép đánh task accepted.

## Atomic Tasks

Status: `[ ]` pending / `[~]` active or blocked / `[x]` accepted with required gates.

- [x] **S9-T1 — Xóa ACL/ownership-hardening code và callers obsolete.** Requirements: R04, R05, R24, R32, R35, R36, R39.
    - Scope: inventory production/tests/scripts/package dependencies; bỏ explicit filesystem/pipe ACL construction/mutation, filesystem ownership gates obsolete, fixture mutators và old hardening expectations; clean cutover mọi caller trong scope. Giữ crypto/DPAPI, safe path validation, authenticated principal/channel và core scope policy; không biến removal thành allow-all owner APIs.
    - Acceptance: product và runnable verification paths không còn custom ACL/permission setup/prerequisite; không yêu cầu owner sửa quyền hiện hữu; named pipes vẫn local-only, owner/agent operations không lẫn quyền; valid existing ciphertext vẫn đọc được đúng key và wrong-key/tamper/scope/revoke/lock/restart vẫn fail closed. Không xóa current security regression chỉ vì fixture của nó từng dùng ACL; đổi fixture setup và giữ assertion bảo vệ API behavior.
    - Evidence: static caller/dependency audit rồi runtime service/transport + changed security negatives trên fixture ordinary permissions; không chạy old ACL code để reproduce. Historical evidence giữ nguyên; sửa live docs sai về ACL, không rewrite history.
    - Accepted: [worker evidence](../../artifacts/sprint-9/product/task-1.md), [independent R1 PASS](../../artifacts/sprint-9/product/reviews/S9-T1-R1.md), [Bronze checkpoint C1](../../artifacts/sprint-9/product/checkpoints/S9-T1-C1.md), commit `94a4a97f65cbcf2b91f59faadc0d4249a9f8c331` (33 worker-owned paths; Main-owned plan changes excluded; no push). Safety violation during baseline reproduction is disclosed in evidence/review; not authorization to execute old ACL paths again.

- [x] **S9-T2 — Shared runtime bootstrap/lifecycle không script.** Requirements: R02–R05, R24, R32, R35, R39.
    - Scope: reuse runtime/settings/installer mechanisms, có một lifecycle implementation để cả ba entrypoints resolve/create/select instance và start/connect host; chỉ nonsensitive config, không auto-provision/unlock. Không thêm daemon/service-registration architecture hoặc duplicate key store.
    - Acceptance: clean root không phải tạo settings bằng tay; existing root không bị overwrite/reset; concurrent starts không tạo hai writers/hosts; readiness/version/path conflicts trả lỗi thật và remediation; host start locked, restart invalidates sessions; đóng một frontend không kill host đang được frontend khác dùng hoặc để lại process không rõ ownership. Stop/restart là hành động tường minh, cùng core drain/lock.
    - Evidence: packaged minimal runtime invocation với cold/warm/concurrent launches, bad config/occupied endpoint/unavailable host và start/stop/restart; kiểm tra no secret persistence hoặc host mutation. Entry-specific UX tiếp tục ở T3–T5, không nhận scaffold là product complete.
    - Accepted: [worker evidence](../../artifacts/sprint-9/product/task-2.md), [R1 PASS](../../artifacts/sprint-9/product/reviews/S9-T2-R1.md), [Bronze C1](../../artifacts/sprint-9/product/checkpoints/S9-T2-C1.md), commit `c0fcf56069d08ce2be4825d0e9c103f79e5ef698` (8 worker-owned paths; Main plans excluded; no push). Existing S8-T1 log-fingerprint failure and unexercised downstream entry UX retained in evidence.

- [x] **S9-T3 — CLI product hoàn chỉnh cho existing owner workflows.** Requirements: R02, R05, R19, R31, R32, R34–R36, R39.
    - Scope: entrypoint/help/commands/input prompts và shared lifecycle integration; bridge toàn bộ owner coverage qua existing shared APIs. Không đòi PowerShell để tạo JSON payload/data/IDs hoặc mở UI; advanced file import nhận file owner đã có, không biến internal fixture schema thành bước bắt buộc.
    - Acceptance: từ clean package/root hoàn thành setup → provision/unlock → tạo dữ liệu bằng CLI → read/search/review/sharing/lifecycle/backup theo coverage; các workflow còn lại có commands/input khả dụng cùng semantics với core. Revision conflict, partial failure và exit codes/output không báo saved/approved giả; sensitive confirmation và secure credential entry giữ nguyên.
    - Evidence: actual CLI terminal smoke từ dữ liệu rỗng, workflow matrix và negative/error paths; UI không chạy, không raw PowerShell/API helper, no direct vault write.
    - Accepted: [worker matrix/evidence](../../artifacts/sprint-9/product/task-3.md), [review round2 PASS](../../artifacts/sprint-9/product/reviews/S9-T3-R1.md), [Bronze C1](../../artifacts/sprint-9/product/checkpoints/S9-T3-C1.md), commit `fea79da15e3634c49ec19745b6ac5e866cef184b` (4 worker-owned paths; Main plans excluded; no push). Round1 attached-console approval evidence gap corrected in round2; no-consent provider execution remains unexercised.

- [x] **S9-T4 — Owner UI tự khởi động và hoàn tất owner workflows.** Requirements: R02, R05, R19, R30–R36, R39.
    - Scope: integrated first-run/start/connect/status/stop UX trên shared lifecycle; lấp adapter/input/navigation gaps cho existing owner coverage, không redesign domain hoặc thêm chức năng mới.
    - Acceptance: mở UI executable từ Explorer trong package sạch, không service chạy sẵn, không script/CLI/settings hand-edit; provision/unlock, tạo zone/source/note, đọc lại và hoàn thành owner coverage chỉ trong UI. Existing instance chọn đúng vault; recoverable failures có next action trong UI. Lock/disconnect clear private views/inflight/drafts theo existing contract; không cache plaintext.
    - Evidence: actual WPF interaction và visual proof trên synthetic data; cold start, restart, refusal/recovery, keyboard/navigation. Không dùng script review làm launcher/seed hoặc lấy build/unit tests thay actual UI. Owner independent walkthrough được thực hiện một lần trên final package ở S9-T7, không phải prerequisite riêng của T4.
    - Accepted: [worker evidence](../../artifacts/sprint-9/product/task-4.md), [final R3 PASS](../../artifacts/sprint-9/product/reviews/S9-T4-R3.md), [Bronze C1](../../artifacts/sprint-9/product/checkpoints/S9-T4-C1.md), commit `e1d34cdeb7dc6fe204c28db2ffcf64d89423f3bd` (15 worker-owned paths; Main plans excluded; no push). R1/R2 failures retained; round3 closed UI-only validity/reviewqueue, nonempty intentions and evidence-overclaim findings. Human walkthrough remains mandatory at T7, not performed.
    - Owner-approved gate timing change: previously T4 required independent owner walkthrough before acceptance, with final owner acceptance again at T7. New: move that human walkthrough to final-package S9-T7 only, avoiding duplicate staging/release walkthroughs. Approval used the owner's explicit default-on-timeout authority through end S9. No performed human action or retroactive PASS is claimed; technical UI evidence/review/checkpoint, product scope, security and final owner acceptance are unchanged.

- [x] **S9-T5 — MCP Server product hoàn chỉnh cho existing agent workflows.** Requirements: R03–R09, R15, R16, R24, R25, R32, R36.
    - Scope: dedicated packaged MCP entrypoint, shared locked-host start/connect và đầy đủ existing agent tools/inputs/errors; registration instructions cho normal Codex/OMP. Không yêu cầu CLI để tạo request, đổi token, chuẩn bị session hoặc làm nốt tool.
    - Acceptance: client launch thực hiện protocol initialize/discovery; sau explicit owner unlock/approval qua UI hoặc CLI, request → status → encrypted handoff redeem → scoped workflows chạy qua MCP. Replayed/expired/revoked token/session, scope/revision violations, lock/restart và owner-operation attempts bị từ chối; stdout không lẫn log hoặc prompt credential. MCP không tự publish/unlock/approve/purge.
    - Evidence: hai normal clients thật trên package, account/profile unchanged; agent process ownership/cleanup rõ. Không test bridge-only rồi claim cả hai clients; không key/token/model-context disclosure. Owner có thể chọn UI-only hoặc CLI-only để quản trị, không bắt dùng cả hai.
    - Accepted: [worker evidence](../../artifacts/sprint-9/product/task-5.md), [R1 evidence PASS](../../artifacts/sprint-9/product/reviews/S9-T5-R1.md), [Bronze C1](../../artifacts/sprint-9/product/checkpoints/S9-T5-C1.md), commit `d14f2fc8d09e8b48d5e46e34d74b40f38b50887d` (nine owned paths; Main plans excluded; no push). Actual Codex/OMP initialize/discovery and packaged scoped workflows proved; text/binary snapshot transport corrected with exact-byte proof. Normal-client discovery and separate packaged workflow scopes, isolated client scratch state, no model turn/network-zero claim, and historical SDK certificate incident remain disclosed.

- [x] **S9-T6 — Release bundle cho CLI, UI và MCP.** Requirements: R24, R32, R34–R36, R39.
    - Scope: publish/package cả ba entrypoints và shared runtime dependencies, version/help/runbooks/quick starts đúng sản phẩm; developer build tooling không là runtime dependency. Reuse supported install/upgrade/data-root model, không installer framework mới.
    - Acceptance: ba executable entrypoints rõ ràng (final names/layout ghi trong help/docs); package self-contained Windows target theo baseline, không SDK/repo/PowerShell prerequisite. Mỗi frontend khởi động từ package, nhận cùng selected instance; upgrade giữ data/key compatibility; uninstall không tự xóa vault/source/backup. Không autostart/registry/service/profile mutations bởi agent; owner-only host steps nếu thực sự cần phải nêu trước, không giấu trong launcher.
    - Evidence: publish thật, launch từng entrypoint từ extracted/staged output ngoài source/build tree; version/dependency checks và existing-data upgrade/uninstall-preservation proof. Retire obsolete runtime scripts/callers sau khi paths thay thế đã prove; giữ developer-only fixtures cần thiết có nhãn rõ.
    - Accepted: [worker evidence](../../artifacts/sprint-9/product/task-6.md), [R1 PASS](../../artifacts/sprint-9/product/reviews/S9-T6-R1.md), [Bronze C1](../../artifacts/sprint-9/product/checkpoints/S9-T6-C1.md), commit `753ee0e15f792836ffd71c206133ce7773cb1dc9` (17 owned paths; Main plans excluded; no push). Self-contained win-x64 1.0.0 bundle exercised from extracted output with CLI/UI/MCP on one instance; same-version upgrade/uninstall/reinstall preserved synthetic data. Two uninstall defects corrected; no human walkthrough or newer-format upgrade claimed.

- [x] **S9-T7 — Three-product acceptance không PowerShell glue.** Requirements: R01–R40 regression phạm vi changed surfaces, trọng tâm R05/R24/R30–R36/R39/R40.
    - Scope: final package coverage matrix, end-to-end pass và owner acceptance đúng independence contract; không thêm domain/performance tuning hoặc rerun toàn Sprint 8.
    - Acceptance: CLI-only và UI-only owner passes từ empty roots không hidden seed/config/start scripts; Codex-MCP và OMP-MCP passes không CLI glue; shared-instance consistency/concurrent launch, cross-frontend lock/restart/revoke và unchanged existing-vault reads đúng. Không workflow owner nào phải chuyển CLI↔UI để làm bước bị thiếu; MCP chỉ cần owner interaction đúng security boundary đã chốt. Không serious finding, missing existing workflow hoặc undocumented prerequisite.
    - Evidence: actual commands/actions/results + package provenance, owner independent walkthrough of the final CLI/UI/MCP package, owner feedback/approval và disclosed limits; per-task evidence/checkpoints đầy đủ trước final differential deep. This is the human walkthrough gate moved from T4, not an optional substitute. Human-only missing evidence giữ blocked, không claim “three complete products” bằng source/build hoặc vài screenshots.
    - Accepted: [worker evidence](../../artifacts/sprint-9/product/task-7.md), [R1 PASS](../../artifacts/sprint-9/product/reviews/S9-T7-R1.md), [Bronze C1](../../artifacts/sprint-9/product/checkpoints/S9-T7-C1.md), commit `635af2a6a6ffb116fe196bdde95b287085f01db2` (five owned paths; Main plans excluded; no push). Owner accepted the original package after actual CLI/OMP walkthrough; corrected package adds CLI UTF-8 output and explicit approval-operation guidance, verified by targeted runtime/provenance smoke. Reviewer accepted the bounded delta without a second owner walkthrough; no invented UI/Codex human actions. Final ZIP SHA-256 `f5bb9bf4fccc8ecf911da8c0b202a8c3c4bd9e23b78ae74ec61e1126ad974ee5`; prior integrated matrix and disclosed limits retained.

## Sprint Acceptance Gate

T1–T7 có matching evidence PASS và checkpoint; final differential deep không actionable findings; owner xác nhận CLI/UI/MCP dùng đúng phạm vi mà không PowerShell glue hoặc bắt owner đổi frontend. CR01/R01–R40/D086, key custody và owner-agent separation không giảm. Không raw session/vault migration/reset hoặc ACL/host mutation để vượt gate. Docs/changelog do implementation cập nhật theo thay đổi đã chạy thật; planning này không có product changelog hoặc runtime PASS.

Gate complete: [S9-DEEP-D1 PASS](../../artifacts/sprint-9/product/reviews/S9-DEEP-D1.md), zero actionable findings, all seven accepted task checkpoints. Original owner-tested package and corrected UTF-8 package remain distinguished in T7 evidence; targeted delta proof accepted, no additional human actions or full-matrix rerun claimed. Main planning/status files remain outside product checkpoints; no push authorized or performed.

## Historical Workflow Maintenance — Complete, Not Product Delivery

Nội dung dưới đây là record của đợt maintenance trước khi Sprint 8 hoàn tất; giữ references/status cũ, không phải scope hoặc gate cho S9-T1–T7.


Separate from replacement product Sprint 1 and future Sprints 2–8. CR01/D016 security model, product acceptance, provider budgets/consent and historical proofs remain unchanged.

- [x] Task 1 — Validate scout leads and repair only confirmed live OMP workflow conflicts.
  - Project/repository/evidence root: `F:/ai-ml/my-meta-brain`.
  - Preserve existing branch/base and unrelated user changes; no secret reads, provider calls, native builds, service provisioning or push.
  - Artifact: `artifacts/sprint-9/task-1.md`; reviewer report/snapshot: `artifacts/sprint-9/reviews/`; authorized fresh task checkpoint record: `artifacts/sprint-9/checkpoints/`.
  - Scout leads: current `AGENTS.md`/`docs/PLAN.md` governance; current default advisor DS and historical hard-task Muse model mentions are NOT conflicts by themselves.
  - Acceptance: audit root/nested/inherited AGENTS, local rules/skills/overrides, active/future plan governance/templates. Repair current Main implementation/log/Git/history obligations, outdated routing, synchronous/polling/gating rules, transient work records and advisor activation only with exact evidence. Preserve domain security, owner consent, non-OMP runtime guidance and historical task records. Fresh task commits after PASS are valid.
  - Main owns maintenance plan/status; worker may repair normative clauses in existing plans only, not product planning/status. No tests/builds mid-flight; final focused instruction walkthrough, fresh evidence gate and exact-snapshot checkpoint for task-owned corrections. No invented changes on clean surfaces.

### Historical Status

Complete. Ten-document scoped workflow repairs, evidence `meta-review-1` PASS and delegated checkpoint `3622d8f6f2d5eb67763f47db572c421805ce685f` (main, parent `1ede35e2a908a090aeb43e94621ba7a8f9293c70`). Owner approved actual OMP Git tools and whole ten-doc snapshots; Sprint 9 excluded/untracked. Latest four-project differential `deep-3` PASS for all projects; report `artifacts/sprint-9/reviews/workflow-final-deep-3.md`. Product security/consent/budgets preserved, no push. Global shell-rule mismatch remains outside the four-project repair scope.
