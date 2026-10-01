# Meta Brain — Yêu cầu cứng và hợp đồng từng chặng

## Baseline và phạm vi

Baseline 2026-09-28 được owner thay security scope bằng CR01/D016 ngày 2026-10-01; đọc cùng [đặc tả](META-BRAIN.md), [decision log](../.agents/memory/decisions.md) và [PLAN](PLAN.md). MUST/bắt buộc là điều kiện nghiệm thu. Old-baseline Sprint 1 fixture PASS là lịch sử; replacement security gates Pending, Sprint 2–8 Not started. Documentation approval không chứng minh implementation.

Mỗi requirement có một sprint chịu trách nhiệm chính và có thể cần nhiều task bảo toàn nó. Sprint sau không được làm hỏng requirement đã pass. Các yêu cầu cross-cutting vẫn áp dụng trước sprint nghiệm thu chính nếu surface tương ứng đã xuất hiện.

## Requirement Registry

| ID | Yêu cầu bắt buộc | Evidence/điều kiện nghiệm thu | Sprint chính |
| --- | --- | --- | --- |
| R01 | Bộ nhớ trợ giúp, không dẫn dắt user; automation opt-in cho tác vụ thay đổi ý nghĩa | Không tự đổi belief, đóng câu hỏi, chọn agenda; cấu hình mặc định không chạy synthesis/provider hoặc thông báo ngoài lựa chọn user | 6 |
| R02 | Owner sở hữu mọi zone và quản trị; unlock độc lập authorization | Owner không tự xin grant cho mình nhưng phải có key để đọc/ghi vault; quản trị scope/catalog qua owner workflow, agent token không tạo owner authority | 1 |
| R03 | Token đổi một lần lấy scoped session deny-by-default | Random opaque token không là decryption key; server-side grant chốt IDs/revisions, operations, expiry và egress; concurrent redemption chỉ một thành công; replay/expired/revoked/lock/restart không hồi sinh quyền | 1 |
| R04 | Encrypted personal vault với key user bảo quản, không agent sandbox | Authenticated ciphertext và wrong-key/tamper negatives thật; key không tới agent; startup locked, lock dừng private operations; không claim chống agent cùng quyền owner lấy runtime key/plaintext hoặc bảo mật lại original sessions | 1 |
| R05 | Tách owner approval/key management khỏi scoped agent API | Agent token/body is_owner/cwd/localhost không mint/unlock/approve/publish/purge; quyền lấy từ owner-approved grant, không suy OS owner token thành ý định user; same-OS compromise/peer token theft ngoài threat model | 1 |
| R06 | Catalog opt-in tách private index; authorization trước mọi retrieval | Agent chỉ thấy IDs/nhãn/description owner công bố; catalog không mở body/source; search/get/list/count/citation/relation/cache không lộ ngoài session scope, thêm membership/revision/link không tự mở grant | 4 |
| R07 | Provider egress trong consent và trusted budget cho calls ứng dụng | Approval cho cloud-agent là disclosure có provider/model context; internal calls enforce exact pricing/input/output/cost trước send; không claim ngăn agent gửi tiếp plaintext đã nhận | 6 |
| R08 | Dẫn xuất liên vùng không tự mở quyền; declassification do owner | Summary từ A+B mặc định yêu cầu cả hai; publish preview cả metadata/citation và giữ lineage private; revoke/đổi nguồn được xử lý | 6 |
| R09 | Nội dung không là instruction/policy | Payload injection trong session/note không đổi grant, gọi shell đặc quyền, xuất bản hoặc đánh dấu owner confirmation | 6 |
| R10 | Item envelope có ID, revision, zone, kind, provenance, thời gian và applicability | Đổi tiêu đề/path không mất reference; thiếu metadata bắt buộc bị reject; lưu/read round-trip ý nghĩa | 2 |
| R11 | Zone độc lập collection/view/level; scope snapshot khi duyệt | Collection/zone descendants chỉ hỗ trợ chọn và preview; thêm membership/item/revision không tự tăng tập resource scope; link không cấp quyền | 2 |
| R12 | Tách tác giả, executor, question/claim/synthesis, cách tạo và căn cứ | Câu hỏi không thành belief; summary AI được giữ không thành user-written/verified; chỉnh sửa bảo toàn attribution | 2 |
| R13 | Phân tầng L0/L1/L2/L3+ có provenance, không xếp độ đúng theo tầng | Nhiều nguồn/nhiều dẫn xuất; derived_from không vòng; item trực tiếp không bị bịa tầng nguồn; synthesis trace về nguồn còn được phép | 6 |
| R14 | Relation typed, có provenance/revision/lifecycle | Structural link theo stable ID; evidence link gắn revision; nguồn đổi/mất được báo; không biến similarity thành supports | 2 |
| R15 | Explicit linking qua chat/MCP, không giả ý chí owner | Agent tạo/link/unlink rõ ràng trong grant; ambiguous endpoint được làm rõ; executed_by khác owner-confirmed; link không share dữ liệu | 4 |
| R16 | Source và item có quyền API riêng; originals ngoài bảo vệ vault | Import/recall không sửa/rename/move/delete .codex/.omp; chỉ owner allowlist; đọc item không tự cho source expansion; không claim ngăn original-source OS access của agent | 3 |
| R17 | Incremental ingestion Codex/OMP đáng tin cậy | Append, partial tail, nhập lại, restart, replacement/truncation, format không hỗ trợ không làm trùng/mất im lặng; checkpoint/attribution chính xác | 3 |
| R18 | Reference-only mặc định, encrypted preservation opt-in và availability | Snapshot/cache/chunk/locator trong Meta Brain encrypted, không plaintext index/temp; locked dừng private ingestion/expansion; missing source nói rõ, không bảo mật lại original transcripts | 3 |
| R19 | Capture research phi cấu trúc nhanh, lưu nguồn trước xử lý | Nhập text + zone, metadata tùy chọn; ghi chú còn sau restart/provider lỗi; URL không bị coi là full paper đã đọc | 3 |
| R20 | Profile coding/research/trend cùng model, không bóp nghĩa | Phân biệt tool result, claim tác giả, giới hạn, giả thuyết user, câu hỏi; trend chỉ nói trên corpus đã lưu và không đếm lặp nguồn như xác nhận độc lập | 6 |
| R21 | Synthesis chọn lọc và phân tầng giữ điều kiện/ngoại lệ | Job hữu hạn/opt-in; bản tổng hợp có citations, limitations, contradictions, revision và invalidation; không summary drift âm thầm | 6 |
| R22 | Job/model không làm mất dữ liệu hoặc vượt authority | Input/output scope, provider/cost settings, cancel/fail/restart rõ; model output được validate, malformed output không vào published truth | 6 |
| R23 | Recall hữu ích với Việt–Anh, symbol, thời gian và paraphrase | Corpus tình huống owner cho phép; kết quả có evidence/applicability/status; stale không ưu tiên như current; không cần leaderboard | 4 |
| R24 | MCP/tool interface thật cho Codex và OMP chạy bình thường | Catalog/request/redeem-session/search/get/propose/link/feedback qua cùng application policy; bridge không giữ key/đọc vault/mint quyền; không AppContainer hoặc patched Codex dependency | 4 |
| R25 | Brief có giới hạn và feedback không tự tăng truth | Brief dựng lại từ quyền hiện tại, có citations/uncertainty, không system instruction; lượt đọc không nâng verification | 4 |
| R26 | Thời gian đúng ngữ nghĩa và truy vấn lịch sử | Occurred khác recorded; validity/review_after, unknown/timezone; superseded/historical và contradiction không newest-wins | 7 |
| R27 | Intention theo thời gian/ngữ cảnh, không tự hoàn thành | Pending/done/cancelled/superseded; due còn khi offline, acknowledged khác completed; chỉ trigger đã được bật | 7 |
| R28 | Quên phân cấp, có giải thích và bảo vệ item hiếm quan trọng | Demote/archive/pin/restore; không tự xóa vì ít đọc hoặc củng cố truth bằng read count | 7 |
| R29 | Purge đầy đủ trong phạm vi kiểm soát, không hứa xóa bên ngoài | Preview canonical/index/cache/snapshot/derivatives/backups; session nguồn chỉ xóa nếu cấp quyền riêng; nêu retention và dữ liệu đã xuất/đã đọc không thu hồi được | 7 |
| R30 | UI owner cho library/capture/review/relations/source history | Thao tác thật trên UI, keyboard cơ bản, Việt–Anh; trả lời được vì sao nhớ và ai thấy; không UI giả nối mock backend | 5 |
| R31 | UI unlock/catalog/access requests và scoped sessions | User unlock/lock, preview/publication catalog, review/thu hẹp/approve/reject requests, handoff token, expiry/revoke; dùng core policy, không sửa config bằng tay hoặc protected-runtime launcher | 5 |
| R32 | Ba thành phần tách ranh giới, một domain/policy implementation | Core không lệ thuộc UI/MCP SDK; adapters không ghi vault; UI/MCP dùng cùng policy và persistence; không cần microservices | 2 |
| R33 | Encrypted file-first canonical, private index rebuildable và durable writes | Plaintext readable chỉ sau owner unlock/explicit export; ciphertext revision conflict/crash/rebuild/migrations đúng; index/WAL/journal/temp không dual truth hoặc plaintext leak | 2 |
| R34 | Encrypted backup/restore, key recovery và explicit scoped export | Restore cần key, tái hiện lineage/lifecycle, không hồi sinh live sessions/used tokens/purged data; lost key không fake recovery; plaintext export có owner disclosure, không gồm private citations ngoài scope | 8 |
| R35 | Install/start/restart/upgrade/uninstall đủ dùng hằng ngày | App startup locked, owner unlock và hai client thường dùng được; không sandbox/account/patch prerequisites; upgrade không reset, uninstall không xóa vault/source/key material khi chưa đồng ý | 8 |
| R36 | Private metadata/index/audit/cache/credentials và artifacts được bảo vệ | Encrypt sensitive persisted product data; chỉ approved catalog/minimal format envelope công khai; không commit vault/transcript/key/token; không log raw secrets/body; same-owner OS hostile access ngoài cam kết | 8 |
| R37 | Chất lượng định tính trên công việc thật, không thay bằng benchmark | Owner walk-through recall lỗi cũ/quyết định/research/link/synthesis/intentions; ghi nhận sai/cũ/thiếu; không finding nghiêm trọng chưa giải quyết | 8 |
| R38 | Operational envelope đo được, khóa trước tuning | Sprint 1 ghi số đo/giới hạn dataset, p95, indexing, disk/idle resources và model budget; Sprint 8 đo lại không đổi mục tiêu để che fail | 8 |
| R39 | Recovery và migration không được né bằng reset dữ liệu | Interrupted writes/jobs, service restart, index corruption, schema upgrade có kịch bản recovery; dữ liệu báo saved không mất | 8 |
| R40 | Yêu cầu cứng, kế hoạch và evidence trung thực | Thay baseline qua change control, cập nhật traceability; task chỉ [x] sau gate, không claim runtime/test chưa chạy | 8 |

