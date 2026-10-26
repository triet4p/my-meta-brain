# Sprint 10 — Release 1.0.0 và User Onboarding

## Sprint Goal

Phát hành **Meta Brain 1.0.0** cho **CLI**, **Owner UI** và **MCP Server**, với **hai bộ cài Windows riêng cho CLI và UI**, cùng một **landing page hướng dẫn sử dụng theo kịch bản**, bao gồm kết nối normal Codex CLI và Oh My Pi (OMP).

Sprint 9 là mốc đóng băng phần lớn chức năng. Sprint 10 tập trung distribution, installation, release integrity và onboarding; không mở một vòng phát triển domain mới.

## Status and Dependencies

- Status: **Complete — M9; release 1.0.0 and GitHub Pages public; final filename/README/typography cutover verified**.
- Owner direction 2026-10-26: Sprint 9 complete; Sprint 10 dành cho release 1.0.0 của ba sản phẩm, CLI/UI có bộ cài Windows riêng, landing page hướng dẫn CLI/UI theo kịch bản và connect Codex/OMP.
- Entry: [Sprint 9 complete](sprint-9.md), S9-T1–T7 accepted, latest checkpoint `635af2a6a6ffb116fe196bdde95b287085f01db2`, final differential PASS. Không cần chạy lại checks để xác nhận báo cáo hoàn thành của owner.
- Baseline: [specification](../META-BRAIN.md), [R01–R40](../REQUIREMENTS.md), [global plan](../PLAN.md), CR01/D016, D086 và D087 trong [decision log](../../.agents/memory/decisions.md). Product independence và owner-agent boundary của Sprint 9 giữ nguyên.
- Existing bundle đã dùng version `1.0.0` tại S9-T6. Đây không phải bằng chứng public release, native installer hay Sprint 10 PASS. Phải phân biệt candidate/build identity bằng source revision và hashes; không ghi đè asset đã công bố dưới cùng tên/version.
- Owner clarification 2026-10-26: **Sprint 10 không test lại chức năng hoặc flows**; phần này đã hoàn thành và được nghiệm thu ở Sprint 9. Kế thừa accepted functional/client/owner-walkthrough evidence của Sprint 9, không tạo fresh PASS cho các flows không chạy lại.

## Scope Freeze and Non-Goals

- Giữ nguyên semantics storage/crypto/key/index, capture, attribution, search/ranking, relations, proposals, jobs, lifecycle, grants và egress. Chỉ sửa release/install/docs defects hoặc concrete regression trên changed paths, với evidence và gate tương ứng.
- Không thêm domain feature, telemetry, auto-update, SaaS, sync, Linux/macOS/ARM support, model provider mới hoặc installer framework tổng quát. Windows x64 self-contained giữ từ baseline; supported Windows versions phải được xác minh và nêu đúng, không suy từ tên target framework.
- Chức năng mới hoặc thay contract cần owner approval/change control, không lén đưa vào release hardening. Không tái benchmark để thay D086 hoặc tuyên bố performance cho mọi máy.
- Không tạo policy/store/key hierarchy thứ hai trong installer, landing page hay MCP. Website chỉ là tài liệu/phân phối, không đọc vault, nhận credentials/token hoặc cấp quyền.
- Không sửa ACL/DACL/owner/permissions, registry/autostart/service registration, firewall, global PATH/environment hoặc Codex/OMP profiles bởi agent, kể cả thông qua installer/tests/scripts. Owner tự thực hiện host-mutating steps sau khi được nêu rõ command/API/resource, tác động và rollback; agent chỉ build, inspect và exercise repo-local/staged paths an toàn. Không thay host acceptance bằng staged proof.
- Không private transcript/provider/network inference ngoài consent. Screenshots và examples dùng synthetic data; không vault, raw sessions, recovery key, bearer hoặc handoff contents trong website/artifacts công khai.

### Verification Boundary — Release Only

