# Sprint 13 — CLI open và browser document preview/edit

## Goal và trạng thái

- Status: **In progress — S13-T1–T5 accepted; T6 prepublication gates accepted**. Candidate `1184282a4204`; final differential and publication/public smoke pending. Release mục tiêu **1.3.0** chưa published.
- Entry: Sprint 12 complete và 1.2.0 published.
- Scope/trace: P2-03, P2-10, P2-12; R10, R23, R30, R32, R38, R39. Các P2 là candidates, không thay baseline khi chưa có approval.
- Root: `F:/ai-ml/my-meta-brain`; target ref `main`; exact base/worktree/ownership resolve khi execution.
- Contract chung: [roadmap 2.0](../ROADMAP-2.0.md), [direction](../POST-1.0-DIRECTION.md), [requirements](../REQUIREMENTS.md), [PLAN](../PLAN.md).

- Owner-approved CR03/D089: CLI là đường owner đầy đủ, MCP là agent surface; browser chỉ phục vụ một tài liệu/revision đã chọn. Không WPF, desktop shell, folder workbench, sharing/review console hoặc UI installer mới.

## Execution và gates

Full OMP route; task tuần tự T1 → T6. Mỗi task cần worker evidence, independent evidence-review PASS và Bronze exact-snapshot checkpoint trước task độc lập kế tiếp. Cuối sprint assigned differential deep review trước owner-authorized publication; publication smoke trước Complete. Current global flow điều khiển async lifecycle và review rounds. Main không implement/Git hoặc viết evidence records.

Owner authorized Sprint 13 execution and per-task reviewed-snapshot checkpoints. Roles: Gold S13-T1/T6; Silver S13-T2–T5, including same-role corrections/replacements. Full route retains independent evidence review per task, separate Bronze checkpoint at each task boundary, and final differential review. After those gates PASS, owner authorizes eligible reviewed commits pushed to `origin/main`, a new `v1.3.0` tag, exact CLI/MCP candidate and adjunct publication, and website deployment through the existing procedure; no replacement of historical tags/assets. Publication requires independent public review/smoke before Complete. No provider/private-transcript/host-mutation permission is added. Repository/worktree `F:/ai-ml/my-meta-brain`, expected Git directory `F:/ai-ml/my-meta-brain/.git`, target `main`; executor verifies actual identity and stages only owned reviewed scope. Tối đa hai active agents, một implementation task. Worker đọc implement-atomic-task và mọi regular file trực tiếp dưới ~/.agents/rules/. Artifacts tại `artifacts/sprint-13/task-<M>.md`, reviews/checkpoints dưới cùng sprint.

Không host mutation/ACL/permissions/profile edits; synthetic data mặc định. Không downgrade baseline security, reset data hoặc lấy test suite thay runtime smoke. Decision còn mở/contract thay đổi phải owner-approved và sync docs trước implementation affected path.

## Atomic tasks

