# Sprint 11 — Folder ingestion và raw-first compatibility

## Goal và trạng thái

- Status: **Complete — M10 / 1.1.0 published**. T1–T6 acceptance, evidence/checkpoints, final differential và actual public proof đã accepted.
- Entry: Sprint 10 complete / 1.0.0 public; changed parser contract được duyệt ở T1.
- Scope/trace: P2-01, P2-12; R16–R19, R30, R36, R39, R40. Các P2 là candidates, không thay baseline khi chưa có approval.
- Root: `F:/ai-ml/my-meta-brain`; target ref `main`; exact base/worktree/ownership resolve khi execution.
- Contract chung: [roadmap 2.0](../ROADMAP-2.0.md), [direction](../POST-1.0-DIRECTION.md), [requirements](../REQUIREMENTS.md), [PLAN](../PLAN.md).

## Execution và gates

Full OMP route; task tuần tự T1 → T6. Mỗi task cần worker evidence, independent evidence-review PASS và Bronze exact-snapshot checkpoint trước task độc lập kế tiếp. Cuối sprint assigned differential deep review trước owner-authorized publication; publication smoke trước Complete. Current global flow điều khiển async lifecycle và review rounds. Main không implement/Git hoặc viết evidence records.

Owner authorize bắt đầu S11; routing đã chọn T1/T6 Gold, T2–T5 Silver. Owner đã duyệt CR02 D1=B, D2–D8=A; authorize đồng bộ baseline và triển khai S11-T2–T5 theo contract này sau các gate. Chưa authorize push/tag/upload/deploy/provider access hoặc migration dữ liệu 1.0. Full route và từng required checkpoint giữ nguyên; commit authorization và exact repository/worktree/ref/ownership phải xác định trước checkpoint dispatch. Tối đa hai active agents, một implementation task. Worker đọc implement-atomic-task và mọi regular file trực tiếp dưới ~/.agents/rules/. Artifacts tại `artifacts/sprint-11/task-<M>.md`, reviews/checkpoints dưới cùng sprint; không tạo trước PASS giả.

Không host mutation/ACL/permissions/profile edits; synthetic data mặc định. Không downgrade baseline security, reset data hoặc lấy test suite thay runtime smoke. Decision còn mở/contract thay đổi phải owner-approved và sync docs trước implementation affected path.

## Atomic tasks