- Bỏ các gate ban đầu yêu cầu chạy lại CLI-only/UI-only owner workflows, normal Codex/OMP end-to-end, security/lock/revoke scenarios, guide walkthroughs trên vault và owner functional walkthrough cuối sprint. Lý do: owner xác nhận các chức năng/flows đã hoàn thành ở Sprint 9.
- Chỉ kiểm tra phần mới của Sprint 10: build/package/dependencies/version/hash/provenance; bộ cài và install/upgrade/uninstall/coexistence ở ranh giới phân phối; executable launch/help/version để phát hiện lỗi package; website render/navigation/downloads và tính nhất quán tài liệu. Không dùng các kiểm tra này để mở lại functional acceptance.
- Hướng dẫn CLI/UI và Codex/OMP được đối chiếu với accepted Sprint 9 evidence, help/schema và runbook hiện hành; không chạy lại kịch bản sản phẩm hoặc clients chỉ để xác minh tài liệu. Ma trận scenario bên dưới là coverage nội dung, không phải test matrix.
- Không đổi product/security requirements hoặc sửa historical PASS. Nếu gặp concrete functional regression, ghi finding và báo owner quyết định scope correction; không tự mở functional regression campaign hoặc che lỗi để phát hành. Thay đổi hành vi thực sự, nếu được authorize riêng, vẫn phải có focused verification theo engineering contract.
- Evidence review/checkpoint/final differential chỉ đánh giá changed release/install/site/docs scope và traceability tới Sprint 9; không yêu cầu owner lặp lại nghiệm thu chức năng. Public publication authorization giữ riêng, không phải functional walkthrough.

## Release Deliverables

| Deliverable | Contract 1.0.0 | Acceptance boundary |
| --- | --- | --- |
| CLI Windows installer | Asset cài CLI riêng, kèm shared runtime/dependencies cần thiết; CLI-only owner workflows không cần UI, SDK, repo hoặc PowerShell glue | Install/launch/help/version/upgrade/uninstall thật; không chỉ zip hoặc installer chung yêu cầu chọn CLI bằng internal flags |
| UI Windows installer | Asset cài Owner UI riêng, kèm shared runtime/dependencies cần thiết; mở bằng Windows, không thay UI workflows đã nghiệm thu | UI installer thực sự có giao diện cho owner, chọn install/data locations và launch; không bắt gõ installer command hoặc dùng CLI bootstrap |
| MCP Server package | Gói độc lập, self-contained, command/args stdio rõ ràng cho Codex/OMP; đi cùng shared runtime cần thiết | Package layout/dependencies/entrypoint/version và instructions đúng; kế thừa client/flow acceptance Sprint 9, không chạy lại clients |
| Landing page + guides | Trang public có download cho ba sản phẩm, quick starts CLI/UI, scenario guides, Codex/OMP integration, security/FAQ/troubleshooting | Render responsive, links/assets đúng exact release; nội dung đối chiếu accepted Sprint 9 contracts/evidence, không functional walkthrough |
| Release records | Source revision, versions, asset sizes/SHA-256, dependency/license notices, release notes, supported platforms/known limits và compatibility instructions | Có thể trace downloaded asset về reviewed candidate; không public private evidence hoặc fake signing claims |

Tên asset/installer technology/layout, signing và hosting được chốt ở T1 sau inventory hiện có; bảng trên là outcome bắt buộc, không mặc định MSI/MSIX, certificate, domain hay CI credentials đã tồn tại. MCP không bắt buộc có bộ cài Windows thứ ba.

## Cross-Product Installation Contract

