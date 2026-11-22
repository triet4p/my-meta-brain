# Meta Brain — Yêu cầu cứng và hợp đồng từng chặng

## Baseline và phạm vi

Baseline 2026-09-28 được owner thay security scope bằng CR01/D016 ngày 2026-10-01; đọc cùng [đặc tả](META-BRAIN.md), [decision log](../.agents/memory/decisions.md) và [PLAN](PLAN.md). MUST/bắt buộc là điều kiện nghiệm thu. Sprint 1/M1 replacement complete; Sprint 2/M2 complete (owner-confirmed committed); Sprint 3 In progress (S3-P0 complete; S3-T1 source enrollment/ingestion coordinator accepted with evidence/checkpoint; S3-T2 Codex session JSONL read-only adapter delivered, worker evidence pending review); Sprint 4–8 Not started. Documentation approval không chứng minh implementation.

Mỗi requirement có một sprint chịu trách nhiệm chính và có thể cần nhiều task bảo toàn nó. Sprint sau không được làm hỏng requirement đã pass. Các yêu cầu cross-cutting vẫn áp dụng trước sprint nghiệm thu chính nếu surface tương ứng đã xuất hiện.

**Current scope:** 1.4.0/Sprint 14 published/Complete theo [PLAN](PLAN.md); các trạng thái cũ trong baseline/change records là lịch sử. Owner-approved **CR03/D089 (2026-11-01)** thay owner surface/support contract từ Sprint 12/1.2.0: CLI owner-complete + MCP, browser document preview/edit từ Sprint 13; WPF 1.1.0 frozen. Không coi planning này là implementation hoặc gate PASS.

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
| R16 | Source và item có quyền API riêng; originals ngoài bảo vệ vault | Import/recall không sửa/rename/move/delete .codex/.omp; chỉ owner allowlist; đọc item không tự cho source expansion; không claim ngăn original-source OS access của agent. CR02: preserved bytes chỉ khi owner opt-in và nằm trong purge/retention; không sửa/rename/move source | 3 |
| R17 | Incremental ingestion Codex/OMP đáng tin cậy | Append, partial tail, nhập lại, restart, replacement/truncation, format không hỗ trợ không làm trùng/mất im lặng; checkpoint/attribution chính xác. CR02: record chưa hiểu chỉ được đi qua khi bytes đã preserve bền vững và đọc lại được; version/root barrier không diễn giải tiếp; không silent drop | 3 |
| R18 | Reference-only mặc định, encrypted preservation opt-in và availability | Snapshot/cache/chunk/locator trong Meta Brain encrypted, không plaintext index/temp; locked dừng private ingestion/expansion; missing source nói rõ, không bảo mật lại original transcripts. CR02: preservation mở rộng sang record chưa hiểu khi owner opt-in; reference-only vẫn mặc định; `unprocessed_bytes`/`pending_tail_bytes`/facet trạng thái phải báo thật | 3 |
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
| R29 | Purge đầy đủ trong phạm vi kiểm soát, không hứa xóa bên ngoài | Preview canonical/index/cache/snapshot/derivatives/backups; session nguồn chỉ xóa nếu cấp quyền riêng; nêu retention và dữ liệu đã xuất/đã đọc không thu hồi được. CR02: preserved span/bytes nằm trong preview/purge/backup/export scope | 7 |
| R30 | Owner library/capture/review/relations/source history đầy đủ; CR03: CLI từ S12 | 1.0/1.1 giữ UI evidence lịch sử. S12+ mọi owner workflow có CLI thật, keyboard/Việt–Anh, provenance/ai-thấy-gì và revision conflicts; không cần WPF/browser. S13 optional document preview/edit có actual browser evidence, không web console | 5 historical; 12–18 CR03 |
| R31 | Owner unlock/catalog/access requests và scoped sessions; CR03: CLI từ S12 | Unlock/lock, catalog preview/publication, narrow/approve/reject requests, protected token handoff, expiry/revoke qua shared core, không sửa config bằng tay. Browser document session không unlock/mint/approve hoặc nhận key; 1.0/1.1 UI gates giữ nguyên | 5 historical; 12, 17 CR03 |
| R32 | Core, application và connections tách ranh giới, một domain/policy implementation | Ba thành phần kiến trúc không đồng nghĩa ba products. Core không lệ thuộc UI/MCP SDK; CLI/MCP/document bridge không ghi vault hoặc tự cấp quyền; browser không key/store/policy riêng | 2; 12–18 CR03 |
| R33 | Encrypted file-first canonical, private index rebuildable và durable writes | Plaintext readable chỉ sau owner unlock/explicit export; ciphertext revision conflict/crash/rebuild/migrations đúng; index/WAL/journal/temp không dual truth hoặc plaintext leak | 2 |
| R34 | Encrypted backup/restore, key recovery và explicit scoped export | Restore cần key, tái hiện lineage/lifecycle, không hồi sinh live sessions/used tokens/purged data; lost key không fake recovery; plaintext export có owner disclosure, không gồm private citations ngoài scope. CR02: preserved bytes không tái tạo được sau khi nguồn đổi → phải vào backup/restore/export scope | 8 |
| R35 | Install/start/restart/upgrade/uninstall đủ dùng hằng ngày | App startup locked, owner unlock và hai client thường dùng được; không sandbox/account/patch prerequisites; upgrade không reset, uninstall không xóa vault/source/key material khi chưa đồng ý. CR03: S12+ CLI installer/MCP ZIP, installed-WPF component transition hoặc explicit refusal/remediation, không UI installer mới | 8; 12–18/G2 CR03 |
| R36 | Private metadata/index/audit/cache/credentials và artifacts được bảo vệ | Encrypt sensitive persisted product data; chỉ approved catalog/minimal format envelope công khai; không commit vault/transcript/key/token; không log raw secrets/body; same-owner OS hostile access ngoài cam kết. CR02: preserved bytes encrypted, diagnostic chỉ code+offset, không body | 8 |
| R37 | Chất lượng định tính trên công việc thật, không thay bằng benchmark | Owner walk-through recall lỗi cũ/quyết định/research/link/synthesis/intentions; ghi nhận sai/cũ/thiếu; không finding nghiêm trọng chưa giải quyết. CR03: S18 CLI daily-use acceptance và optional document browser thật, không WPF replay | 8; 18 CR03 |
| R38 | Operational envelope đo được, khóa trước tuning | Sprint 1 ghi số đo/giới hạn dataset, p95, indexing, disk/idle resources và model budget; Sprint 8 đo lại không đổi mục tiêu để che fail | 8 |
| R39 | Recovery và migration không được né bằng reset dữ liệu | Interrupted writes/jobs, service restart, index corruption, schema upgrade có kịch bản recovery; dữ liệu báo saved không mất. CR02: journal+checkpoint atomic với preserved span; truncation/replacement vẫn quarantine | 8 |
| R40 | Yêu cầu cứng, kế hoạch và evidence trung thực | Thay baseline qua change control, cập nhật traceability; task chỉ [x] sau gate, không claim runtime/test chưa chạy. CR02/CR03 approval + sync docs không thay product PASS; future release/support change không sửa historical acceptance | 8; 12–18/G2 CR03 |

