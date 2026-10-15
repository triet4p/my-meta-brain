# Sprint 4 — Retrieval và MCP/tool interface

## Sprint Goal

Codex/OMP chạy bình thường khám phá approved catalog, yêu cầu memory, nhận user-approved token/session rồi retrieval/proposal/link qua MCP thật với scope thống nhất và citations đúng.

## Status and Dependencies

- Status: Complete — S4-T1–S4-T7 có independent evidence PASS và exact-snapshot checkpoint riêng; S4-ENV1 complete. Assigned sprint acceptance/differential review PASS, zero actionable findings. Latest implementation checkpoint `1992621976a082104e01b08471a8ce5751781c4c`; owner đã authorize separate documentation commit cho final plan/status, không push hoặc tự mở Sprint 5.
- [Global plan](../PLAN.md), [specification](../META-BRAIN.md), [requirements](../REQUIREMENTS.md).
- Requirements: R06, R15, R23, R24, R25; bảo toàn R03–R05, R07, R12, R14, R16, R26.

## Downstream Contracts

UI S5 dùng catalog/request/redeem/session và private search APIs, không query index/direct decrypt. Catalog chỉ metadata được publish; brief/private cache gắn grant IDs/revisions/generation. Agent/cloud disclosure theo owner-reviewed provider context trước trả memory; ứng dụng không kiểm soát agent gửi tiếp plaintext đã nhận. Key không tới MCP/model; no standalone search bypass.

## Atomic Tasks