- CLI, UI và MCP vẫn là ba entrypoints trên một application/core; shared host không phải sản phẩm thứ tư user tự orchestration. Mỗi package mang đủ dependencies để chạy độc lập.
- Cài CLI rồi UI, UI rồi CLI, hoặc dùng MCP với một owner frontend phải chọn được cùng instance/data root và cùng authoritative state, không hai writers hoặc tự tạo vault copy. Runtime packaging ownership/version selection phải được định nghĩa trước installer implementation.
- Upgrade không overwrite settings/data/key, không reset vault hoặc auto-migrate schema ngoài existing explicit owner contract. Host đang chạy phải được xử lý bằng shared drain/lock lifecycle; refusal có remediation, không thay binaries dưới process mà báo thành công giả.
- Gỡ một frontend không phá frontend/runtime còn được dùng; gỡ app không xóa vault, original sources, snapshots, backups hoặc recovery material. Reinstall mở lại đúng dữ liệu khi owner có key; restart luôn locked và không hồi sinh sessions/tokens.
- Default không autostart, không autonomous unlock và không global PATH/profile edits. Hướng dẫn CLI phải dùng được bằng installed executable path; optional host integration chỉ do owner thực hiện tường minh trong phạm vi được duyệt.
- Signing status phải trung thực: checksum xác minh bytes, không chứng minh publisher identity. Không hướng dẫn disable SmartScreen/Defender hoặc nới policy. Nếu thiếu certificate/host approval gây chặn distribution, ghi blocker và để owner quyết định; không tự coi unsigned là signed hoặc bỏ gate.

## Execution Route

- **Full OMP route** cho từng task: fresh explicitly selected worker → independent evidence review PASS → fresh Bronze exact-snapshot checkpoint; cuối sprint differential deep review. Main chỉ planning/gate/orchestration, không implementation/Git/evidence records.
- Thứ tự **T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8 → final deep → owner publication gate**. Không shared checkpoint batch. Chỉ một sprint implementation task một thời điểm; tối đa hai active agents theo project contract.
- Routing authority của Sprint 9 không tự kéo dài sang Sprint 10. Trước launch phải có explicit role hoặc scoped routing authority và kiểm tra effective non-blocking/async definitions theo current `omp-subagent-flows`.
- Hai review rounds mặc định; Main chỉ cấp round 3 khi có progress evidence và bounded correction/check; otherwise `blocked: owner_decision_required`. Không giảm acceptance để tránh FAIL.
- Root `F:/ai-ml/my-meta-brain`, target ref `main`; brief phải nêu exact base/ownership/task/attempt và gates. Worker evidence `artifacts/sprint-10/task-<N>.md`, reviewer `reviews/`, checkpoint `checkpoints/`, handoff/state dưới cùng root; giữ private/generated outputs ngoài Git. Không tạo PASS records trước execution.
- Planning này không authorize commit/push/tag/release upload, deploy website, mua domain/certificate hoặc thay máy. Per-task checkpoints chỉ được authorize trong execution flow sau review PASS; external publication cần owner authorization riêng.

## Atomic Tasks

Status: `[ ]` pending / `[~]` active or blocked / `[x]` accepted with evidence and checkpoint.

- [x] **S10-T1 — Chốt release contract và freeze manifest.** Requirements: R24, R32, R35, R36, R39, R40.
    - Scope: inventory S9 package/installer/release script/runbooks; chốt product versions, candidate identity, asset names/layout, supported Windows target, install/data-root/runtime ownership và coexistence/upgrade rules. Đánh giá reuse installer trước dependency/framework mới; ghi architectural choices theo `log-decision` nếu cần.
    - Acceptance: release matrix bao phủ ba products, hai installers riêng và landing page; defined lifecycle không phụ thuộc frontend khác; signing/hosting/license/third-party prerequisites được ghi là available, owner action hoặc blocker, không assumed. Chốt repo-local website source/build/preview path và public publication destination do owner kiểm soát, không tự provision.
    - Evidence: inventory có source pointers, review release contract cho fresh/existing roots và uninstall/coexistence cases; phân biệt agreed requirements với proposed choices. Không chạy functional walkthrough hoặc tạo product-runtime PASS từ research.
    - Accepted: [release contract](../release-contract.md), [R1 PASS](../../artifacts/sprint-10/reviews/S10-T1-R1.md), [C1 checkpoint](../../artifacts/sprint-10/checkpoints/S10-T1-C1.md), commit `75d552b3353f8560feb233417bff7d909ada9fe1`; no functional retest/publication.