**D090 clarification for S12 onward:** R04/R10/R18/R29/R33–R36/R39 retain their existing protections and additionally apply the [approved workspace contract](#owner-approved-workspace-contract--d090-2026-11-01); approval is not implementation or acceptance evidence.

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
| 8 — Production | Sprint 7 pass | Installer startup locked; encrypted backup/restore/recovery/export/migration; CR01 security replay, frozen budgets (riêng T5 dùng baseline vận hành owner chấp nhận 2026-10-23 / D086; các invariant bounded/encrypted giữ nguyên), R01–R40 và owner acceptance |

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
- Dependency impact: M1/Sprint 1 replacement complete; M2/Sprint 2 complete; Sprint 3 In progress (S3-P0 complete; S3-T1 source enrollment/ingestion coordinator accepted with evidence/checkpoint; S3-T2 Codex session JSONL read-only adapter delivered, worker evidence pending review); Sprint 4–8 Not started. Bổ sung S5 key/recovery UI và S8 key/backup recovery. Không nâng các target/budget R38.
- Cutover: S1-CLEAN removes the obsolete protected launcher, AppContainer SID/HMAC bindings, agent endpoints and fixture callers from current source. Retained owner-only status/read is not token, ciphertext, catalog or approval evidence. No data reset/migration or compatibility shims; historical decisions and evidence remain unchanged.
- Supplemental protected model proof superseded/stopped, không PASS, không tiếp tục dùng test key trong lần docs này; giữ unknown reservation ledger. Real model execution được kiểm chứng ở S6-T1 sau owner consent, không kế thừa test credential như production setting.
- CR01 planning change (2026-10-01) was documentation-only: no implementation/model/provider/ACL/service setup, product changelog entry, or owner daily-use acceptance occurred in that planning update. The later source cleanup and user-facing removal entry are tracked separately.

### Owner-approved Sprint 8 execution clarification — 2026-10-20

- R04/R05/R35/R36 vẫn bảo vệ bằng encrypted vault/user key/lock và approved scoped APIs, không thêm filesystem ACL hoặc OS-isolation acceptance. Agent bị cấm thay đổi ACL/DACL/owner/permissions và cấu hình/tài nguyên hệ thống, kể cả fixture/temp và execution gián tiếp; authorization fixture ACL trước đó không còn hiệu lực.
- Giữ outcomes R01–R40, frozen targets, full task evidence/checkpoint và release acceptance. S8-T1 thêm scope correction → package → staged lifecycle → owner-assisted host acceptance; S8-T2–T7 giữ thứ tự, prerequisites/authorization/evidence được chuẩn bị sớm.
- Data/security impact: không đổi encrypted format/key hierarchy, không sửa quyền dữ liệu hiện hữu, không reset hoặc migrate original sessions. Scoped correction phải loại ACL prerequisite trên đường package/start/unlock có liên quan và bảo toàn encryption/owner-agent API authorization; không mở broad permission refactor.
- Host install/autostart/client setup/logout/reboot do owner thực hiện, không agent thay đổi máy; missing evidence vẫn là blocker. Đây là planning/rule change, không chứng minh correction đã chạy, runtime PASS hoặc owner acceptance.

### Owner-approved S8-T5 correction — bounded-memory, encrypted-at-rest private index (2026-10-22)

- **R23 và R38 giữ nguyên outcome, ngưỡng và phương pháp đo.** Không đổi mục tiêu, không đổi số đo, không
  đổi tập corpus/truy vấn, không thêm benchmark thay thế. Owner duyệt **hướng hiện thực** của private
  index, không duyệt việc nới ngưỡng: 256 MiB idle-RAM, 250/500/1.000 ms recall p95 theo cỡ dữ liệu,
  ngưỡng rebuild 100k và R37 vẫn là ngưỡng.
- **Luật hiện thực (R23, R36, R38 đọc cùng nhau).** Private index là derivative rebuildable của
  canonical — canonical là truth duy nhất, không dual truth, không reset vault để né cutover. Index
  phải **mã hóa at-rest bằng AEAD đã được duyệt của vault**, với key suy ra từ data key của chính
  vault đó (không key riêng, không tự unlock): không plaintext index, temp, staging, journal, WAL
  hay spill file nào được ghi xuống đĩa. Generation hiện hành là artifact persistent được AEAD bảo
  vệ; rebuild có thể tạo staging, posting spill và directory spill nhưng từng record chỉ chứa
  ciphertext dưới key suy ra từ data key của vault, dùng AEAD/associated-data domain hiện có và
  identity file riêng cho scratch. Không thêm crypto mới; scratch còn sót được dọn ở lần rebuild
  tiếp theo. Record generation authenticate identity của generation, kind và offset; generation
  identity gắn canonical digest, để tamper, truncate, stale hay thay thế file đều **fail closed**.
  Only explicit, capped structures may grow with the canonical set: one immutable manifest
  descriptor snapshot (at most 100,000 rows), released before the live reconciliation snapshot;
  no vault-wide collection of bodies, search documents, token strings or posting pairs. The posting
  arena is 4 MiB with at most 65,536 pairs per run; encrypted spill chunks are at most 49,160 B,
  merge fan-in is 8, and source-derived run references are capped at 49 retained across levels.
  Final posting chunks are at most 32,768 postings / 256 KiB; bucket pages at most 384 entries /
  256 KiB. The resident directory is capped at 32 MiB and rejected by count-derived preflight
  before its arrays are allocated; the default decrypted-record cache is capped at 16 MiB and
  oversized records are not cached. Build/open transient ciphertext and plaintext buffers remain
  bounded and are measured separately; these caps do not claim zero process-private or managed
  memory.
- **Luật truy vấn (R23, R26, R30 đọc cùng nhau).** Truy vấn phải stream/batch qua **toàn bộ** ứng
  viên trong scope và score tất cả: không cap, không top-K approximate, không đổi tie order, token
  equality/collation, scope/revision/date/current/provenance/score gate. Ứng viên ngoài scope không
  được rời engine. Đọc body của ứng viên chỉ xảy ra ở những nơi contract đã yêu cầu (bộ lọc
  level, và trang được phục vụ), không phải để dựng index.
- **Lifecycle (R04, R33, R35, R39 đọc cùng nhau).** Lock/restart xóa key, cache và mọi private read;
  interrupted rebuild không publish gì; generation không đọc được thì owner rebuild tường minh, và
  canonical không bao giờ bị đụng. Index không nằm trong backup (là derivative rebuildable) và
  được dựng lại sau restore.
- **Bằng chứng (R38, R40).** Số đo phải chạy trên sản phẩm thật với phương pháp đã khóa (≥1.000
  truy vấn đo sau ≥100 warm-up mỗi cỡ), phải báo riêng working set/private/native/managed/cache
  bytes, cold và warm, thời gian rebuild và throughput, và p95 so với **ngưỡng không đổi**. Phép đo
  in-process là bằng chứng về index và đường truy vấn, **không** thay bằng chứng service/UI thật và
  **không** thay phán đoán owner R37.

### Owner-approved S8-T5 acceptance cutover — 2026-10-23 (D086)

Thay đổi này chỉ sửa **tiêu chí nghiệm thu T5**; nó không sửa source, sản phẩm, test hay dữ liệu, và
không tạo PASS cho bất kỳ kết quả cũ nào. Nó thay thế phần ngưỡng định lượng của mục
"Owner-approved S8-T5 correction — bounded-memory, encrypted-at-rest private index (2026-10-22)" ở trên
đúng như change-control yêu cầu; các quyết định và FAIL report cũ vẫn nguyên vẹn.

- **Bị thay thế làm cổng sign-off T5.** Recall p95 250/500/1.000 ms ở 1.000/10.000/100.000 record và
  rebuild 100k ≤ 20 phút không còn là điều kiện pass/fail của T5, cùng với yêu cầu đo lại bắt buộc
  handler p95 ở 1k/10k. Đây là các ngưỡng đã miss ở một số run (p95 100k ở A13/A15/A17/A18; rebuild
  100k ở A18) hoặc chưa từng đo được (handler 1k/10k), và owner đã quyết định không dùng chúng làm
  cổng nữa. A14 đo p95/rebuild/đĩa trong ngưỡng nhưng vẫn có RAM proxy bất lợi — kết quả đó cũng giữ
  nguyên.
- **Bản ghi lịch sử không bị xóa.** Các ngưỡng trên vẫn là **operational envelope Sprint 1** và mọi kết
  quả đã đo cùng mọi miss đã ghi (A11, A13, A14, A15, A17, A18; R11/R13/R14) vẫn là sự thật, giữ nguyên
  trong [đặc tả](META-BRAIN.md) và [runbook](OPERATIONS.md). Không có PASS nào được tạo cho chúng và
  không con số lịch sử nào bị viết lại.
- **Baseline vận hành được chấp nhận (quan sát, không phải ngưỡng mới).** Run A18-2 (2026-10-23, 100.000
  record, in-process, 100 warm-up + 1.100 truy vấn) là mốc owner chấp nhận là thực tế dùng được:
  handler p50/p95 183.937 / 1.668.026 ms; rebuild 1.396.956,547 ms (23 phút 16,957 giây); idle WS
  207.084.032 B unlocked / 204.846.357 B locked; idle CPU 0,0709 %/0,0729 %; đĩa vault+staging
  1.132.272.603 B; comparator 1.101 dòng, 0 lệch trường chung. Đây là **số đo lịch sử của snapshot
  in-process**, không phải service/UI/IPC và không phải bằng chứng cho mọi version sau; thay đổi này
  **không** tạo trần số mới và **không** nới budget CPU/đĩa/RAM nào.
- **Điều kiện sign-off T5 mới.** (a) Định lượng: baseline A18 ở trên được ghi nhận là mức hiệu năng
  thực tế owner chấp nhận cho 100k, không cần đo lại cho thay đổi này (Gold cap 2/2 đã dùng hết).
  (b) Hành vi (A19/R14): generation mã hóa đã xác thực và khớp canonical được **tái sử dụng sau
  lock/unlock và restart mà không ghi lại file**; generation thiếu/stale/không đọc được/tamper/wrong-key
  **fail closed** (`index_not_materialized`, `search_index_stale`, `index_unavailable`) và **rebuild đầy
  đủ tường minh của owner** là đường phục hồi; lock xóa key/cache/private read; format hiện tại không có
  delta từng record nên thay đổi canonical đi qua rebuild tường minh.
- **Invariant giữ nguyên.** Resident directory/cache bounded theo các cap đã ghi (32 MiB directory /
  16 MiB cache và các cap builder), index/cache/temp mã hóa at-rest bằng AEAD và khóa suy ra từ data key
  của vault, không plaintext, không crypto mới; scope/revision/currentness/provenance/ranking/tie order,
  lock và currentness correctness không đổi; không host/ACL/DACL/permission/system mutation.
- **Không miễn trừ toàn cục.** Thay đổi này **không** thay acceptance của S8-T6 (security/recovery replay)
  hay S8-T7 (owner daily-use và R37 walkthrough định tính trên công việc thật), không thay evidence
  real service/WPF/owner UI/IPC, không thay review độc lập và không thay checkpoint. R23 vẫn cần phán
  đoán owner về độ hữu ích, không suy từ số đo.
- **Tác động dữ liệu/security/downstream.** Không đổi format/crypto/key, không migration, không reset,
  không đụng dữ liệu hiện hữu; A19 chỉ đọc generation đã xác thực nên không mở surface mới. R38 giữ
  nguyên tắc "đo và ghi lại, không tự đổi mục tiêu để che fail": thay đổi này **không** phải để che một
  fail — nó là quyết định của owner qua change control, có decision [D086](../.agents/memory/decisions.md),
  và toàn bộ số đo cũ vẫn hiển thị. S8-T5 vẫn `[~]`; các cổng còn mở được giữ nguyên, không xóa.
- **Còn mở, không xóa.** Owner R37 walkthrough định tính, evidence real service/WPF/owner UI/IPC, review
  độc lập trên snapshot sau thay đổi và mọi checkpoint vẫn mở. Không product changelog entry: đây là
  thay đổi tiêu chí nghiệm thu/tài liệu, không phải feature.

## Approved Change Record — CR02 (2026-10-29)

Owner đã chọn **“Duyệt gói đề xuất”** cho [CR02 §8](CR02-PROPOSAL.md#8-gói-quyết-định-cho-owner):
**D1=B, D2–D8=A**. Đây là approval thật cho changed parser/folder contract, không suy từ việc bắt đầu
Sprint 11. Quyết định được ghi tại [D088](../.agents/memory/decisions.md); proposal, measurement protocol
và corpus schema 3 là evidence đi kèm.

- **Contract được duyệt.** Record chưa hiểu chỉ được đi qua khi bytes chính xác của record đã được
  preserve bền vững, encrypted, đọc lại được trong cùng giao dịch journal/checkpoint (D1=B). Reference-only
  vẫn là mặc định; preservation là owner opt-in theo nguồn/folder kèm retention (D2=A). Record khai báo
  version/root mới là barrier: preserve tail nhưng không diễn giải tiếp (D3=A). Không lossy transcode;
  record lỗi encoding chỉ recoverable qua preservation (D4=A). Chain identity chỉ được admit bởi
  envelope validator độc lập của format/version; envelope không validate không cấp `id`/`parentId` và
  tham chiếu sau đó bị refusal (D5=A). Không đi theo reparse point; entry bị loại với
  `excluded_reparse_point`, không admit target của link, mỗi file vẫn qua final-path check (D6=A).
  Cross-record parent validation dùng `omp_chain_reference_unresolved` (D7=A). Journal v2 chỉ được ghi
  khi lần ghi thực sự chứa phần tử chỉ có ở v2 (preserved span/status v2-only); source reference-only
  giữ v1, đọc cả v1+v2, không auto-downgrade, schema v3+ fail closed (D8=A).
- **Trạng thái là bốn facet độc lập**: `availability`, `preservation`, `interpretation`, `derivation`.
  `preserved` không hàm ý `partially_interpreted`; `fully_supported` chỉ dùng khi mọi record hoàn chỉnh
  đã diễn giải và không còn byte chưa xử lý ngoài tail dở dang. Mọi owner surface phải hiển thị facet
  đúng, cùng `pending_tail_bytes`, `preserved_bytes`, `uninterpreted_records`, `last_diagnostic`.
- **Requirement IDs giữ nguyên; không xóa/hạ mức requirement nào.** R16–R19, R29, R34, R36, R39, R40 được
  làm rõ điều kiện nghiệm thu trong registry ở trên; R38 không đổi (không thêm ngưỡng hiệu năng mới).
  R01–R15, R20–R28, R30–R33, R35, R37 giữ nguyên.
- **Bảo vệ giữ nguyên.** Nguồn gốc vẫn read-only; preserved bytes là dữ liệu private bổ sung, encrypted
  bằng cơ chế vault hiện có, nằm trong purge/backup/export/retention; diagnostic chỉ code + offset, không
  echo transcript/body; lock dừng private ingestion và preservation; không grant/scope mới cho agent.
- **Compatibility.** Vault/canonical format không đổi; upgrade 1.0 → 1.1 không rewrite journal cũ, không
  migrate/reset dữ liệu; source fully supported phải giữ đúng event/span/digest như 1.0. "Additive" không
  có nghĩa mọi journal chuyển sang v2.
- **Approval-time status (historical).** Khi duyệt CR02, S11-T2–T5 chưa triển khai; corpus expectation
  không phải PASS sản phẩm. Sau đó Sprint 11 đã Complete và 1.1.0 published theo
  [accepted sprint records](sprint-plans/sprint-11.md); approval này tự nó không phải runtime proof hoặc
  authorization cho provider/network, host mutation, migration hay publication tương lai.

### Owner-approved S11-T4 folder refresh policy — 2026-10-31

The owner approved explicit `folder-refresh-optin` / `folder-refresh-optout` consent, default off. The original folder preview/confirmation is not refresh consent. While opted in, the folder policy covers current and future matching regular files under the selected root and stored include/exclude rules; new files are admitted and read only after reparse, root, and final-path boundaries pass. A source-level pause, revoke, or explicit refresh opt-out vetoes the folder policy without being overwritten. Folder opt-out and vault lock stop refresh at the source boundary. Deleted members remain canonical/history and are reported missing; refresh never automatically revokes or purges them. Manual and background refresh share the same coordinator.

This records the approval-time T4 contract, not runtime evidence by itself. T4 subsequently passed its full-route evidence/checkpoint gates and shipped in 1.1.0; see [Sprint 11](sprint-plans/sprint-11.md). CR02/D088 encrypted preservation/retention, atomic journal/checkpoint, compatibility, attribution and scoped-rights constraints remain unchanged.

## Approved Change Record — CR03 (2026-11-01)

Owner chấp thuận hướng CLI-first và yêu cầu chỉnh sprint/global plan, đồng bộ tài liệu từ Sprint 12 sau khi phản hồi rằng duy trì CLI + WPF song song tốn kém và CLI thuận tiện debug/test hơn. [D089](../.agents/memory/decisions.md) ghi lựa chọn, alternatives và impact; [roadmap](ROADMAP-2.0.md) cùng Sprint 12–18/G2 là execution contracts tương lai.

- **Thay requirement nào:** R30/R31 đổi từ bắt buộc desktop UI parity sang CLI owner-complete cho release 1.2.0 trở đi; R32 giữ ba architectural boundaries, bỏ suy diễn ba distribution products. R35/R37/R40 cập nhật release/support/surface evidence theo CR03; các functional outcomes vẫn giữ, không bỏ capture, evidence/review, sources, relations, jobs/synthesis, intentions, catalog/request/token/session hoặc backup/export/purge.
- **Bỏ điều kiện nào:** Không thêm WPF feature, WPF parity/replay gate hoặc `MetaBrain-UI-<v>.exe` cho Sprint 12+; không chuyển toàn bộ console thành web app. Downloads/source/evidence/accepted limits của 1.0/1.1 giữ nguyên, không retroactively đổi PASS hoặc xóa installed component.
- **Surface mới được duyệt:** CLI là đường owner độc lập; MCP vẫn là scoped agent surface. S13 thêm optional `metabrain open <id>` (tên dự kiến), foreground local document bridge và browser hai chế độ preview/edit, chỉ document/revision/operations/lifetime được chọn; không owner API tổng quát, unlock/key/catalog/approval/review console trong browser. S12 không bị đòi browser trước dependency S13.
- **Document safety:** Không key tới browser, không plaintext HTML/temp/draft hoặc app-managed browser storage/cache/service worker; không private content/credential trong URL/log. Request phải được authenticate/bind theo document session, không tin localhost/Origin/body; chặn cross-origin/CSRF và active document HTML/script/remote resources. Preview/toggle không thay original bytes; save tường minh với expected revision, conflict không overwrite. CSV preview bảng, edit raw text trước; Markdown/plain text theo support matrix, sessions nguồn read-only, unsupported binary không giả editable.
- **Lock/lifetime và disclosure:** Lock/disconnect/expiry/CLI exit revoke read/save, bỏ stale response và clear document/draft; unsaved policy không chặn lock. Không hứa tuyệt đối browser/managed-memory/OS-swap/extension erasure hoặc thu hồi copies đã xem; không thay host/browser policy. Plaintext export riêng cần explicit owner disclosure; CR01 same-owner OS compromise exclusion giữ nguyên.
- **Compatibility/data impact:** Không thay crypto/key/storage format hoặc tự migrate/reset dữ liệu trong CR03. WPF 1.1.0 frozen, không cam kết dùng runtime/data 1.2+. S12-T1/T5/T6 chốt và exercise existing CLI/UI component ownership/version mismatch, explicit owner transition hoặc refusal/remediation an toàn trước package mới; không silent shared-runtime replacement, auto-uninstall UI hoặc xóa data. Đây là ngoại lệ có approval cho WPF support withdrawal trong 1.x, không cho breaking CLI/MCP/data changes khác.
- **Verification/traceability:** S12 CLI parity và installed-component transition; S13 actual packaged CLI + browser preview/edit/save/conflict/rendering/authority/lock/exit proof; S14–S17 cognitive/relation/sharing workflows qua CLI và changed MCP paths; S18 CLI daily-use acceptance + optional document-browser lifecycle; G2 clean cutover mọi supported caller. Vẫn independent evidence/checkpoints/differential/publication gates; không thay budgets R38/D086 bằng ngưỡng mới hoặc test suite thay runtime.
- **Release contract:** S12–S18/G2 phát hành `MetaBrain-CLI-<v>.exe` + `MetaBrain-MCP-<v>.zip`; S13+ bundle document bridge/assets trong CLI/shared runtime, không product/installer/browser profile riêng. Website/operations/compatibility guides khi phát hành phải phân biệt supported CLI/MCP với legacy UI 1.0/1.1. Integrity/provenance/licenses/signing disclosure và data-preserving install/recovery giữ nguyên.
- **Chỉ planning:** Approval này không khởi động Sprint 12, chọn implementation package/framework, cấp quyền provider/network/private transcripts/host mutation, commit/push/tag/upload/deploy hoặc tạo runtime/gate/publication PASS. R01–R29, R33–R34, R36, R38–R39 giữ protection/outcome; các change gates storage/key/share/engine/migration vẫn cần approval theo scope riêng.

## Owner-approved workspace contract — D090 (2026-11-01)

The owner approved “Duyệt reuse vault và semantics đề nghị” for D1–D5 and “Refuse mixed; installation tách biệt” for D6, exactly as proposed in `artifacts/sprint-12/task-1.md:16-21`. This supplements CR03 rather than retroactively changing what CR03 approved. [D090](../.agents/memory/decisions.md) records alternatives and consequences; [specification §13.4](META-BRAIN.md#134-approved-workspace-contract--d090) is the detailed normative contract, including deterministic name equality and destination filesystem collisions.

- **R04/R33/R36:** reuse the existing vault root key/lifecycle with no agent key, second store or custom crypto; encrypt workspace bytes, names, hierarchy, owner metadata/relations, index and scratch. Minimal envelope and opaque names are public; ciphertext size/count, timing/access, version and approved catalog disclosure remain visible. Index is derivative, not owner-authored truth.
- **R10/R18/R33/R39:** stable random file/folder IDs, separately checked content/tree revisions, revision-pinned citations and stale anchors; rename/move preserves IDs, copy uses fresh IDs and no inherited grants, explicit replacement retains selected ID and advances content revision. Revision/parent/occupancy conflicts and cycles refuse atomically, with no silent overwrite. External imports are independent explicit copies, not automatic sync or legacy migration; original sessions remain read-only.
- **R29/R34/R39:** reversible encrypted tombstones and confirmed recursive folder delete, occupied-location restore refusal; separate purge preview/confirmation includes derivatives, snapshots, backups and relations, with no false recall/erasure of exports/backups. Restore cannot resurrect purged data, sessions or used tokens; lost both credentials means no recovery. Authenticated versioned workspace/metadata, atomic tree/bytes/metadata publication, encrypted staging and fail-closed recovery are required, not yet proven.
- **R34/R35:** original empty/binary/encoded bytes round-trip; only explicit scoped export creates plaintext and discloses destination/irreversibility. Refuse 1.2 installation over 1.1 WPF or unknown/mixed candidate before stopping/changing files; separate install/data roots/endpoints, no shared live root or competing writers. An independently proven, separately confirmed owner transition is the only alternative. Wire/version mismatch must also fail closed before private operations for manually mixed binaries/endpoints; do not promise old WPF/new runtime compatibility.
- **Downstream and limits:** S12-T2–T6 must implement and exercise these requirements and CLI workflow gaps; existing vault behavior, 1.1 releases and this approval do not prove them. No automatic migration, source-file sync, host mutation, release or publication is authorized by D090; other sharing/engine/migration approvals remain separate.

## Owner-approved hybrid exposure contract — S15-T3 / D104 (2026-11-08)

The owner selected explicit semantic candidate discovery after reviewing measured alternatives: 27/27 labeled evidence retained with unrelated prose returning model neighbours, versus 21/27 under content-term grounding. This replaces the proposed generalized natural-language abstention-before-exposure gate for S15, not scope, revision, attribution or exact-match protections; it does not retroactively establish abstention.

- Hybrid responses MUST distinguish literal matches from unconfirmed semantic candidates with a per-hit class and separate counts, and keep literal matches ahead of semantic-only candidates. Neither class alone establishes truth, support or owner confirmation.
- Semantic retrieval is candidate discovery, not a verified answer or supporting evidence. No similarity cutoff or generalized natural-language abstention is implemented or claimed; identifier-shaped, scope and currentness negatives retain their no-answer behaviour.
- Scope-before-disclosure, exact byte/revision anchors, complete eligible-candidate scoring before global ranking/totals/paging, deterministic ties and lock cancellation remain required. No new grants, cloud/provider access, host mutation or publication authority follows from this approval.

## Owner-approved workspace relation contract — S16-T1 / D107 (2026-11-12)

The owner approved proposal 1–5 and choices A–E (“Duyệt hết”). The normative [endpoint and revision contract](META-BRAIN.md#41-approved-workspace-relation-contract-s16-t1-d107) clarifies these retained requirements; approval is not runtime implementation or acceptance.

- **R10/R14/R15:** Stable file/folder identity and revision/byte-range/digest passage selectors; preserve seven typed directions and lifecycle, explicit attribution and no implied approval. Workspace content-sensitive assertions pin and review both file/passage endpoints, including `contradicts`; `related_to` is identity-only. Legacy evidence-side-only review and identity-only legacy `contradicts` remain distinct. Immutable attributed workspace-edge history is prospective, with no invented legacy acts.
- **R06/R08/R11/R15:** Tree membership, folder applicability and explicit relations neither imply child evidence nor grant access or declassification. Both endpoints and their exact revisions require authority; scope, lock, revoke and non-disclosure apply.
- **R13/R14:** The mixed `derived_from`/`supersedes` union is a DAG across all stored states; creation/reactivation checks are atomic. Other semantic cycles do not authorize unbounded traversal.
- **R23/R25:** Downstream retrieval must respect evidence pins, stale/unavailable state, scope and bounded traversal; similarity alone is not supporting evidence.
- **R29/R33/R34/R36/R39:** Authenticated encrypted canonical edges/history survive backup/restore subject to purge; encrypted adjacency is rebuildable. Scoped plaintext export includes an edge only with both endpoints and required evidence selected, and omits out-of-scope stubs and identifying details. No reset or implied migration.

S16-T2–T5, S17 sharing and S18 migration/portability remain separate work and gates; no existing code or previous evidence proves the new workspace relation contract. See the [Sprint 16 tasks](sprint-plans/sprint-16.md#atomic-tasks).

## Owner-approved workspace folder-scope contract — S17-T1 / D108 (2026-11-16)

The owner approved the recommended default set P1–P9 and choices A–G (“Duyệt”) after the timed-out form, exactly as proposed in [S17-T1 proposal §2–8](../artifacts/sprint-17/task-1.md). The normative [folder-scope contract](META-BRAIN.md#56-approved-workspace-folder-scope-contract--s17-t1--d108) is the detailed future contract; [D108](../.agents/memory/decisions.md) records alternatives and consequences; approval is not runtime implementation or acceptance.

- **Additive clarification, not a baseline change.** No requirement is replaced, renumbered, relaxed or retroactively rewritten, and no protection is withdrawn, so change-control step 3 has no baseline-loss disclosure. The frozen-scope rule in [§5.4](META-BRAIN.md#54-token-đổi-một-lần-và-scoped-session) and R11's scope snapshot stay exactly as approved before; the contract only names how a folder selector resolves into eligible resources. An approved folder scope can never be read as consent to continuous membership: new files, new revisions, copies, moved-in nodes and restored nodes do not enter an existing grant.
- **R03/R06/R11:** One approval mints a frozen set of exact member IDs/revisions at one tree revision plus explicit identity-based exclusions. Folder identity is a selector and preview boundary, never a live authority object; members must remain live descendants of the approved folder, so moving a member out removes authority and moving one in adds none. Grant/token/session redemption, policy generation, expiry, revoke and lock behavior remain unchanged, and folder-based approval does not widen identity/revision matching for old grants.
- **R29:** A deleted/tombstoned member is unavailable, and restoring it creates no automatic re-share without a new owner refresh. Purge, tombstone and non-resurrection behavior are unchanged; no historical acceptance is rewritten.
- **R31:** CLI remains the owner preview/narrow/approve/revoke surface for folder grants, including protected handoff and granted-scope/session inspection. CLI and MCP use one policy with no mandatory per-file approval, and browser document sessions still mint no sharing or owner authority; agent operations cannot mint, approve, publish or revoke.
- **R07/R08/R09:** The output boundary is the exact approved member revisions. `provider.egress` remains a separately required operation with provider/model/cost disclosed in the same approval, cannot be added to an existing grant, and stays bounded by expiry; folder sharing itself performs no provider call, and a folder approval implies neither CR02 source enrollment nor ingestion. Multi-zone synthesis and injection authority are unchanged.
- **R02/R05/R36:** A folder approval mints no owner authority and no key, and persisted folder-scope state stays encrypted with only the minimal observable envelope, length and count behavior. No host, ACL, permission or global configuration change follows.
- **Explicit non-rights.** A workspace folder scope is its own zone-independent scope subject (choice D) and does not fabricate a zone or classification. It grants no registered-source body, cognitive annotation, owner-private classification, derived summary/synthesis, relation evidence or cross-zone derived content; those need their own grants and operations. S17-T4/T5 cognitive, hybrid and relational outcomes and their evidence budgets are neither narrowed nor pre-approved here.

Affected gates R03/R06/R07/R08/R09/R11/R29/R31/R36 and the S17-T2 gate are re-verified when S17-T2–T5 implement and exercise their changed surfaces; this documentation approval creates no runtime evidence, product changelog entry, migration, release or external action.

S17-T3 adds the owner CLI folder-sharing workflow through the shared authority; its actual synthetic CLI/security proof passed the assigned owner workflow and negative scenarios without catalog publication (see the [S17-T3 implementation note](META-BRAIN.md#56-approved-workspace-folder-scope-contract--s17-t1--d108) and [worker evidence](../artifacts/sprint-17/task-3.md)). The proof did not exercise a provider, MCP normal client, browser authority, or host/ACL mutation. This implementation note does not revise D108 or establish task acceptance; the independent review and exact-snapshot checkpoint remain pending.

## Owner-approved workspace context-pack grant — S17-T4 / D110 (2026-11-17)

The owner approved “Duyệt grant riêng theo đề xuất” for a distinct, versioned `workspace.context.pack` authority. This additive contract authorizes the selected T4 context-pack disclosure only; it does not change any requirement or interpretation of `workspace.file.read`, D108 folder grants, old grants, T5 integration or provider authority. See the normative [T4 contract](META-BRAIN.md#57-approved-workspace-context-pack-grant--s17-t4--d110), [D110](../.agents/memory/decisions.md) and [Sprint 17](sprint-plans/sprint-17.md#atomic-tasks).

- **R03/R06/R11/R31:** The owner previews and explicitly approves exact sorted workspace file IDs, retained revisions and full digests, selected passage byte anchors and a separate `workspace.context.pack` versioned subject/operation. The preview binds policy generation, unlock epoch, tree revision and the complete output set. No folder membership, existing folder grant, or `workspace.file.read` grant implies context-pack access.
- **R07/R08/R09/R23/R25:** Only complete, budget-fitting verbatim entries are returned. Each retains citation, attribution, file/revision/full digest, exact byte anchor and all applicable relation provenance; an entry that cannot fit is omitted whole with an explicit bounded gap and honest partial/unavailable status. No citation/metadata truncation, generated summary, local inference, provider call, network egress or cross-zone derived inclusion is allowed in T4.
- **R14/R15/R23/R25:** Only explicitly selected accepted/current relation records with both independently authorized exact file/passage endpoints and pins may be included. Preview and output preserve relation ID/revision/state, both endpoint revisions and digests, stored attribution/provenance, rationale and history; preserve `contradicts`, exclude proposed/rejected/retracted/needs-review/stale evidence, and infer no relation from membership. Out-of-scope endpoint identities are withheld. Keep traversal bounded to two hops and request limits; emit one deterministic source/revision/path per origin, without link-count scoring or truth inflation.
- **R03/R05/R36/R31:** The five-minute preview is generation-bound and redemption atomically single-use. Persist the frozen scope in encrypted policy; expiry, grant/session revoke, lock and restart invalidate it. Any policy/unlock/tree/content/relation/endpoint-pin drift requires a fresh owner preview and authorization, never an expansion of the existing grant. Injection does not widen scope.
- **Explicit non-rights:** No cognitive annotations, owner-private classification, registered-source body, derived summary/synthesis, cross-zone derived content, model/provider/local inference, egress consent or derived-artifact authority. Future cloud-agent egress requires separate explicit approval naming provider, model, context and cost. D108 and all old grants remain unchanged.

This approval is a normative T4 contract, not runtime evidence, task acceptance, an independent review, a checkpoint, a T5 normal-client launch or external-action authorization.

## Owner-approved workspace agent recall contract — S17-T5 / D111 (2026-11-17)

The owner approved a distinct, versioned `workspace.agent.recall` grant with independently selected cognitive-annotation, hybrid-search and relational-recall operations. The normative [contract](META-BRAIN.md#58-approved-workspace-agent-recall-grant--s17-t5--d111) and [D111](../.agents/memory/decisions.md) are additive: D108, D110, all older grants, canonical source/item rights and proposal semantics remain unchanged.

- **R03/R05/R06/R11/R31/R36:** Owner preview pins the exact operation subset and selected annotation/file/passage/relation revisions, digests and endpoints; the encrypted scope is generation-bound, five-minute, single-use on redemption and rechecked on every call. Drift requires fresh approval; expiry, grant/session revoke, lock and restart deny. A folder request is an opaque non-authoritative receipt; only the owner's independent D108 preview/approval creates a file-read grant.
- **R14/R15/R23/R25:** Cognitive output preserves selected annotation provenance, review/origin and exact anchor/currentness; hybrid output preserves D104 literal versus semantic classes, exact citations and no-truth semantics; relational output preserves selected accepted/current relation provenance and contradiction without membership-derived edges, out-of-scope identifiers or unbounded traversal.
- **R07/R08/R09/R16:** No provider/network egress or cross-zone/derived output is included. Semantic hybrid queries require a separate explicit local-inference consent naming the exact repo-local model directory, query scope and resource/time budget; no fallback may misrepresent lexical results as semantic hybrid recall. `source.read`, `source.snapshot.read` and `proposal.create` remain separate existing authorities; workspace recall does not open sources or create derived proposals.
- **Bounded disclosure:** Queries are at most 256 characters, hybrid pages at most 100 entries with snippets at most 240 characters, relation traversal at most two hops/100 selected relations, and complete-entry response budget at most 64 KiB. Required anchors, citations and provenance are never truncated.

This approval defines authorization, not implementation, model/provider execution, normal-client proof, task acceptance, evidence review, checkpoint, release or external action.

## Owner-approved migration/cutover contract — S18-T1 / D112 (2026-11-18)

The owner approved the guarded migration/cutover contract and the S18 acceptance envelope on 2026-11-18. The normative [contract](META-BRAIN.md#136-approved-migrationcutover-contract--s18-t1--d112) and the [S18 acceptance envelope](META-BRAIN.md#s18-acceptance-envelope--approved-2026-11-18-d112) are the binding detail; [D112](../.agents/memory/decisions.md) records alternatives and consequences; the proposal, inventory and corpus are in [S18-T1](../artifacts/sprint-18/task-1.md) §2–§8. Approval is a contract decision, not implementation, runtime evidence, task acceptance, review PASS, checkpoint or release authorization.

This record is a clarification plus the S18-scoped quantitative envelope under [R38](#requirement-registry), not a baseline security change: no requirement is removed or renumbered, no requirement outcome is relaxed, and no historical PASS, FAIL, measurement or D086 decision is rewritten. The S18-T1 change-control steps are: affected IDs/baseline/evidence in [S18-T1 §1–§3](../artifacts/sprint-18/task-1.md), alternatives in [D112](../.agents/memory/decisions.md), compatibility/migration/data/security/downstream impact in [S18-T1 §9](../artifacts/sprint-18/task-1.md) and the contract above, owner approval on 2026-11-18, this appended decision, and the gate reopening stated below.

- **R08/R10/R11 — classification is never moved as a side effect.** A zone-bearing or unknown/unmappable classification transfers only when the destination enforces every applicable protection it carried; under the current workspace authorization every zoned record fails that guard, stays legacy and is reported ineligible. Annotation plus encrypted map are provenance, not enforcement. Declassification remains an independent explicit owner decision, recorded before migration and never offered, bundled or pre-checked in a migration preview or confirmation. No zone/classification is fabricated and `unknown` stays explicit.
- **R16/R18 — original bytes and preservation stay truthful.** Only records with exact original bytes — preserved source snapshots inside the existing 1 MiB per-act cap plus explicit file imports — are eligible, with byte/length/digest equality required. Memory items, `arc-` envelope history and every structured-body-only family stay in the legacy surface; no original is fabricated and no such record is written as an unlabeled content revision. A snapshot above 1 MiB stays legacy and is reported, never truncated or chunked. Migration never reads, rewrites, moves or auto-imports an external source path.
- **R29/R34 — restore, purge and export span both representations.** The only supported rollback is restoring the verified pre-migration encrypted backup with its key to a separate directory and switching the data root; there is no per-record undo and no 1.x binary downgrade, and post-backup edits/exports are excluded and disclosed. Purge previews and export cover the workspace node/revisions, legacy record, mapping, annotations, relations, indexes, retained revisions, snapshots and backups, state what is outside Meta Brain's control, and leave no hidden second truth; the purge ledger is re-applied on restore so purged identities never resurrect.
- **R30/R31 — CLI remains the owner surface.** Preview, explicit confirmation, execution, report and rollback are owner CLI operations sharing one policy; the browser gets no migration, approval or owner authority, and migration mints no grant, token, session or catalog entry. A live token or session is never transferred; catalog entries, grants and collections stay pinned to legacy ids/zones/revisions until expiry or owner withdrawal/revocation, and workspace sharing remains a separate S17 approval.
- **R33/R36/R39 — encrypted, fail-closed and resumable.** Transfer happens inside the unlocked process between encrypted stores with no plaintext temp/draft/cache; wrong key, tamper, truncation, digest/length mismatch, schema mismatch, unsupported version or an over-cap record fails closed before mutation. The vault envelope/manifest format and key hierarchy are unchanged. An interrupted run resumes without loss or duplicate node, and a migrated record is either complete or unchanged.
- **R35 — no downgrade promise.** Restore of a compatible pre-migration backup is the downgrade path; a 1.0/1.1 binary is not promised to read a post-1.8 vault. S18-T6 verifies only the changed 1.8 upgrade/install/refusal path and claims no WPF replay.
- **R37 — real files need separate consent.** The owner-approved R37 walkthrough may use real owner files only under separate per-run consent; that consent is **not granted** by this approval and no real-file access occurs under it. Evidence from any consented run stores only redacted counts, digests and non-content observations. A synthetic-only R37 substitution would need an explicit owner scope change.
- **R38 — S18 thresholds frozen before tuning.** S18-only absolute gates: handler p95 ≤250 ms at 1,000 records and ≤500 ms at 10,000 on the A18-2 protocol. At 100,000 on that protocol, handler p95, rebuild wall time, unlocked idle WS and locked idle WS must each stay ≤110% of the A18-2 baseline (1,668.026 ms; 1,396,956.547 ms; 207,084,032 B; 204,846,357 B); p50, max, idle CPU, private/native/managed/cache bytes, disk/staging peak and throughput are report-only. The 256 MiB idle-RAM ceiling and the 1 MiB per-act cap are unchanged. D086's old 250/500/1,000 ms and 20-minute rebuild remain historical envelope records, not S8-T5 gates, and the 100k absolute targets are not re-adopted for S18. The 1k/10k handler percentiles were never measured before this approval; that gap and the in-process-versus-service gap must be reported, not filled with estimates. Corpus C1/C2/C3, held-out queries and the no-wholesale-Sprint-9 rule are frozen in the contract; no tuning on held-out data and no post-measurement query addition.
- **R40 — honest evidence.** The migration report lists exact eligible and ineligible records with reasons; no silent drop, fabricated original, unlimited claim or unverified mapping is accepted. A metric that was not measured is a gap, not a pass, and T2 must demonstrate the destination authorization before any transfer.

**Gates reopened.** S18-T5 and the final acceptance must measure and report this envelope and the migration contract; S18-T2/T3 must demonstrate authorization, resume, restore and fail-closed behavior. No gate PASS is created here: S18-T1 remains `[~]` until worker evidence review PASS and the separate exact-snapshot documentation checkpoint, and T2–T6 stay unauthorized.

**Explicit non-rights.** This approval does not authorize T2–T6 implementation, migration execution, provider/network access, R37 real-file access, R37 scope changes, release/push/tag or external actions. G2 removals remain a separate inventory decision under a separate owner approval; no legacy path is removed in 1.x, and the S12 installed-transition evidence is retained as history.

## Owner-approved 1.8.0 resource exception — S18-T5 / D113 (2026-11-20)

For 1.8.0 only, [D113](../.agents/memory/decisions.md#2026-11-20-d113--treat-worker-host-performance-comparisons-as-report-only-for-180) supersedes the blocking status of D112's R38 1k/10k handler p95 targets and its four 100k A18-2-relative targets (handler p95, rebuild wall time, locked and unlocked idle working set). Report exact available observations, methodology and comparability gaps under R40. A missing or incomparable value stays a gap in the measurement record; the owner accepts it for this release, not as a passing measurement. See the [normative exception](META-BRAIN.md#180-only-resource-exception--d113).

The 256 MiB service-idle RAM ceiling, 1 MiB per-act content cap and all non-comparison requirements, including security, data handling, migration/rollback, held-out relevance and R37, remain unchanged. This decision neither passes T5/T6 nor authorizes a provider, publication or G2.

## Owner-approved G2 removal scope — D114 (2026-11-20)

[D114](../.agents/memory/decisions.md#2026-11-20-d114--limit-g2-removals-to-obsolete-wpf-packaging) authorizes the [G2-T1](ROADMAP-2.0.md#8-g2--final-release-200-không-phải-sprint-bổ-sung) workspace default, supported caller cutover, packaged `metabrain` entrypoint and removal of redundant active WPF packaging callers only, if present. It does not authorize publication.

- **R30/R34:** Retain legacy memory/source/relation workflows and structured export for records that stay legacy. A workspace default is not a migration or a declassification.
- **R35/R36:** Retain plaintext-settings `owner migrate`, fail-closed protocol mismatch, installed WPF detection/refusal/remediation, backup/restore and data-preserving upgrade. Leave existing components and history untouched.
- **R33/R40:** Preserve the existing data/authority boundary, including pinned grants, and prove no two writers or loss of a retained workflow. This approval is not an evidence, checkpoint, or candidate PASS.

## Owner-approved CLI user-PATH opt-in — D115 (2026-11-20)

Under R35, [D115](../.agents/memory/decisions.md#2026-11-20-d115--register-metabrain-through-an-explicit-user-path-opt-in) permits the owner-run installer to add the installed CLI directory to the current user's PATH only with explicit consent. Preserve entries already present; upgrade/uninstall must not remove entries the install did not add. No system PATH, profile or WindowsApps writes. Agents may not execute persistent PATH or registry changes. G2-T2 needs owner-run proof from a fresh PowerShell outside the install directory after opted-in install/upgrade, as well as uninstall ownership proof.

For G2-T2 candidate `bc8ebc909aa8` only, the owner approved [D116](../.agents/memory/decisions.md#2026-11-22-d116--accept-combined-owner-path-evidence-for-g2-t2): combine the owner-run clean-install PATH ownership lifecycle and pre-existing-entry preservation with owner-confirmed fresh PowerShell discovery after the opted-in upgrade from 1.8.0. A distinct fresh-shell observation after clean install is not required for this candidate and was not observed. D115 product and host-safety requirements are unchanged.

## Owner-approved Codex MCP evidence adjustment — D117 (2026-11-22)

For G2-T2 candidate `bc8ebc909aa8` only, [D117](../.agents/memory/decisions.md#2026-11-22-d117--accept-bounded-codex-mcp-evidence-for-g2-t2) accepts an actual OMP MCP tool call, Codex MCP server initialization, and direct stdio verification of the packaged server in place of successful Codex MCP tool execution. Record that neither authorized Codex run called a tool. This adjusts release evidence only, not MCP scope, authority, compatibility guarantees, or remaining acceptance and review gates.