Mỗi task sau phụ thuộc toàn bộ task trước; release task theo [release contract](../ROADMAP-2.0.md#7-definition-of-release-mỗi-sprint). Status `[ ]` pending; `[~]` active/blocked; `[x]` chỉ sau acceptance và gates.

- [x] **S13-T1 — Document bridge contract và resource budget.**
    - Change: Chốt `metabrain open <id>` (tên CLI dự kiến) foreground, browser session giới hạn tài liệu/revision/operations/lifetime, renderer/editor support matrix và budgets. Exercise lát cắt open/preview/edit/save/conflict/lock bằng synthetic corpus; chốt encoding/newline policy trước writes.
    - Acceptance: Reuse shared application/core và owner authority; browser không nhận key, filesystem/vault access hoặc owner API tổng quát. Loopback/Origin/body không tự cấp quyền; request binding, cross-origin/CSRF, expiry, replay và shutdown contract có evidence trước chọn dependency. Không desktop framework contest hoặc hard budget tự đặt; R38/D086 giữ nguyên trừ owner approval.
    - Check: Runnable CLI/service + browser slice, observed authority negatives và limits; không dùng mockup hoặc test suite thay smoke.
    - [Evidence R2 PASS](../../artifacts/sprint-13/reviews/S13-T1-C1-R2.md), [checkpoint](../../artifacts/sprint-13/checkpoints/S13-T1-C1.md), commit `9e20589ef1c7a1a77672951fefe5e028509f1931`. Contract and actual CLI/service/browser slice accepted; downstream preview/edit/search/lifecycle gates remain separate.

- [x] **S13-T2 — Scoped local bridge và safe preview.**
    - Change: CLI mở browser qua local document bridge với private content serve khi unlocked, không tạo HTML chứa plaintext xuống disk. Preview Markdown có format, CSV có header/column context, JSONL/text bounded; unsupported binary có state thật. Không xây folder browser/tabs workbench.
    - Acceptance: Authenticate/bind mọi document read theo T1; không mở được document/revision ngoài phiên. Không script/raw active HTML, remote images/resources hoặc network egress ngầm; response không cache, không browser storage/service worker/private URL/log hay plaintext temp. Large file paging/cancel không materialize toàn workspace. Preview/toggle không đổi original bytes.
    - Check: Actual browser render và CLI open, malicious Markdown/cross-origin/out-of-session requests, unsupported/large-file/locked negatives; ghi browser-managed persistence/erasure limits.
    - [Evidence R1 PASS](../../artifacts/sprint-13/reviews/S13-T2-C1-R1.md), [checkpoint](../../artifacts/sprint-13/checkpoints/S13-T2-C1.md), commit `6df38d502189bb485ab38c695416919604a55fbe`. Actual CLI/browser safe preview, paging/cancel and exact-cap read accepted; original-content cap unchanged.

- [x] **S13-T3 — Explicit revision-gated text editing.**
    - Change: Hai chế độ preview/edit cho supported text; Markdown/plain text và CSV raw-text editing trước, không WYSIWYG/grid rewrite. JSONL sessions nguồn luôn read-only; file workspace JSONL theo matrix T1. CLI có explicit content revision path không cần browser/editor ngoài.
    - Acceptance: Explicit save qua shared API với expected revision, tạo revision mới; edit/save/reopen/restart đúng nội dung. Conflict không overwrite; encoding/newline/CSV-invalid edits được giải thích và giữ draft trong memory khi phiên hợp lệ, không autosave plaintext. No-op preview/toggle không normalize/rewrite bytes; unsupported editing bị từ chối trung thực.
    - Check: CLI-only revise và actual browser preview → edit → save, concurrent conflict, invalid encoding, cancel/unsaved handling; giữ nguồn read-only.
    - [Evidence R1 PASS](../../artifacts/sprint-13/reviews/S13-T3-C1-R1.md), [checkpoint](../../artifacts/sprint-13/checkpoints/S13-T3-C1.md), commit `f2a4c81cccfa404949bf48371ffc2087461cd58c`. CLI/browser editing, conflict/draft, encoding/CSV refusal and restart/reopen accepted.

- [x] **S13-T4 — CLI lexical search và open-at-location.**
    - Change: Shared lexical filename/path/heading/content search, CLI folder filters và revision-pinned anchors; CLI trả đủ evidence/location dạng text/structured result, `open` có thể hiển thị đúng đoạn đã chọn.
    - Acceptance: Exact symbol/phrase/name khớp; hit anchor cùng revision preview, stale anchor không trỏ nhầm. Filter không cấp quyền; frontend không tự rank. Search usable hoàn toàn qua CLI trước hybrid sprint, không đưa search console vào browser.
    - Check: Actual CLI query-to-source và browser anchor rendering khi được mở; observe stale/missing/scope negatives.
    - [Evidence R1 PASS](../../artifacts/sprint-13/reviews/S13-T4-C1-R1.md), [checkpoint](../../artifacts/sprint-13/checkpoints/S13-T4-C1.md), commit `25a3e4b7efddca5a8a79ecfffac0ecfaade8e155`. Actual CLI search/source locations and Chromium pinned highlights accepted; stale/missing/mismatch fail closed.

- [x] **S13-T5 — Document session lifecycle và bounded work.**
    - Change: Nối bounded cache/progress/cancel với foreground CLI bridge; lock/disconnect/session expiry/CLI exit thu hồi read/save, clear browser content/draft và bỏ in-flight response theo epoch. Unsaved policy rõ, không cản owner lock.
    - Acceptance: Actual lock/exit/disconnect/restart và save/read races không phục vụ private stale response hoặc mất revision đã saved; browser reload/back không phục hồi quyền/nội dung bằng app cache. Đo CLI/service/browser resources khi indexing theo T1; không key tới browser. Không claim xóa tuyệt đối browser/managed memory, OS swap, extension hay copies đã disclosure; không sửa browser/host policy.
    - Check: Actual browser actions, lifecycle/race negatives và file/cache observations trong consent scope; ghi giới hạn đo thật.
    - [Evidence R1 PASS](../../artifacts/sprint-13/reviews/S13-T5-C1-R1.md), [checkpoint](../../artifacts/sprint-13/checkpoints/S13-T5-C1.md), commit `928e02d1c4f3ed0a2009835a9d91e2ea26c533d9`. Actual lifecycle/race/resource proof accepted, with unmanaged/browser erasure limitations retained.

- [~] **S13-T6 — Release 1.3.0.**
    - Change: Publish CLI installer + MCP ZIP mang bundled document assets/bridge, open/edit support matrix, secure-rendering/lock/browser limits và CLI-only guides; không package UI product riêng.
    - Acceptance: Packaged CLI search/read/revise/restart/unlock không cần browser; optional open → preview/edit → save → conflict/lock chạy trên browser thật. Exact candidate không plaintext HTML/temp, không general web console; release contract chung đạt.
    - Check: Exercise installed CLI, launched browser document surface và MCP khi affected; không WPF proof hoặc provider replay ngoài scope.
    - Prepublication [evidence R1 PASS](../../artifacts/sprint-13/reviews/S13-T6-C1-R1.md), [checkpoint](../../artifacts/sprint-13/checkpoints/S13-T6-C1.md), commit `35c764c74e0f50a251614a1c8c0b0a72ae85c412`; candidate `1184282a4204`. Task remains [~] until required differential and publication/public smoke pass.

## Exit và release boundary

- User outcome: CLI owner workflow độc lập; optional `open` preview/edit tài liệu qua phiên giới hạn, explicit revision save và lock/exit revocation; không plaintext HTML artifact hoặc full browser workbench.
- Tất cả task gates/checkpoints PASS, final differential không actionable findings; không commit_pending.
- Candidate chuẩn bị ở release task; review/checkpoint rồi differential trước publication. Owner authorize external actions riêng; exact public artifacts/links/version smoke hoàn tất rồi Main mới Complete. Release pending chặn sprint sau.
- CLI installer + MCP ZIP và guides version 1.3.0; browser assets thuộc CLI/shared runtime, không UI installer mới. Install/upgrade/data preservation theo CR03/shared contract, không overwrite assets.
- Kế thừa unchanged acceptance có trace; kiểm tra changed surfaces thực, không blanket replay 1.0 hoặc giả fresh PASS.

## Gate record

S13-T1–T5 accepted; T6 prepublication evidence/checkpoint PASS, latest `35c764c7`. Final differential active; publication/public-smoke gates pending, no commit_pending.