- [x] **S10-T2 — Reproducible release assembly và asset integrity.** Requirements: R32, R35, R36, R40. Dependency: T1.
    - Scope: reuse release tooling để tạo candidate CLI/UI installer payloads và MCP package từ cùng reviewed source, self-contained win-x64; manifest/hash/version/provenance, dependency/license notices và sanitized release notes.
    - Acceptance: build từ documented repo-local command; output không chứa SDK prerequisite, vault/config secrets/private artifacts; manifest khớp từng distributed asset và file set. Candidate rebuild có identity rõ, không claim deterministic byte equality nếu chưa prove. Public stable assets immutable; mismatch fail closed ở verification, không bỏ qua hash failure.
    - Evidence: assembly thật, verify hashes/layout/version; launch/help/version smoke staged entrypoints ngoài source/build tree để phát hiện missing dependencies/package defects, không provision/unlock hoặc chạy product flows. Installer final artifacts được hoàn tất và verify ở T3/T4, không nhận payload là bộ cài đã hoàn thành.
    - Accepted: [R1 PASS](../../artifacts/sprint-10/reviews/S10-T2-R1.md), [C1 checkpoint](../../artifacts/sprint-10/checkpoints/S10-T2-C1.md), commit `0c36cf8b5fdc0e309d59eaa820a8d71faa8cae09`; actual assembly/integrity/package smoke, CLI/UI payloads chưa phải installers; no functional retest/publication.

- [x] **S10-T3 — Bộ cài Windows riêng cho CLI.** Requirements: R02–R05, R32, R34–R36, R39. Dependency: T2.
    - Scope: CLI installer riêng trên contract T1, reuse shared installer/lifecycle thay vì tạo core/config store mới; setup UX, installed executable discovery, upgrade/uninstall và lỗi rõ ràng.
    - Acceptance: owner cài từ asset mà không SDK/repo/script; installed CLI launch/help/version đúng. Upgrade/reinstall không overwrite data/settings; uninstall giữ data và không phá shared UI/MCP package dependencies. Path có spaces/Unicode, cancel/failure và runtime/version conflict ở ranh giới installer có kết quả/remediation trung thực. Không chạy lại provision/unlock/capture/search/lock hoặc owner workflows.
    - Evidence: safe staged install/upgrade/uninstall/coexistence checks, file-preservation/hash checks và installed executable smoke; mọi host-mutating install/registration do owner thực hiện với exact candidate hashes, actions/results và rollback instructions. Missing required host installation proof là blocker, không yêu cầu owner functional walkthrough.
    - Accepted: [R2 PASS](../../artifacts/sprint-10/reviews/S10-T3-R2.md), [C1 checkpoint](../../artifacts/sprint-10/checkpoints/S10-T3-C1.md), commit `907fc995ded4d2c8fafd37494e3a7ef2d168f2fb`; exact A2 asset owner install/help/version/uninstall/data-preservation proof captured by reviewer. [R1 history](../../artifacts/sprint-10/reviews/S10-T3-R1.md) retained; no functional retest/publication.

- [x] **S10-T4 — Bộ cài Windows riêng có giao diện cho Owner UI.** Requirements: R02–R05, R30–R36, R39. Dependency: T3.
    - Scope: UI installer riêng, owner chọn vị trí phù hợp và mở app sau install; integrate shared instance/runtime contract, không bootstrap bằng CLI/PowerShell.
    - Acceptance: download asset → graphical setup → launch UI thành công, không CLI bootstrap. Upgrade/uninstall/reinstall giữ data/settings; CLI-before-UI và UI-before-CLI không overwrite shared installation hoặc xóa runtime còn dùng. Không chạy lại select/provision/unlock/capture/search/lock hay UI workflows đã nghiệm thu.
    - Evidence: actual graphical installer visual/interaction proof, installed UI launch, setup keyboard/navigation/cancel/error và installation coexistence/uninstall checks; không exercise product views hoặc dùng vault data để test flows. Host mutations chỉ owner thực hiện; staged proof không thay required actual install proof.
    - Accepted: [R1 PASS](../../artifacts/sprint-10/reviews/S10-T4-R1.md), [C1 checkpoint](../../artifacts/sprint-10/checkpoints/S10-T4-C1.md), commit `cfb060c29e3d2b5e1aecd791cae3d3c88c92a02a`; owner direct double-click/install/open/close/remove observation accepted separately from staged hash/preservation/coexistence proof. No owner PowerShell prerequisite or functional retest/publication.