## Hợp đồng mỗi chặng

| Chặng | Entry gate | Exit gate không được cắt giảm |
| --- | --- | --- |
| 1 — Encrypted vault và scoped sharing | Owner-approved CR01; synthetic fixtures, không private transcript/provider call mặc định | Key/unlock/lock thật; encrypted fixture reads; owner-published catalog; request preview/approval; single-use token và scoped session/revoke/expiry/restart; real Codex/OMP bình thường, không patched/sandbox dependency |
| 2 — Durable encrypted core | CR01 Sprint 1 replacement gate pass, không dùng old PASS thay thế | Domain/persistence/relations/source metadata trên encrypted file-first store; protected private-index recovery; migration/cutover và downstream contracts thật |
| 3 — Capture | Sprint 2 pass; allowlist nguồn đã duyệt | Hai adapter JSONL thật + text research, read-only sources, resume/idempotence và source-loss/preservation rõ |
| 4 — Agent access | Sprint 3 pass | Hai client catalog → request → owner approve → token redeem → scoped retrieval/source/proposal/link; lock/revoke/cache negatives và consent, không OS isolation claim |
| 5 — Owner UI | Sprint 4 pass | UI unlock/recovery, catalog publication, request review/token handoff, access preview cùng capture/library/relations/source thật |
| 6 — Structuring | Sprint 5 pass; unlocked vault và owner provider/input/budget consent | Real model jobs, encrypted outputs, cross-zone owner publication và injection/egress/lock gates, không raw key vào model |
| 7 — Lifecycle | Sprint 6 pass | Temporal/lifecycle thật, privacy-safe locked scheduling, purge/catalog withdrawal không resurrection hoặc xóa original source trái ý |
| 8 — Production | Sprint 7 pass | Installer startup locked; encrypted backup/restore/recovery/export/migration; CR01 security replay, frozen budgets, R01–R40 và owner acceptance |

