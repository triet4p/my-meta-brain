# Meta Brain — Yêu cầu cứng và hợp đồng từng chặng

## Baseline và phạm vi

Baseline 2026-09-28 được owner thay security scope bằng CR01/D016 ngày 2026-10-01; đọc cùng [đặc tả](META-BRAIN.md), [decision log](../.agents/memory/decisions.md) và [PLAN](PLAN.md). MUST/bắt buộc là điều kiện nghiệm thu. Sprint 1/M1 replacement complete; Sprint 2/M2 complete (owner-confirmed committed); Sprint 3 In progress (S3-P0 complete; S3-T1 source enrollment/ingestion coordinator accepted with evidence/checkpoint; S3-T2 Codex session JSONL read-only adapter delivered, worker evidence pending review); Sprint 4–8 Not started. Documentation approval không chứng minh implementation.

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
| R30 | UI owner cho library/capture/review/relations/source history | Thao tác thật trên UI, keyboard cơ bản, Việt–Anh; trả lời được vì sao nhớ và ai thấy; không UI giả nối mock backend | 5 |
| R31 | UI unlock/catalog/access requests và scoped sessions | User unlock/lock, preview/publication catalog, review/thu hẹp/approve/reject requests, handoff token, expiry/revoke; dùng core policy, không sửa config bằng tay hoặc protected-runtime launcher | 5 |
| R32 | Ba thành phần tách ranh giới, một domain/policy implementation | Core không lệ thuộc UI/MCP SDK; adapters không ghi vault; UI/MCP dùng cùng policy và persistence; không cần microservices | 2 |
| R33 | Encrypted file-first canonical, private index rebuildable và durable writes | Plaintext readable chỉ sau owner unlock/explicit export; ciphertext revision conflict/crash/rebuild/migrations đúng; index/WAL/journal/temp không dual truth hoặc plaintext leak | 2 |
| R34 | Encrypted backup/restore, key recovery và explicit scoped export | Restore cần key, tái hiện lineage/lifecycle, không hồi sinh live sessions/used tokens/purged data; lost key không fake recovery; plaintext export có owner disclosure, không gồm private citations ngoài scope. CR02: preserved bytes không tái tạo được sau khi nguồn đổi → phải vào backup/restore/export scope | 8 |
| R35 | Install/start/restart/upgrade/uninstall đủ dùng hằng ngày | App startup locked, owner unlock và hai client thường dùng được; không sandbox/account/patch prerequisites; upgrade không reset, uninstall không xóa vault/source/key material khi chưa đồng ý | 8 |
| R36 | Private metadata/index/audit/cache/credentials và artifacts được bảo vệ | Encrypt sensitive persisted product data; chỉ approved catalog/minimal format envelope công khai; không commit vault/transcript/key/token; không log raw secrets/body; same-owner OS hostile access ngoài cam kết. CR02: preserved bytes encrypted, diagnostic chỉ code+offset, không body | 8 |
| R37 | Chất lượng định tính trên công việc thật, không thay bằng benchmark | Owner walk-through recall lỗi cũ/quyết định/research/link/synthesis/intentions; ghi nhận sai/cũ/thiếu; không finding nghiêm trọng chưa giải quyết | 8 |
| R38 | Operational envelope đo được, khóa trước tuning | Sprint 1 ghi số đo/giới hạn dataset, p95, indexing, disk/idle resources và model budget; Sprint 8 đo lại không đổi mục tiêu để che fail | 8 |
| R39 | Recovery và migration không được né bằng reset dữ liệu | Interrupted writes/jobs, service restart, index corruption, schema upgrade có kịch bản recovery; dữ liệu báo saved không mất. CR02: journal+checkpoint atomic với preserved span; truncation/replacement vẫn quarantine | 8 |
| R40 | Yêu cầu cứng, kế hoạch và evidence trung thực | Thay baseline qua change control, cập nhật traceability; task chỉ [x] sau gate, không claim runtime/test chưa chạy. CR02: approval thật + sync docs; corpus expectation không thay product PASS | 8 |

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
- **Chưa shipped.** Đây là contract approval, không phải product behavior: S11-T2–T5 chưa triển khai và
  chưa có runtime/UI/CLI/MCP evidence. Corpus expectation không phải PASS sản phẩm. Approval không bao
  gồm push/tag/publication, provider/network access, host/ACL/registry mutation hoặc migration dữ liệu 1.0;
  các gate full-route và checkpoint vẫn giữ nguyên.

### Owner-approved S11-T4 folder refresh policy — 2026-10-31

The owner approved explicit `folder-refresh-optin` / `folder-refresh-optout` consent, default off. The original folder preview/confirmation is not refresh consent. While opted in, the folder policy covers current and future matching regular files under the selected root and stored include/exclude rules; new files are admitted and read only after reparse, root, and final-path boundaries pass. A source-level pause, revoke, or explicit refresh opt-out vetoes the folder policy without being overwritten. Folder opt-out and vault lock stop refresh at the source boundary. Deleted members remain canonical/history and are reported missing; refresh never automatically revokes or purges them. Manual and background refresh share the same coordinator.

This records the owner-approved T4 contract, not shipped behavior or runtime evidence. Existing CR02/D088 requirements, encrypted preservation and retention, journal/checkpoint atomicity and compatibility, attribution, and scoped-rights constraints remain unchanged; S11-T4 remains subject to its full-route evidence and separate checkpoint gates.