- [x] **S10-T5 — MCP Server 1.0.0 distribution và integration instructions.** Requirements: R24, R32, R35, R36, R40. Dependency: T4.
    - Scope: finalize standalone MCP package, version/help/command/args, installed-runtime compatibility và known limits; không agent unlock/owner authority hoặc profile auto-edit.
    - Acceptance: standalone package đủ shared runtime/dependencies, entrypoint/help/version đúng và command/args/path quoting phù hợp packaged layout. Codex/OMP compatibility và agent/security flows kế thừa Sprint 9; không initialize/discovery/request/redeem/retrieval/revoke replay hoặc client end-to-end gate mới.
    - Evidence: package inspection/hash/version và entrypoint smoke; đối chiếu config method, supported client versions và instructions với accepted S9-T5/S9-T7 evidence và current help/schema. Không launch normal clients, sửa profiles hoặc xin lại client access/consent để lặp functional proof.
    - Accepted: [R1 PASS](../../artifacts/sprint-10/reviews/S10-T5-R1.md), [C1 checkpoint](../../artifacts/sprint-10/checkpoints/S10-T5-C1.md), commit `35e01ec1c6d80bac41d8d2fe7aeef8144f3d34f7`; actual package hash/member/dependency/license/help/version proof, client compatibility inherited from S9, no functional replay/publication.

- [x] **S10-T6 — Landing page và release download surface.** Requirements: R04, R24, R30, R35, R36, R40. Dependency: T5.
    - Scope: static landing page giới thiệu đúng product/threat model, phân biệt CLI/UI/MCP, ba download actions, platform/version/checksum/signing status, quick-start navigation, scenario index, security/FAQ/troubleshooting và release notes. Reuse repo docs, không copy internal plans/evidence thành public site.
    - Acceptance: responsive desktop/mobile, keyboard/accessibility cơ bản, links/download labels đúng asset/version; không private paths/content/secrets, tracking hoặc provider calls. First screen cho biết chọn sản phẩm nào và đi đâu để bắt đầu; không claim chống same-owner compromise hoặc thu hồi context đã gửi model. Public release links chỉ active khi assets thực sự published, không placeholder download báo thành công.
    - Evidence: build/serve repo-local và inspect actual browser desktop/mobile, navigation/download behavior, broken links và asset hashes. Local preview không phải public deployment; hosting/domain/CI credential work thuộc owner-controlled publication gate.
    - Accepted: [R1 PASS](../../artifacts/sprint-10/reviews/S10-T6-R1.md), [C1 checkpoint](../../artifacts/sprint-10/checkpoints/S10-T6-C1.md), commit `cfb647633800819db91f9cb1286206618b1697cb`; actual desktop/mobile browser render/navigation and candidate metadata proof. Local preview only; no public deployment.

