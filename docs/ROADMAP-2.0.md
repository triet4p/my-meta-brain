# Meta Brain — Roadmap tới 2.0.0

- Ngày lập: 2026-10-29. Trạng thái: **Sprint 12 Complete, 1.2.0 published; Sprint 13–18/G2 planned, chưa authorize launch.**
- Cơ sở: [định hướng hậu 1.0.0](POST-1.0-DIRECTION.md), phản hồi owner và yêu cầu mỗi sprint có một release 1.x.x.
- [PLAN](PLAN.md) là overview/status; task atomic nằm ở từng sprint bên dưới. 1.0.0 và các gate lịch sử giữ nguyên.
- Cập nhật owner-approved 2026-11-01: [CR03](REQUIREMENTS.md#approved-change-record--cr03-2026-11-01)/D089 chuyển Sprint 12+ sang CLI + MCP, browser chỉ document preview/edit. Approval này là planning/support contract, không authorize implementation/publication.

## 1. Đích đến

**2.0.0 = encrypted file-first workspace + cognitive memory + relational/hybrid recall + CLI đủ dùng hằng ngày**, với đường chuyển dữ liệu thật từ 1.0/1.1 và optional browser document preview/edit. Không gọi file viewer, prototype hoặc engine chưa nối CLI/MCP là completion.

Owner làm việc theo folder/file quen thuộc; Meta Brain giữ định dạng nguyên bản, thêm trí nhớ phân loại và quan hệ có nguồn gốc, rồi phục vụ tìm kiếm của owner và context cho LLM. Mã hóa vẫn bắt buộc khi nghỉ; giảm thao tác cấp quyền không đồng nghĩa bỏ authorization tại ranh giới chia sẻ.

## 2. Release train và dependencies

| Sprint | Release cuối sprint | Outcome dùng được | Entry |
| --- | --- | --- | --- |
| [11](sprint-plans/sprint-11.md) | 1.1.0 | Folder enrollment và raw-first ingestion có trạng thái partial trung thực | 1.0.0 accepted; approval cho changed parser contract |
| [12](sprint-plans/sprint-12.md) | 1.2.0 | Workspace mã hóa qua CLI: file/folder, identity, original read/export, backup; CLI parity và WPF support transition | 1.1.0 published; approved storage/key contract |
| [13](sprint-plans/sprint-13.md) | 1.3.0 | CLI lexical search và `open`: scoped browser preview/edit, revision/conflict/lock | 1.2.0 published; document bridge security/budget contract |
| [14](sprint-plans/sprint-14.md) | 1.4.0 | Sáu góc nhìn cognitive memory, owner review và lifecycle | 1.3.0 published; approved taxonomy |
| [15](sprint-plans/sprint-15.md) | 1.5.0 | Hybrid passage search và recall theo mục đích | 1.4.0 published; approved engine/budgets |
| [16](sprint-plans/sprint-16.md) | 1.6.0 | Quan hệ file/folder và relational retrieval giải thích được | 1.5.0 published |
| [17](sprint-plans/sprint-17.md) | 1.7.0 | Folder sharing dễ hiểu và context có bằng chứng qua normal Codex/OMP | 1.6.0 published; approved sharing contract |
| [18](sprint-plans/sprint-18.md) | 1.8.0 | Migration opt-in, recovery, daily-use/performance acceptance và readiness 2.0 | 1.7.0 published |
| [G2 — final release gate](#8-g2--final-release-200-không-phải-sprint-bổ-sung) | 2.0.0 | Default cutover, clean API/storage cutover và public major release | 1.8.0 published; G2 gates PASS và owner approval |

Thứ tự tuần tự; không bắt đầu task độc lập tiếp theo khi required checkpoint hoặc publication của sprint trước còn pending. 1.1.1/1.2.1… dành cho patch cần thiết, không thay release outcome của sprint và không tự thêm scope. Nếu delay, giữ version planned, không đánh dấu complete bằng zip candidate.

### Quy tắc compatibility

- Sprint 11/1.1.0 giữ ba products lịch sử. **Từ Sprint 12/1.2.0 tới G2 chỉ phát hành CLI installer + MCP ZIP**; browser document assets/bridge (từ 1.3) thuộc CLI/shared runtime, không UI installer hoặc web console riêng. Các surface dùng cùng application/core/runtime.
- Trong 1.x, giữ hành vi/API và dữ liệu legacy cho các CLI/MCP workflow. CR03 là ngoại lệ owner-approved cho việc ngừng phát triển/phát hành WPF, không phải permission xóa owner capability hoặc breaking data/protocol change. Workspace additive và opt-in; không tự chuyển vault cũ hoặc thay nghĩa grant cũ.
- Workspace mới dùng format version rõ. Không hứa executable 1.0 đọc format mới; app upgrade, data migration và binary downgrade là ba thao tác khác nhau.
- Không giữ hai authoritative copies hoặc dual-write một tài liệu. Trong giai đoạn 1.x, dữ liệu legacy chưa migrate có một owner; dữ liệu workspace có một owner. Migration là chuyển ownership tường minh, không tạo alias/shim hoặc sync hai truth stores.
- Shared capabilities dùng một domain/policy implementation; không xây engine/security stack thứ hai cho workspace. G2 bỏ obsolete write/API paths sau khi mọi caller đã migrate; không xóa archive/backup hoặc dữ liệu chưa chuyển để làm sạch giả.
- Breaking default/API/schema changes và removal chỉ ở 2.0.0 sau owner-approved change control, thông báo compatibility và recovery path. Nếu một increment không thể additive an toàn, chặn task để owner quyết định, không lén ship breaking change dưới version 1.x.
- WPF 1.1.0 frozen; downloads/evidence 1.0/1.1 giữ nguyên, không hứa old UI dùng new runtime/data. S12-T1/T5/T6 phải chốt và exercise installed component/version mismatch cùng explicit owner transition/remediation trước 1.2.0, không silent replace/delete component hoặc data. Compatibility/removal không thuộc CR03 vẫn cần approval riêng.

## 3. Requirement candidates và traceability

Các mã P2 dưới đây là acceptance candidates cho roadmap, **không tự thay R01–R40**. Task contract/budget cụ thể phải được duyệt trước implementation tương ứng.

| ID | Outcome | Direction mục | Sprint chính | R hiện hành cần đối chiếu |
| --- | --- | --- | --- | --- |
| P2-01 | Chọn folder, refresh giới hạn phạm vi, raw-preserved/partial/unsupported rõ; attribution không bịa | 1, 8 | 11 | R16–R19, R36, R39 |
| P2-02 | Encrypted original bytes, key/recovery/lock, folder identity và export | 3, 4 | 12 | R04, R10, R33–R36 |
| P2-03 | CLI read/revise/search; optional browser preview/text-edit đúng revision, không web workbench | 3, 7, 9 | 13 | R10, R30, R32, R39; CR03 |
| P2-04 | Sáu memory facets, multi-label passages, owner corrections bền vững; AI không thành belief | 5 | 14 | R01, R12, R13, R20–R22 |
| P2-05 | Temporal/intention/forgetting và history không mất qua mô hình mới | 5, 6, 11 | 14, 18 | R26–R29, R39 |
| P2-06 | Exact/lexical/structural/semantic Việt–Anh, passage citations và region results | 6, 7 | 15 | R23, R25, R38 |
| P2-07 | Typed file/folder relations, provenance, stale evidence và bounded traversal | 6 | 16 | R08, R14, R15 |
| P2-08 | Folder scope preview, revoke/lock/expiry, không mở quyền qua link hoặc membership ngầm | 4 | 17 | R03, R05–R08, R11, R31 |
| P2-09 | Context pack có nguồn, budget, contradictions/gaps; normal Codex/OMP dùng thật | 7 | 17 | R07, R09, R24, R25 |
| P2-10 | Lazy/streaming/bounded cache, responsive CLI/document browser, measured retrieval quality | 9, 12 | 13, 15, 18 | R23, R30, R37, R38; CR03 |
| P2-11 | Lossless migration/recovery/backup/export/purge và clean cutover | 11 | 12, 18, G2 | R29, R33–R36, R39 |
| P2-12 | Usable release mỗi sprint: S11 ba products; S12+ CLI installer/MCP ZIP, docs/integrity/owner publication | 10 | 11–18, G2 | R24, R32, R35, R40; CR03 |

### Change-control gates

S11-T1 đã hoàn tất CR02/D088 approval; S11-T2–T5 implemented và 1.1.0 published. CR03/D089 owner-approved ngày 2026-11-01 thay delivery/support contract từ Sprint 12; không blanket-approve các storage/key/share/engine decisions tương lai.

- Parser/folder enrollment: CR02 approved/implemented trong Sprint 11; không mở lại như gate pending.
- File/storage/key/metadata disclosure and installed-WPF transition: owner approved [D090](META-BRAIN.md#134-approved-workspace-contract--d090) for S12; implementation and proof remain required before affected writes/package.
- CLI + narrow document browser đã được chọn bởi CR03; S13-T1 chốt bridge authorization, rendering/editor dependencies, encoding và budgets bằng evidence, không chọn lại desktop framework. Taxonomy và engine chốt ở đầu sprint tương ứng.
- Folder snapshot/continuous scope: giải quyết trước S17-T2. Snapshot là phương án conservative để so sánh, không quyết định thay owner; nếu chọn continuous phải nêu file/revision mới, moves và revocation semantics.
- Migration/default/API removals: giải quyết trước S18 migration execution và G2 cutover.

Nếu approval của contract còn mở chưa có, task affected là blocked; không thực thi dưới baseline mâu thuẫn. CR03 chỉ approve planning/support change; không tạo runtime evidence, authorize worker launch hoặc thay CR01 security.

## 4. Kiến trúc và trải nghiệm xuyên suốt

- File gốc, owner-authored metadata/relations và derived index là ba trách nhiệm khác nhau. Index mất có thể rebuild; owner corrections không mất theo index.
- File/folder IDs survive rename/move; content/tree revisions and citation anchors, copy, replacement and conflicts follow [D090](META-BRAIN.md#134-approved-workspace-contract--d090).
- Mã hóa cả nội dung/private metadata/index/scratch/backup; chỉ export plaintext do owner chọn. Agent không nhận key; cùng quyền OS owner và bản đã chia sẻ vẫn ngoài khả năng bảo vệ tuyệt đối.
- Reuse the existing vault root key/lifecycle per D090; no agent key, custom crypto or unjustified second key store. Further subkey/format choices requiring approval are not decided by D090.
- Markdown/CSV/JSONL/text là scope ban đầu: original-byte round-trip cho cả file chưa có editor; editing theo supported matrix, không rewrite JSONL sessions nguồn. Không thêm PDF crawler hoặc mọi định dạng Office mặc định.
- Folder là không gian, memory facet là ý nghĩa, relation là đường nối; không dùng folder membership thay evidence hoặc authority.
- Giữ C#/.NET core/application, Windows x64 và versioned pipes. CLI owner-complete; MCP scoped agent surface. Browser không key/vault access hoặc owner API tổng quát; localhost/Origin/body không tạo authority.
- `metabrain open <id>` là tên lệnh dự kiến từ S13: foreground local document bridge, phiên giới hạn document/revision/operations/lifetime; không plaintext HTML/temp/draft/cache ngầm. Markdown sanitized/no active content hoặc remote assets; CSV preview bảng và raw-text edit trước, không WYSIWYG/grid rewrite; source sessions read-only. Preview/toggle không đổi bytes, save explicit expected-revision; unsupported format báo đúng.
- Lock/disconnect/expiry/CLI exit revoke read/save, clear document/draft, bỏ stale responses; không app-managed browser persistence, service worker hay private content/credential trong URLs/logs. Không claim browser/OS/extension erasure tuyệt đối hoặc thay host/browser policy; export plaintext là hành động riêng tường minh.
- Existing coding/research/trend/synthesis jobs, review, intentions, relations, source operations, catalog/request/token/session và backup/purge phải giữ đầy đủ qua CLI/shared APIs; browser không mở rộng thành console quản trị để thay WPF.
- S12-T2–T6 inherit D090's encrypted canonical owner metadata, deterministic sibling names, original-byte export, tombstone/purge/recovery and refusal of mixed WPF/runtime installations. S13 anchors, S16 relations, S17 sharing and S18 migration build on that contract without granting permissions or approving migration by implication.

## 5. Verification và performance contract

S11-T1 cố định bộ scenario synthetic, host/environment, corpus sizes và phương pháp đo để theo dõi từ release đầu. S13-T1/S15-T1/S18-T1 chốt budgets/quality thresholds trước tuning trong scope tương ứng; owner duyệt mọi thay đổi R38/D086, không biến target đề xuất thành hard gate hoặc nâng ngưỡng sau FAIL.

Theo dõi tối thiểu: startup/unlock; folder import/append; CLI search/read/revise và optional open latency; peak/idle RAM; cache/scratch/disk; document responsiveness khi indexing; context size và retrieval relevance/citation correctness. Quality set có Việt–Anh, exact symbols, paraphrase, temporal, causal/action–effect, prospective, preference và multi-hop; có expected evidence, negatives/out-of-scope và held-out queries để tránh chỉ tối ưu câu mẫu.

Mỗi task phải exercise surface thật bằng dữ liệu synthetic/được owner cho phép: CLI/service, browser thật chỉ khi document path bị ảnh hưởng, hoặc normal MCP client tùy changed path; không WPF gate mới. Test suite không thay runtime smoke. Lock, wrong key, tamper, cross-scope, partial-schema, crash và document-bridge authority/rendering negatives theo changed contract. Không replay toàn bộ Sprint 9/10 chỉ để có PASS mới; private transcripts/provider/network vẫn cần consent riêng.

## 6. Workflow thực thi

Tất cả sprint 11–18 và G2 chọn **Full route** vì storage/security/API/UX interactions có material risk: mỗi task → worker evidence → independent review PASS → Bronze exact-snapshot checkpoint; cuối mỗi sprint assigned differential deep review. Không shared atomic checkpoint batch. Current global `omp-subagent-flows` điều khiển lifecycle, rounds và effective definitions, không copy các routing tóm tắt lịch sử.

Main chỉ plan/gate/orchestrate; chọn explicit Bronze/Silver/Gold theo user role hoặc scoped authority trước launch. Tối đa hai active agents, một implementation task; không implicit Gold, không launch trong lần planning này. Mỗi worker đọc `implement-atomic-task` và enumerate/read mọi regular file trực tiếp dưới `~/.agents/rules/`.

Repository `F:/ai-ml/my-meta-brain`, target `main`; exact base/ref/worktree/ownership được resolve khi thực thi, không bịa từ planning. Task artifact `artifacts/sprint-<N>/task-<M>.md`, reviews/checkpoints cùng sprint; G2 dùng `artifacts/release-2.0/`. Main chỉ cập nhật concise status links, không tạo evidence/checkpoint records trước execution.

Publication task được tách thành candidate preparation → task gates/checkpoint → final differential → owner-authorized push/tag/upload/Pages → exact public bytes/links smoke. Không tự upload trước deep review. Publication pending chặn sprint complete và sprint tiếp theo. Gate review sau public smoke chỉ kiểm tra delta publication, không tạo circular prerequisite yêu cầu public release trước prepublication deep review.

Agent không thay ACL/owner/permissions, registry, services/autostart, firewall, PATH/global configuration hoặc agent profiles, kể cả qua installer/tests. Repo-local staging không thay real-host proof; host-mutating actions do owner thực hiện và ghi nhận đúng giới hạn. Planning không authorize commit/push/publication hoặc model calls.

## 7. Definition of release mỗi sprint

Release task cuối mỗi sprint chịu cùng contract:

1. Toàn bộ task đạt acceptance/evidence/checkpoint; không unresolved actionable finding hoặc commit_pending. Candidate nối đủ owner CLI và agent surface liên quan; browser chỉ document preview/edit từ S13, không no-op/mock/scaffold.
2. Từ S12 version đồng nhất CLI/MCP/shared runtime và bundled document assets; assets `MetaBrain-CLI-<v>.exe`, `MetaBrain-MCP-<v>.zip`, manifests/digests/source provenance/dependency licenses/signing status trung thực. Không UI installer mới, không overwrite assets lịch sử. S11 giữ nguyên ba-product acceptance.
3. Install/upgrade/uninstall giữ data root/recovery; installed WPF 1.1 transition hoặc version refusal/remediation phải trung thực. Format compatibility, opt-in features và downgrade/restore instructions rõ; không silent partial shared-runtime update.
4. Release notes/changelog, website downloads/scenarios/operations, migrations và known limits đúng changed scope; phân biệt supported CLI/MCP mới với legacy UI downloads. Không commit private artifacts/vault/keys/raw transcripts/generated outputs.
5. Final differential PASS trước publication; owner authority riêng cho external actions. Xác minh public artifacts khớp reviewed candidate; installed CLI/MCP launch và changed flow smoke, actual browser nếu document surface affected; không WPF replay hoặc chỉ build PASS.
6. Chỉ Main ghi sprint Complete sau publication evidence; nếu owner chưa authorize publish, trạng thái là release_pending, không giả “đã có release” bằng local candidate.

## 8. G2 — Final release 2.0.0, không phải sprint bổ sung

G2 là major-release milestone sau tám sprint, **không thay release 1.8.0 cuối Sprint 18**. Nhờ vậy mỗi sprint đều kết thúc bằng 1.x.0 như owner yêu cầu. G2 gồm ba task riêng tuần tự, trạng thái ban đầu `[ ]`:

- [ ] **G2-T1 — Default/API clean cutover.** Entry: Sprint 18 complete, owner chấp thuận migration/removal contract. Chuyển workspace thành trải nghiệm mặc định, migrate mọi supported CLI/MCP/document-bridge/settings caller, bỏ obsolete active legacy write paths/shims và WPF product/packaging callers theo approved mapping. Không cập nhật/revive WPF 1.1; giữ historical assets/source/evidence và owner data. Kiểm tra old-data opening/migration/refusal, protocol mismatch, no two writers và interrupted cutover; không đụng backups nguyên bản. Outcome: một authoritative implementation, retained owner workflows đầy đủ qua CLI, không alias hoặc mất tính năng im lặng.
- [ ] **G2-T2 — Exact 2.0 candidate và release delta acceptance.** Entry: T1 review/checkpoint PASS. Build version 2.0.0, không retag binary 1.8.0; exercise changed defaults/removals, install/upgrade từ 1.0 và intermediate release, recovery và normal client protocol trên candidate thật. So sánh 1.8 acceptance chỉ cho unchanged surfaces; append changed-path evidence. Cập nhật complete feature/requirement mapping, operations/migration/docs/assets/licenses; owner final acceptance và task gates trước differential major-release review.
- [ ] **G2-T3 — Public 2.0.0.** Entry: T2 checkpoint + major differential PASS, no blocker, owner publication authorization. Publish immutable source tag/assets/site, verify exact hashes/links/version và public docs; không provider/functional replay không liên quan. Sau public smoke, Main ghi M18 complete. Nếu bytes thay đổi, review/refreeze delta và rerun affected checks trước phát hành, không sửa asset bất biến.

G2-T1 không được mở nếu 1.x buộc thay contract chưa được duyệt. G2 completion bao gồm toàn bộ P2-01–P2-12 và các R outcomes retained/changed được trace tới evidence thật, owner daily-use acceptance, lossless migration và restore, privacy/lock/scope/egress negatives, performance/quality budgets đã duyệt. Không giảm thành release branding-only hoặc tuyên bố 2.0 chỉ vì đủ version numbers.

## 9. Ngoài phạm vi và status

Không mặc định thêm multi-device sync, SaaS/multi-user, Linux/macOS/ARM, autonomous researcher, crawler/PDF automation, graph database, provider mới, telemetry hoặc auto-update. Multi-platform/UI đổi stack chỉ qua quyết định có lý do, không phát sinh từ roadmap.

Hiện tại: Sprint 11/M10 **Complete**, 1.1.0 published gồm CLI/UI/MCP. Sprint 12/M11 **Complete**, [1.2.0 published](https://github.com/triet4p/my-meta-brain/releases/tag/v1.2.0) gồm CLI installer + MCP ZIP, WPF 1.1 legacy frozen. D090 approved, T1–T6 evidence/checkpoints và [final differential D1 PASS](../artifacts/sprint-12/reviews/S12-DEEP-D1.md); owner-authorized publication và [independent public smoke PASS](../artifacts/sprint-12/reviews/S12-PUBLIC-C1-R1.md). Source/tag `82079457`, candidate `921141ec536c`, exact public asset hashes verified; [site](https://triet4p.github.io/my-meta-brain/) deployed. Sprint 13–18/G2 **Not started**, cần owner authorization riêng, không tự launch.