Status legend: [ ] pending / [~] in progress / [x] done. OMP role selection, evidence review, record ownership, and exact-snapshot checkpoints follow the [canonical execution contract](../PLAN.md#planning-and-execution-contract). Worker artifact for S4-T<M>: `artifacts/sprint-4/task-<M>.md`; reviewers and checkpoint executors own their separate records under the sprint artifact tree.

Workflow S4-T1–S4-T2: Full; independent evidence-reviewer PASS và separate Bronze exact-snapshot checkpoint cho từng task trước task độc lập kế tiếp. Implementation worker không commit; checkpoint chỉ được Main giao sau matching PASS, không push. S4-T1 dùng Gold theo owner selection; Main chọn Gold cho S4-T2 theo authority riêng task đó vì scope/revision filtering và bilingual/paraphrase relevance cần reasoning/design. Giữ nguyên downstream contracts và sprint acceptance gate.

Workflow S4-T3–S4-T4: Full, Gold theo owner selection cho từng task; sequential T3 rồi T4, independent evidence-reviewer PASS và separate Bronze exact-snapshot checkpoint cho từng task trước task độc lập kế tiếp. Worker không commit; Bronze chỉ commit wholly-owned frozen reviewed snapshot sau matching PASS, preserve/exclude foreign plan/staged changes, không push. SDK-native build và actual synthetic runtime proof bắt buộc; không owner-private corpus/model/provider calls. Sprint-end deep review chưa được giao ở boundary T3–T4.

Owner steer: sau pause tại S4-T3, owner yêu cầu tiếp tục riêng S4-T4; S4-T4 đã complete theo Full workflow/Gold selection đã chọn. Không tự mở S4-T5–S4-T7.

Workflow S4-T5: Full, Gold theo owner selection riêng T5; native SDK build và actual synthetic proposal CLI/MCP runtime proof, fresh independent evidence-reviewer PASS và separate Bronze exact-snapshot checkpoint tại boundary T5. Worker không stage/commit; checkpoint chỉ wholly-owned frozen reviewed scope, preserve/exclude foreign Main plan/index. Không push, private corpus/provider calls hoặc deep review ở boundary này; T6–T7 chưa được giao.

Workflow S4-T6–S4-T7: Full, sequential T6 (Silver) rồi T7 (Silver theo owner resume steer, thay lựa chọn Gold trước pause); mỗi task cần actual changed-surface runtime proof, fresh independent evidence-reviewer PASS và separate Bronze exact-snapshot checkpoint trước task kế tiếp. Owner consent actual official Codex/OMP command/chat verification với corpus/prompt tổng hợp cho T6/T7 và provider cấu hình của client; không private corpus/transcripts hoặc key/token/session credential trong model context/argv/log. Worker không stage/commit; checkpoint chỉ wholly-owned frozen reviewed scope tại repository/worktree `F:/ai-ml/my-meta-brain`, Git dir `.git`, ref `refs/heads/main`, preserve/exclude foreign Main plan/index; không push. Sau cả hai checkpoint, assigned sprint-end differential deep review về relation/proposal/retrieval/cache/lock/revoke và sprint acceptance, không rerun passed gates.

- [x] **S4-T1 — MCP catalog/request/token-session bridge cho Codex và OMP.** Requirements: R03–R07, R24.
    - Scope: real transport/capability dispatch, dùng S1 catalog/request/approval/redemption APIs, no key/storage/policy trong bridge.
    - Acceptance: cả hai clients bình thường initialize, discovery không private body, submit request, owner CLI approve/narrow, token local handoff/redeem và scoped calls; no owner tools/mint/unlock trên agent surface. Replay/spoof/expired/locked denies; no token/key vào argv/URI/model prompts.
    - Evidence: actual MCP initialize/calls trên official normal Codex/OMP, request/token/session/revoke flow và failed cases; không ACP/helper/mock hoặc patched AppContainer binary proof.
    - Outcome: [worker artifact](../../artifacts/sprint-4/task-1.md), [clean evidence PASS R3](../../artifacts/sprint-4/reviews/task-1-r3.md), [checkpoint PASS](../../artifacts/sprint-4/checkpoints/task-1-c1.md), commit `6d599a6b168cb2473d8cafa12d9c81b25f56f3e7`. Official Codex/OMP proof và expired-approval/channel-survival correction accepted; no push.

- [x] **S4-T2 — Scoped retrieval với lexical/metadata và bilingual relevance.** Requirements: R06, R23, R26.
    - Scope: scope-filtered lexical/metadata/ranking/time queries trên protected private index; approved catalog search là surface riêng, no embeddings mặc định.
    - Acceptance: candidate filter theo ID/revision snapshot trước private retrieval; Việt–Anh/symbol/paraphrase/current có nghĩa; no hidden counts/facets; pagination phù hợp snapshot. Catalog query chỉ published labels/descriptions, không private ranking/snippets; lock/revoke deny cache.
    - Evidence: tình huống recall thật từ corpus được phép, negative scope queries và measured latency so với envelope; ghi search tradeoff/embedding decision, không tự chọn benchmark làm mục tiêu.
    - Outcome: [worker artifact](../../artifacts/sprint-4/task-2.md), [evidence PASS R1](../../artifacts/sprint-4/reviews/task-2-r1.md), [checkpoint PASS](../../artifacts/sprint-4/checkpoints/task-2-c1.md), commit `100dc1a646804d435fc45126e7e6e6c95fe13660`. Synthetic-corpus recall/runtime proof accepted; bounded local expansion và small-corpus latency limits được ghi rõ, không claim owner-corpus/large-envelope proof. No push.

- [x] **S4-T3 — Item detail và source expansion có quyền độc lập.** Requirements: R06, R16, R18, R23.
    - Scope: get-by-ID, citations, revision/source span rendering và availability warnings.
    - Acceptance: readable item không kéo raw/source/link ngoài scope; ID guessed không private existence; đúng approved revision/spans và missing/changed warnings; new revisions cần approve lại, không silently replace body. Internal paths không lộ vô cớ, locked deny.
    - Evidence: mở kết quả search sang source bằng owner và hai grants khác nhau; revoke giữa search và get được chặn, không dùng cached item để vượt quyền.
    - Outcome: [worker artifact](../../artifacts/sprint-4/task-3.md), [clean evidence PASS R2](../../artifacts/sprint-4/reviews/task-3-r2.md), [checkpoint PASS C1](../../artifacts/sprint-4/checkpoints/task-3-c1.md), commit `e27a9a1861c2ccd6972bf62bed3de77b538c1e69`. Synthetic live-source missing/changed/re-enrollment, actual locator non-disclosure và new snapshot CLI/MCP positive paths accepted sau R1 corrections; no push. Sequence dừng trước T4 theo owner steer.

- [x] **S4-T4 — Brief/context và feedback không bóp nghĩa.** Requirements: R12, R25.
    - Scope: context budget, selection/projection và feedback storage, không synthesis LLM thường trực.
    - Acceptance: brief ngắn có citations/applicability/uncertainty, dựng lại theo grant hiện tại; không thành system instructions; read count/usefulness không tăng verification; feedback wrong/stale không tự viết lại belief của owner.
    - Evidence: quay lại project, budget nhỏ, conflict/stale entries và revoke sau cache; trace rõ nội dung được chọn và phần chưa chắc.
    - Outcome: [worker artifact](../../artifacts/sprint-4/task-4.md), [clean evidence PASS R1](../../artifacts/sprint-4/reviews/task-4-r1.md), [checkpoint PASS C1](../../artifacts/sprint-4/checkpoints/task-4-c1.md), commit `83d7a901aff2a2fb89bd75839f09093b464d0762`. Scoped deterministic brief/budget/uncertainty và independent encrypted feedback accepted trên synthetic CLI/named-pipe/MCP proof; feedback không mutate belief/evidence. A1 partial được fresh Gold A2 verify lại; failure S4-T1 cũ không reproduce, cause chưa chứng minh. No push; không claim owner-corpus/provider/sprint-end gate.

- [x] **S4-T5 — Proposal write interface cho agent.** Requirements: R03, R10, R12, R24.
    - Scope: propose create/revise với source references và revision preconditions, không auto-publish.
    - Acceptance: read-only token không write; agent cần owner-approved proposal operation/đích inbox riêng; preserved executor/AI origin/revision preconditions; errors không published, fake user claim không confirmed, repeated operation không duplicates.
    - Evidence: agent gửi lesson sau task thật trên fixture, revise proposal, denied zone và stale revision; owner CLI đọc đúng proposal để UI sau này dùng.
    - Outcome: [worker artifact](../../artifacts/sprint-4/task-5.md), [clean evidence PASS R1](../../artifacts/sprint-4/reviews/task-5-r1.md), [checkpoint PASS C1](../../artifacts/sprint-4/checkpoints/task-5-c1.md), commit `7df21292797a38533e9094e04c2d3adadb6e4fea`. Pending scoped create/revise, exact revision/source references, preserved agent provenance và grant/inbox/key idempotency accepted qua synthetic CLI/named-pipe/MCP proof. SDK build 0 warnings/errors, T5 14 PASS và 13 regression modes exit0. No publish, no private/provider calls, no push; restart giữ proposal, không claim same-grant replay qua restart vì session invalidated theo contract.

- [x] **S4-T6 — Explicit relation commands qua hội thoại và MCP.** Requirements: R14, R15, R24.
    - Scope: resolve/list/create/remove/supersede relations trong quyền; ghi request evidence và undo phù hợp.
    - Acceptance: câu “nối ý này với research X” thực hiện khi endpoints rõ; mơ hồ không nối bừa; có quyền endpoints và quyền mutation; agent không tự khai user confirmation để share; link/relation metadata không mở raw hoặc zone khác.
    - Evidence: hội thoại thực trên cả clients, explicit related-not-supports, unlink/undo, ambiguous target và cross-zone denied scenarios. Chat interpretation không được core coi là authorization.
    - Outcome: [worker](../../artifacts/sprint-4/task-6.md), [clean evidence PASS R1](../../artifacts/sprint-4/reviews/task-6-r1.md), [checkpoint PASS C1](../../artifacts/sprint-4/checkpoints/task-6-c1.md), commit `a4a5c3549b9b15ea2cde152e25cdb1d26c2ef25d`. Scoped relation lifecycle/request evidence accepted; actual synthetic Codex/OMP ambiguity, relatedTo, cross-zone denial và unlink/restore proof; supersede verified separately via CLI/MCP smoke. Build 0 warnings/errors, T6 5 groups và S2-T4 9 groups PASS. Local protected-handoff path disclosed, credential bytes not disclosed; no OS-attack proof. Foreign Main plan excluded; no push.

- [x] **S4-T7 — Agent workflow integration và cache/revocation hardening.** Requirements: R06, R23, R24, R25.
    - Scope: discovery → request → owner approval/token handoff → scoped recall/evidence → explicitly granted proposal/link; cache/lock/revocation toàn workflow.
    - Acceptance: cả clients chạy thật; không memory thành policy; new member/revision/link không tăng scope; lock/revoke chặn next calls/counts/cache, no key/token model disclosure. Withdrawal catalog chặn serving tiếp, không xóa copies đã nhận.
    - Evidence: actual command/chat workflow và hostile direct API calls, concurrent/replayed token, different scopes và lock; redact transcripts, record actual disclosure vs unverified OS attack limits.
    - Outcome: [worker](../../artifacts/sprint-4/task-7.md), [clean evidence PASS R2](../../artifacts/sprint-4/reviews/task-7-r2.md), [checkpoint PASS C1](../../artifacts/sprint-4/checkpoints/task-7-c1.md), commit `1992621976a082104e01b08471a8ce5751781c4c`. Synthetic actual Codex/OMP workflow và 28-assertion direct hardening proof accepted; SDK build 0 warnings/errors, affected regressions PASS. No product edits; parsed MCP digest assertion fixed. Attached-console preview unverified, owner API/typed CLI approval exercised; Codex configured-provider consent established by config-layer invariance inference, not paired endpoint capture. Proposal-induced stale search requires explicit owner index rebuild. Foreign Main plan excluded; no push.

## Environment Prerequisite Task

- [x] **S4-ENV1 — .NET 10 SDK availability và SDK-native verification.** Owner-requested prerequisite; không đổi thứ tự/dependencies của S4-T3–S4-T7.
    - Scope: xác định SDK/runtime/PATH đang dùng; reuse SDK đã cài nếu phù hợp, nếu thiếu cài official .NET 10 SDK x64 theo user scope. Nếu cần admin/prerequisite khác, báo exact thao tác để owner tự cài, không workaround phức tạp/nâng quyền. Không gỡ runtime/SDK hiện có hoặc thay source để né build. Theo owner steer, relocate source/scaffold thuộc my-meta-brain bị đặt nhầm dưới `F:/ai-ml/` vào đúng subtree `F:/ai-ml/my-meta-brain/`; không chạm sibling projects, private data hoặc ownership chưa rõ, không overwrite nội dung.
    - Acceptance: fresh child process resolve đúng `dotnet`, `dotnet --info` và `dotnet --list-sdks` có .NET 10 SDK; repository restore/build qua SDK thật và actual T1/T2 synthetic runtime smoke có kết quả rõ; ghi paths source/scaffold relocated và bounded verification không còn source project đã xác định nằm sai parent. Nếu cần owner install hoặc repo build lỗi, ghi exact blocker, không claim SDK verification complete hoặc tự mở rộng sang sửa product.
    - Workflow: Light environment-only task, Silver theo owner selection; Main kiểm tra completed worker evidence, không evidence-review/checkpoint/deep-review được giao, không commit/push. Worker record `artifacts/sprint-4/env-1.md`. Không đọc owner private corpus hoặc gọi model/provider.
    - Outcome: [worker record](../../artifacts/sprint-4/env-1.md). Official user-scope SDK `10.0.402` installed/verified; SDK-native restore 4 projects và build smoke project/dependencies: 0 warnings/errors; SDK-built T1 5 PASS, T2 19 PASS. Source/log/scaffolds đã xác định ở parent được relocate vào ignored `artifacts/sprint-4/relocated-scratch/` và `env-1-evidence/`; bounded post-check không còn `.s3*`/`.s4*` entries tại parent, unrelated data giữ nguyên. No product changes/commit/push.
    - Completion: owner cung cấp output thực từ PowerShell 7.6.6, `dotnet --list-sdks` → `10.0.402 [C:\Program Files\dotnet\sdk]`, xác nhận normal fresh-terminal SDK discovery sau system-wide install. Main chấp nhận evidence này để clear blocker; không rerun checks đã PASS, không claim build/smoke được chạy lại trên machine-root SDK. Task complete theo Light acceptance; không thêm review/checkpoint/commit.

## Sprint Acceptance Gate

Cả Codex/OMP: approved catalog → request → user narrow/approve → local token redeem → search Việt/Anh/get/source đúng scope → separately authorized proposal/link → revoke/lock/denied. No private index trước approval hoặc existence leak qua counts/relations/citations/cache; new IDs/revisions không auto scope. No owner/key-management tools trên MCP, no OS-isolation promise.

## Notes / Blockers

Không coi MCP roots, tool description hay hidden UI button là access control. Semantic search là lựa chọn có evidence; không bỏ khả năng tìm paraphrase đã yêu cầu chỉ vì FTS dễ hơn. Không dùng cloud embeddings khi zone chưa cho egress.

S4-T1 verification đã chạy theo explicit owner consent trên official Codex/OMP với synthetic corpus; private owner transcripts/vault và key/token model disclosure ngoài scope. Consent này không mở rộng sang T2/provider calls. T2 recall evidence chỉ synthetic/local và local expansion có coverage limits; chưa có owner-corpus hoặc large-envelope measurement claim.

## Gate Record

S4-T1: [R3 PASS](../../artifacts/sprint-4/reviews/task-1-r3.md) và [checkpoint PASS](../../artifacts/sprint-4/checkpoints/task-1-c1.md), commit `6d599a6b168cb2473d8cafa12d9c81b25f56f3e7`. S4-T2: [R1 PASS](../../artifacts/sprint-4/reviews/task-2-r1.md) và [checkpoint PASS](../../artifacts/sprint-4/checkpoints/task-2-c1.md), commit `100dc1a646804d435fc45126e7e6e6c95fe13660`. S4-T3: [clean R2 PASS](../../artifacts/sprint-4/reviews/task-3-r2.md) và [checkpoint PASS C1](../../artifacts/sprint-4/checkpoints/task-3-c1.md), commit `e27a9a1861c2ccd6972bf62bed3de77b538c1e69`. S4-T4: [clean R1 PASS](../../artifacts/sprint-4/reviews/task-4-r1.md) và [checkpoint PASS C1](../../artifacts/sprint-4/checkpoints/task-4-c1.md), commit `83d7a901aff2a2fb89bd75839f09093b464d0762`.

S4-T5: [clean R1 PASS](../../artifacts/sprint-4/reviews/task-5-r1.md) và [checkpoint PASS C1](../../artifacts/sprint-4/checkpoints/task-5-c1.md), commit `7df21292797a38533e9094e04c2d3adadb6e4fea`. S4-T6: [clean R1 PASS](../../artifacts/sprint-4/reviews/task-6-r1.md) và [checkpoint PASS C1](../../artifacts/sprint-4/checkpoints/task-6-c1.md), commit `a4a5c3549b9b15ea2cde152e25cdb1d26c2ef25d`. S4-T7: [clean R2 PASS](../../artifacts/sprint-4/reviews/task-7-r2.md) và [checkpoint PASS C1](../../artifacts/sprint-4/checkpoints/task-7-c1.md), commit `1992621976a082104e01b08471a8ce5751781c4c`.

Sprint acceptance: [differential review PASS](../../artifacts/sprint-4/reviews/sprint-4-deep-d1.md), zero actionable findings. All seven task gates/checkpoints established; synthetic official-client proof accepted with recorded console, config-invariance, index-rebuild, corpus/latency và OS-security limits. Không claim production/owner-corpus acceptance hoặc tự mở Sprint 5.