- [x] **S10-T7 — Scenario guides CLI/UI và kết nối Codex/OMP.** Requirements: R03–R09, R16–R19, R24, R30, R31, R34–R36, R39, R40. Dependency: T6.
    - Scope: viết guides theo scenario matrix dưới đây; mỗi owner scenario có đường CLI và UI riêng, không bắt đổi frontend. Codex/OMP có trang riêng với config syntax/version/platform/path quoting đối chiếu accepted Sprint 9 contracts/evidence; liên kết landing page, README và operations runbook để tránh instructions mâu thuẫn.
    - Acceptance: mỗi guide nêu prerequisites, hành động/command chính xác, expected result, refusals/recovery và privacy implications; phân biệt owner action với agent action. Không cần script glue/manual internal JSON hoặc đọc source; integration snippets không có key/bearer, không auto-edit profiles. Ví dụ expiry phải còn hợp lệ theo hướng dẫn, không copy timestamp cũ thành recipe cố định.
    - Evidence: content review với source pointers tới accepted Sprint 9 evidence, existing help/schema và runbook; kiểm tra examples, prerequisites, quoting, links và privacy claims. Chỉ preview/render tài liệu, không chạy lại terminal/UI/client flows, không fresh user-onboarding functional PASS.
    - Accepted: [R2 PASS](../../artifacts/sprint-10/reviews/S10-T7-R2.md), [C1 checkpoint](../../artifacts/sprint-10/checkpoints/S10-T7-C1.md), commit `86626bda2908235c02392d421f2117b36c3d11cb`; seven scenario sections, separate Codex/OMP pages and link/fragment/browser evidence. [R1 history](../../artifacts/sprint-10/reviews/S10-T7-R1.md) retained; CLI proposal-undo absence disclosed, no invented command or product flow replay.