## Evidence và Definition of Done

Mỗi sprint plan ghi acceptance cho từng task, downstream contracts và gate scenario. Evidence chứa phiên bản môi trường, command/scenario thực chạy, quan sát, requirement IDs và giới hạn. Dữ liệu riêng tư không được sao chép nguyên vào artifact.

Trong OMP, giữ task [~] đến khi evidence-reviewer không còn finding actionable; deep-reviewer chỉ chạy sau mọi task gate. Correction giữ task ID, cập nhật artifact và chạy lại cùng review level. Không coi output worker hoặc test pass là thay thế runtime smoke.

Docs-only planning này chỉ kiểm tra nội dung, links và coverage; không tạo báo cáo sprint pass hay test sản phẩm giả. Khi triển khai, mỗi sprint có section Gate record cho Main cập nhật actual evidence.

## Change Control — chỉ đổi khi có bằng chứng tốt hơn

Requirement không tự đổi vì gặp khó hoặc muốn pass. Owner có thể phê duyệt thay đổi product/threat model sau evidence/tradeoffs; phải gọi rõ phần bảo vệ bỏ đi và outcome mới, cập nhật traceability, dữ liệu, security, downstream và gates.

Quy trình bắt buộc:

1. Ghi requirement/task IDs bị ảnh hưởng, baseline hiện tại và evidence thực tế; chỉ rõ điều gì không còn phù hợp.
2. Đề xuất lựa chọn mới và alternatives; giải thích lợi ích cho sản phẩm dùng hằng ngày, security, maintainability, dữ liệu hiện có và các sprint sau.
3. Liệt kê tác động tới compatibility/migration, quyền, UI, verification và toàn bộ dependency chain. Không giấu giảm scope dưới tên refactor.
4. Owner phê duyệt thay đổi yêu cầu/scope trước khi thực hiện. Chọn implementation không thay contract có thể do worker quyết định, nhưng lựa chọn kiến trúc đáng kể vẫn phải ghi decision log.
5. Append decision có ID/tham chiếu evidence vào .agents/memory/decisions.md; giữ lịch sử, không viết đè quyết định cũ. Cập nhật đồng bộ đặc tả, registry này, PLAN và sprint plans liên quan.
6. Mở lại gate bị ảnh hưởng và verify/review lại. Ghi requirement mới thay thế requirement nào; không xóa dấu vết một requirement chưa hoàn thành.