Mỗi task sau phụ thuộc toàn bộ task trước; release task theo [release contract](../ROADMAP-2.0.md#7-definition-of-release-mỗi-sprint). Status `[ ]` pending; `[~]` active/blocked; `[x]` chỉ sau acceptance và gates.

- [x] **S11-T1 — Contract, CR02 proposal và corpus.**
    - Change: Đối chiếu R01–R40 với direction, lập change proposal riêng cho folder/partial parsing và downstream impact; định nghĩa supported/partial/raw-preserved states, symlink/reparse-point boundary, source consent, checkpoint và release compatibility. Chuẩn bị synthetic corpus/schema variants và measurement protocol, không đọc private sessions.
    - Acceptance: Owner approval cho changed parser contract được ghi thật, specs/requirements/decision được đồng bộ sau approval; original-source read-only, offsets/attribution và unsupported semantics rõ. Cố định corpus/query/environment đo cơ sở; không gán PASS cho proposal.
    - Accepted: Owner duyệt CR02 D1=B, D2–D8=A; baseline đã đồng bộ D088. [Evidence PASS cycle1 round2](../../artifacts/sprint-11/reviews/S11-T1-CYCLE1-R1.md), [checkpoint C1](../../artifacts/sprint-11/checkpoints/S11-T1-C1.md), commit `535cc5c0b52ea7301a1bac80edfa70791a3b7808`; [worker record](../../artifacts/sprint-11/task-1.md). Corpus là contract-level, không phải runtime proof cho T2–T5.
    - Check: exercise changed owner CLI/service và actual UI/MCP khi affected; ghi scenario, observations, negatives, limits vào task artifact. Không claim surface chưa chạy.

- [x] **S11-T2 — Folder enrollment trong shared coordinator.**
    - Change: Thêm owner operation chọn folder, recursive/include/exclude rõ, enumerate/preview rồi xác nhận một lần; membership/index metadata encrypted, reuse source registry.
    - Acceptance: CLI exercise folder nhiều file, duplicate enroll, ngoài scope, missing folder, reparse point và lock; không scan profile, không nhập tay từng file hoặc nới allowlist qua link.
    - Accepted: [Evidence cycle2 round1 PASS](../../artifacts/sprint-11/reviews/S11-T2-CYCLE2-R1.md), overall assessment3, unchanged acceptance; [checkpoint C1](../../artifacts/sprint-11/checkpoints/S11-T2-C1.md), commit `bd0d7e415373cc83471aa4ee9fbfd6eefc6b3c4a`. Durable persistence-failure/process-interruption recovery và normal CLI/service scenarios có evidence trong [worker record](../../artifacts/sprint-11/task-2.md). Read-permission-denied, deliberate path-replacement race, UI/MCP chưa exercise; không claim coverage hoặc nới host policy.
    - Check: exercise changed owner CLI/service và actual UI/MCP khi affected; ghi scenario, observations, negatives, limits vào task artifact. Không claim surface chưa chạy.

- [x] **S11-T3 — Raw-preserved và version-tolerant adapters.**
    - Change: Tách original-byte preservation được owner opt-in khỏi interpretation; Codex/OMP adapter giữ record chưa hiểu cùng offset và trạng thái, không ép thành event hợp lệ.
    - Acceptance: Known messages giữ attribution/span đúng; unknown shape/version và encoding lỗi còn đọc/export bytes được khi preservation được chọn, không silent drop hoặc tuyên bố fully interpreted. Nếu không preserve phải nói rõ source-dependent availability.
    - Check: exercise changed owner CLI/service và actual UI/MCP khi affected; ghi scenario, observations, negatives, limits vào task artifact. Không claim surface chưa chạy.
    - Accepted: Owner-approved minimal corpus correction đã thực hiện, acceptance/parser semantics giữ nguyên. [Evidence cycle1 round1 PASS](../../artifacts/sprint-11/reviews/S11-T3-CYCLE1-R1.md), [checkpoint C1](../../artifacts/sprint-11/checkpoints/S11-T3-C1.md), commit `d74279de51dc32638913c88a8592a9fd6f6668de`; [worker evidence](../../artifacts/sprint-11/task-3.md), full CLI/service smoke 20 cases và independent scenarios PASS; UI/MCP không affected/exercised.

- [x] **S11-T4 — Incremental refresh và recovery.**
    - Change: Nối folder membership refresh vào coordinator; xử lý append/partial tail/truncation/replacement/restart/cancel ở boundaries đã chốt.
    - Acceptance: Runtime smoke không duplicate, checkpoint không vượt dữ liệu chưa commit; source replacement quarantine rõ; lock/pause chặn private reads và không làm mất raw đã báo saved.
    - Check: exercise changed owner CLI/service và actual UI/MCP khi affected; ghi scenario, observations, negatives, limits vào task artifact. Không claim surface chưa chạy.
    - Approved policy: Owner duyệt explicit folder-refresh opt-in/out mặc định off; bật áp dụng current/future matching regular files trong root/include/exclude, không theo link. Per-source pause/revoke/opt-out là veto; lock/folder opt-out dừng refresh. File xóa giữ canonical/history và báo missing, không auto-revoke/purge. Contract CR02/spec/requirements/decision đã đồng bộ; [prior investigation](../../artifacts/sprint-11/handoffs/S11-T4-20261030-A1.md).
    - Accepted: [Evidence cycle1 round1 PASS](../../artifacts/sprint-11/reviews/S11-T4-CYCLE1-R1.md), [checkpoint C1](../../artifacts/sprint-11/checkpoints/S11-T4-C1.md), commit `70f6d217f569087d5532bc8ee8647b58ccf4c871`; [worker record](../../artifacts/sprint-11/task-4.md). CLI/service/background/real-store cancellation và recovery đã exercise; initial T3 assertion observation giữ trong evidence, reviewer kiểm tra độc lập không có actionable finding. UI/MCP chưa exercise.

- [x] **S11-T5 — Owner folder ingestion surface.**
    - Change: UI và CLI có folder picker/preview/enroll/status/refresh/pause; hiện riêng saved/indexed/interpreted/error và remediation theo file/offset, không echo transcript trong diagnostics.
    - Acceptance: Actual UI và CLI cùng phản ánh một source state; chọn một folder xong đọc được nguồn known và unknown dưới trạng thái đúng; lock/disconnect clear private views.
    - Check: exercise changed owner CLI/service và actual UI/MCP khi affected; ghi scenario, observations, negatives, limits vào task artifact. Không claim surface chưa chạy.
    - Accepted: [Evidence cycle1 round2 PASS](../../artifacts/sprint-11/reviews/S11-T5-CYCLE1-R1.md), [checkpoint C1](../../artifacts/sprint-11/checkpoints/S11-T5-C1.md), commit `b4a3bbbce0f0c90d32dabddef058eb2b7848f508`; [worker record](../../artifacts/sprint-11/task-5.md). Actual WPF/CLI shared state, visual bounds và lock/end/transport-disconnect clearing có evidence. R1 thiếu disconnect proof, R2 bổ sung; không claim direct in-flight disconnect race.

- [x] **S11-T6 — Release 1.1.0.**
    - Change: Đóng gói ba products, docs folder/raw-first và troubleshooting partial schema; thực hiện release contract chung.
    - Acceptance: Candidate help/version, changed folder/partial flow và upgrade 1.0 được exercise; prepublication gates rồi publish exact assets với owner authorization.
    - Check: exercise changed owner CLI/service và actual UI/MCP khi affected; ghi scenario, observations, negatives, limits vào task artifact. Không claim surface chưa chạy.
    - Accepted: [Prepublication R1 PASS](../../artifacts/sprint-11/reviews/S11-T6-CYCLE1-R1.md), [C1](../../artifacts/sprint-11/checkpoints/S11-T6-C1.md), [correction R2 PASS](../../artifacts/sprint-11/reviews/S11-T6-CYCLE1-R2.md), [C2](../../artifacts/sprint-11/checkpoints/S11-T6-C2.md); [worker record](../../artifacts/sprint-11/task-6.md). Exact candidate `573e00d784cd` (CLI/UI/MCP, unsigned) và synthetic 1.0 upgrade/backup restore/data-preserving lifecycle có runtime evidence.
    - Differential correction: [D1 FAIL](../../artifacts/sprint-11/reviews/S11-DEEP-D1.md) hai LOW đã sửa; [evidence cycle1 round2 PASS](../../artifacts/sprint-11/reviews/S11-T6-CYCLE1-R2.md), [checkpoint C2](../../artifacts/sprint-11/checkpoints/S11-T6-C2.md), commit `26d0fa1ee37cce13c8410af919b4b217822bcf52`; [fresh D2 PASS](../../artifacts/sprint-11/reviews/S11-DEEP-D2.md), zero actionable findings. Docs mô tả đúng fingerprint/retention; actual CLI expiry check có evidence, asset bytes giữ nguyên.
    - Publication authority: Owner duyệt push reviewed source/main, tag/release `v1.1.0`, upload exact candidate assets/manifests/digests và Pages/guides cập nhật đúng release; không overwrite assets/release có sẵn. Nếu cần source/docs delta: tối đa hai assessments trong publication cycle liên kết T6 prior rounds, independent evidence + Bronze checkpoint trước push; không reset lịch sử/auto-stage foreign planning. Public hashes/links/version proof cần trước Complete.
    - Published: [Publication delta R1 PASS](../../artifacts/sprint-11/reviews/S11-PUBLISH-CYCLE1-R1.md), [publication checkpoint](../../artifacts/sprint-11/checkpoints/S11-PUBLISH-C1.md), source/tag target `98a8379041e568dfff93340e44daa6fbf1fd669d`; [final D3 PASS](../../artifacts/sprint-11/reviews/S11-DEEP-D3.md), zero actionable findings. [Release v1.1.0](https://github.com/triet4p/my-meta-brain/releases/tag/v1.1.0), [live site](https://triet4p.github.io/my-meta-brain/), [Pages deployment](../../artifacts/sprint-11/publication/S11-PAGES-DEPLOY-C1.md) `cec39e7adfcbc1c4198262dbaf11f1c5019c1068`; [public proof](../../artifacts/sprint-11/publication/S11-PUBLISH-P2.md) xác nhận full asset downloads size/SHA-256, actual downloaded CLI/MCP/UI và live browser/site. 1.0.0 giữ nguyên; không claim production-host mutation/provider/client replay.

## Exit và release boundary

- User outcome: Actual UI và CLI cùng phản ánh một source state; chọn một folder xong đọc được nguồn known và unknown dưới trạng thái đúng; lock/disconnect clear private views.
- Tất cả task gates/checkpoints PASS, final differential không actionable findings; không commit_pending.
- Candidate chuẩn bị ở release task; review/checkpoint rồi differential trước publication. Owner authorize external actions riêng; exact public artifacts/links/version smoke hoàn tất rồi Main mới Complete. Release pending chặn sprint sau.
- Ba products và guides tương ứng version 1.1.0; install/upgrade/data preservation theo shared contract. Không overwrite assets hoặc đem compatibility break vào minor release.
- Kế thừa unchanged acceptance có trace; kiểm tra changed surfaces thực, không blanket replay 1.0 hoặc giả fresh PASS.

## Gate record

T1–T6 accepted qua task links phía trên; publication delta review/checkpoint và final D3 PASS. Owner-authorized release/site đã published và public proof accepted; M10/Sprint 11 Complete. Main status/planning files vẫn là local foreign state, không auto-stage/push. Sprint 12 chưa authorize bắt đầu.