- [x] **S10-T8 — Final release integrity và publication handoff.** Requirements: R24, R32, R35, R36, R40. Dependency: T7.
    - Scope: freeze final source/assets/site revision, tổng hợp release/install/site/docs evidence và public-release handoff; không functional/security/client replay, owner functional walkthrough hoặc provider/performance rerun.
    - Acceptance: final CLI installer/UI installer/MCP package đúng hashes từ accepted build; install/upgrade/coexistence/uninstall-preservation evidence đúng candidate; release notes/known limits/license/signing/download matrix đầy đủ và functional acceptance trace được về Sprint 9. Không unresolved actionable release/install/docs finding hoặc commit_pending; concrete functional finding phải được owner xử lý scope trước phát hành. Delta sau acceptance phải review/checkpoint và verify changed release paths trước refreeze, không mặc định rerun flows.
    - Evidence: candidate provenance + release-only verification matrix + inherited S9 functional evidence links, sanitized publication checklist và rollback/withdrawal instructions. Không owner walkthrough mới hoặc fresh functional PASS. Public artifacts không chứa private worker evidence. Sau per-task PASS/checkpoint, final differential review release scope phải PASS trước owner public publication.
    - Accepted: [R1 PASS](../../artifacts/sprint-10/reviews/S10-T8-R1.md), [C1 checkpoint](../../artifacts/sprint-10/checkpoints/S10-T8-C1.md), commit `e088409288d1870d97fc46b4a619e3bc22510fd4`; MIT candidate `1e2953ae6bf4`, package-member delta verified with product/runtime unchanged. Final differential and actual publication pending.
    - [D1 finding F1](../../artifacts/sprint-10/reviews/S10-DEEP-D1.md) resolved: final MCP count 396. [R2 PASS](../../artifacts/sprint-10/reviews/S10-T8-R2.md), [C2 checkpoint](../../artifacts/sprint-10/checkpoints/S10-T8-C2.md), commit `29af04e4d8db121e4aa850ad4b44d4594ecb55e0`; docs-only delta, assets/product unchanged.
    - Final differential: [D2 PASS](../../artifacts/sprint-10/reviews/S10-DEEP-D2.md), zero actionable findings; release source `29af04e4`. Publication executor role explicitly selected Bronze; actual public page/assets smoke remains required before sprint completion.
    - Owner-requested post-publication cutover: public assets `MetaBrain-CLI-1.0.0.exe`, `MetaBrain-UI-1.0.0.exe`, `MetaBrain-MCP-1.0.0.zip`, giữ nguyên bytes/SHA-256/tag; rename existing assets, không duplicate, old filename links ngừng hoạt động. Owner explicitly authorizes `S10-T8-CYCLE2` tối đa hai review rounds, rồi checkpoint/differential/publication. Include published-status correction và README Pages/canonical OMP links; CYCLE1 R1/R2 PASS và R3 superseded giữ nguyên.
    - Owner additions within current cutover: README milestone status must match actual PLAN milestone definitions (Sprint 9 completion is not automatically M9 completion); correct Vietnamese heading typography reported on public site using local/system fonts, with desktop/mobile visual verification and no remote-font dependency.
    - Final acceptance: [CYCLE2 R2 PASS](../../artifacts/sprint-10/reviews/S10-T8-CYCLE2-R2.md), [C4 checkpoint](../../artifacts/sprint-10/checkpoints/S10-T8-C4.md), commit `ac8421829f645bdee73c45dab1abd3d1d7af6909`; [D5 PASS](../../artifacts/sprint-10/reviews/S10-DEEP-D5.md), zero actionable findings. [P4 public smoke](../../artifacts/sprint-10/publication/S10-PUBLISH-P4.md) and [P5 final sync](../../artifacts/sprint-10/publication/S10-PUBLISH-P5.md): three short-name assets preserve IDs/bytes/digests; Pages `6c75b8f2` live, desktop/mobile typography verified, README M1–M9 complete and canonical OMP/Pages links current. Immutable `v1.0.0` points to `29af04e4`; subsequent source commits are documented release/site updates, not rebuilt binaries.
    - Published: [Release 1.0.0](https://github.com/triet4p/my-meta-brain/releases/tag/v1.0.0) and [landing page / guides](https://triet4p.github.io/my-meta-brain/). No Sprint 9 functional/flow reruns; exact final installer bytes were not owner-run, per accepted delta.

## Required Scenario Guide Matrix

| Scenario | CLI guide | UI guide | Integration/result cần giải thích |
| --- | --- | --- | --- |
| First use | Cài CLI, chọn root, start, provision/recovery, unlock, status, lock | Graphical install, launch, select/create instance, provision/recovery/unlock/lock | Startup locked; giữ recovery key ngoài website/model; start khác unlock |
| Ghi nhanh research/note | Capture text + zone + metadata tùy chọn, read-back | Inbox capture rồi đọc lại | Lưu nguồn trước model, không fetch URL hoặc tự gọi provider |
| Nhớ lại lỗi/quyết định cũ | Search/filter/detail/evidence/current-history | Library search/filter/open evidence | Attribution/citations, missing source, stale index và explicit rebuild |
| Dùng lại session coding | Allowlist/map/enroll/ingest/refresh/preservation | Sources workflow tương ứng | Codex/OMP originals read-only; không scan toàn profile, snapshot opt-in |
| Agent nhớ đúng phạm vi | Publish catalog, preview/narrow/approve request, local handoff, sessions/revoke | Sharing workflow tương ứng | Hai bài connect Codex và OMP riêng; request không phải permission; granted retrieval và lock/revoke denial |
| Giữ đề xuất và liên kết | Review/publish/reject/undo; relation operations | Inbox review + Relations | AI proposal không thành user-written; related không tự thành supports |
| Kết thúc và bảo toàn dữ liệu | Lock/restart, encrypted backup/restore, upgrade/uninstall | UI lock/backup/restore và installer workflow | Key loss không fake recovery; uninstall giữ vault; restart không hồi sinh quyền |

Đây là ma trận nội dung hướng dẫn, không phải danh sách flows phải test lại trong Sprint 10. Các advanced existing workflows (provider jobs/budget/egress, intentions/schedules, lifecycle/purge/export, diagnostics) có đường dẫn tới runbook đúng version; ghi rõ functional evidence kế thừa Sprint 9, không claim fresh execution. New cloud/model execution không là prerequisite của quick start và cần consent/budget riêng.

## Decisions / External Prerequisites

- T1 phải resolve installer format/framework và dependency approval từ existing implementation; chưa mặc định MSI/MSIX hoặc chọn thư viện mới trong planning.
- Certificate/publisher identity, hosting/public URL, release publication rights và supported Windows test environments chưa được chứng minh ở lần planning này. T1 inventory trước; chỉ hỏi owner phần không có trong repo/tools và có tradeoff/material authorization thật.
- Owner execution decisions: release 1.0.0 **unsigned**, công bố rõ trạng thái và SHA-256 (không claim publisher authentication); đích dự kiến **GitHub Pages + GitHub Releases** của repository owner kiểm soát, cần xác minh URL/visibility/rights. Quyết định này không authorize publicize repository, bật hosting, push/tag/upload/deploy hoặc thay đổi host.
- Agent không tự cài toolchain, mua domain/certificate, chỉnh CI secrets, hệ thống hoặc profiles. Nếu thiếu prerequisite, hoàn tất mọi repo-local work reachable và ghi rõ command/API/resource cùng tác động/rollback cho owner.
- Functional freeze không bỏ security requirements; concrete functional/security regression phải ghi finding và báo owner quyết định correction riêng, không tự mở lại functional testing scope Sprint 10. Domain expansion không thuộc Sprint 10 mặc định.

## Sprint Acceptance and Publication Gate

1. T1–T8 accepted evidence và exact-snapshot checkpoints, final differential PASS trong release/install/site/docs scope; functional/flow/normal-client/owner-walkthrough acceptance kế thừa Sprint 9, không test lại. Không unresolved required installer/publication proof hoặc actionable release finding; concrete functional finding phải được owner xử lý trước publication.
2. Owner authorize riêng việc push/tag `v1.0.0`, upload release assets và deploy landing page; phải kiểm tra tag/asset collision trước, không rewrite existing published version. Nếu `v1.0.0` đã tồn tại khác snapshot, dừng để owner quyết định version/distribution, không overwrite.
3. Owner hoặc executor được authorize thực hiện external publication đúng frozen hashes/site revision; thao tác đụng host/system vẫn chỉ owner. Kiểm tra public page, từng download/hash và integration links thực sự reachable từ vị trí công bố, không chỉ local preview hoặc draft release.
4. Chỉ đánh **release 1.0.0 published / Sprint 10 complete** sau actual publication và public smoke; nếu candidate đã pass nhưng chưa có authorization/public URL/assets thì ghi **release-ready, publication pending**, không claim đã release.

Execution được owner authorize; owner chọn **Silver riêng cho từng S10-T3, T4, T5, T6, T7, T8** trong một batch routing, gồm corrections/replacements cùng role; T1/T2 cũng đã chọn Silver. Không hỏi routing lại trong scope đã chọn, không tự đổi role. Full route và per-task reviewed-snapshot checkpoint giữ nguyên; worker không commit. Không test lại chức năng/flows Sprint 9. T1/T2 accepted theo links trên; chưa push/tag/upload hoặc deploy website, publication cần owner authorization riêng.

Owner clarification trong execution: từ thời điểm này, kết quả `ask` auto-selected do timeout được coi là câu trả lời của owner; không hỏi lại chỉ vì nhãn auto. Không hồi tố các timeout trước clarification; không thay đổi prohibition host mutations hoặc tự authorize hành động chưa được hỏi.

Owner publication decisions superseding earlier prerequisites: authorize push reviewed commits, tag `v1.0.0`, upload exact frozen assets to GitHub Releases và deploy GitHub Pages cho `triet4p/my-meta-brain` sau T8 evidence/checkpoint và final differential PASS; kiểm tra collision, không overwrite/rewrite. Owner chấp nhận documented verified delta từ các installer đã owner chạy tới final candidate, không yêu cầu chạy lại và không claim final bytes đã owner-run. Latest owner instruction: **MIT License, giữ copyright**; supersedes lựa chọn all-rights-reserved/no-project-license trước đó. Third-party terms giữ riêng; cập nhật license/site/release/package trước freeze và review. Không authorize host/system mutations; nếu cần owner đổi settings/quyền ngoài scope thì dừng đúng prerequisite.