## Approved Change Record — CR01 (2026-10-01)

Owner đã chấp thuận encrypted vault, catalog chọn lọc và opaque single-use token đổi lấy scoped session, đồng thời bỏ trách nhiệm sandbox Codex/OMP và bảo mật lại original agent sessions. Approval là các trao đổi threat-model/key-vs-token và yêu cầu “Document lại và điều chỉnh sprint 1 và các sprint sau”; [D016](../.agents/memory/decisions.md) lưu alternatives, evidence và giới hạn.

- Replacement requirements: R03–R06, R16, R18, R24, R31, R33–R36; R02, R07, R11 và R29 giữ outcome, làm rõ unlock/disclosure/scope/copies. IDs giữ ổn định để trace lịch sử; không coi old R04 OS-isolation PASS là new R04 encryption PASS.
- Removed acceptance: sandbox/process/peer-credential isolation và protected launcher; same-owner runtime key/token/UI compromise, original-source OS reads và dữ liệu đã chia sẻ ngoài phạm vi.
- Added acceptance: encrypted private canonical/index/metadata/temp/backup; user key/recovery/lock; approved catalog; request review; snapshot resource IDs/revisions; concurrent durable single-use redemption; scoped session/cache/revoke/lock/restart.
- Dependency impact: M1/Sprint 1 reopened; retained S1-T1 stack/envelope evidence, S1-T2–T9 Pending. Sprint 2–8 Not started với entry gates mới; bổ sung S5 key/recovery UI và S8 key/backup recovery. Không nâng các target/budget R38.
- Cutover: S1-CLEAN removes the obsolete protected launcher, AppContainer SID/HMAC bindings, agent endpoints and fixture callers from current source. Retained owner-only status/read is not token, ciphertext, catalog or approval evidence. No data reset/migration or compatibility shims; historical decisions and evidence remain unchanged.
- Supplemental protected model proof superseded/stopped, không PASS, không tiếp tục dùng test key trong lần docs này; giữ unknown reservation ledger. Real model execution được kiểm chứng ở S6-T1 sau owner consent, không kế thừa test credential như production setting.
- CR01 planning change (2026-10-01) was documentation-only: no implementation/model/provider/ACL/service setup, product changelog entry, or owner daily-use acceptance occurred in that planning update. The later source cleanup and user-facing removal entry are tracked separately.
