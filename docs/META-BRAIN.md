# Meta Brain — Đặc tả sản phẩm và kiến trúc

- Trạng thái hiện hành: [Sprint 8 complete](sprint-plans/sprint-8.md), toàn bộ milestone trong [PLAN](PLAN.md) đã hoàn tất với assigned gates. Owner chấp nhận T7 và các giới hạn đã công bố; [T7 evidence PASS](../artifacts/sprint-8/reviews/S8-T7-R1.md), [checkpoint](../artifacts/sprint-8/checkpoints/S8-T7-C1.md) `9ce136bc4c8394b7b8af1230c2f00a448b3a6cc8`, [final differential PASS](../artifacts/sprint-8/reviews/S8-DEEP-A1.md), không actionable findings. CR01 threat model và giới hạn same-owner/copies giữ nguyên; không tuyên bố mọi provider/model attack đã được kiểm thử. Không push.
- Chủ sở hữu: người dùng duy nhất của sản phẩm; các AI agent là client được ủy quyền.
- Lộ trình: [PLAN](PLAN.md). Hợp đồng nghiệm thu: [REQUIREMENTS](REQUIREMENTS.md). Quy tắc thực hiện: [AGENTS](../AGENTS.md).

## 1. Mục tiêu và triết lý

Meta Brain là bộ nhớ thứ hai, local-first, dùng hằng ngày để giữ bằng chứng, tìm lại bối cảnh, kết nối kinh nghiệm và tổng hợp tri thức khi cần. Nguồn đầu vào gồm session Codex/OMP, ghi chú của user, research/paper, quan sát trend và dự định. UI phục vụ owner; MCP/tool interface phục vụ agent với quyền giới hạn.

Nguyên tắc xuyên suốt:

- Giữ hộ, tìm hộ, kết nối theo yêu cầu; không tự chọn agenda, dẫn dắt suy nghĩ hoặc quyết định điều user phải tin.
- Tự động hóa việc cơ học; thay đổi ý nghĩa, cấp quyền và chia sẻ phải có kiểm soát.
- Lưu bằng chứng rẻ; cấu trúc hóa và tổng hợp có chọn lọc. Không cần LLM chạy extraction cho mọi token hoặc thường trực.
- Không tối ưu vì leaderboard. Chất lượng được đánh giá trên công việc thật; bảo mật, toàn vẹn và khôi phục phải có kiểm tra xác định.
- Không làm MVP rồi thay nền móng. Mỗi chặng là phần sản phẩm thật, có hợp đồng ổn định và tính tới các chặng sau. Sản phẩm chỉ hoàn thành khi toàn bộ phạm vi bắt buộc vượt gate.
- Không over-engineer: không graph database, microservice mesh, ontology tổng quát, cây khóa theo mọi zone hoặc sandbox agent nếu chưa thuộc nhu cầu được duyệt. Trừu tượng hóa tại ranh giới thực: nguồn, encrypted storage, model provider và connection.
- Mỗi chức năng phải bảo toàn quyền sở hữu, nguồn gốc, thời gian và khả năng sửa sai. Không dùng mock, no-op hoặc fallback bịa dữ liệu để qua nghiệm thu.

Không thuộc baseline: SaaS đa tổ chức, đồng bộ nhiều máy, crawler theo dõi Internet tự trị, huấn luyện model, benchmark leaderboard, marketplace plugin và agent tự lập kế hoạch cuộc sống. Các mục này không được dùng để trì hoãn chức năng local bắt buộc.

## 2. Khái niệm: những trục không được trộn

| Khái niệm | Trả lời câu hỏi | Quy tắc |
| --- | --- | --- |
| Zone | Ai được đọc, đề xuất, xuất bản, chia sẻ, lưu giữ? | Ranh giới bảo mật và policy; không chỉ là folder/tag |
| Source | Nội dung đến từ đâu? | Giữ định danh, locator, phiên bản, tác giả/vai trò và trạng thái khả dụng |
| Memory item | Điều gì đáng giữ và có thể xử lý độc lập? | Có ID ổn định, revision và provenance |
| Collection/topic | User muốn tổ chức nội dung ra sao? | Nhiều-nhiều với item; không sinh quyền |
| View/brief | Công việc này cần thấy gì? | Bản chiếu được lọc quyền, có thể dựng lại |
| Abstraction level | Nội dung cụ thể hay khái quát đến đâu? | Không đại diện cho độ đúng hay quyền truy cập |
| Relation | Hai nội dung liên quan như thế nào, do ai xác định? | Dữ liệu tường minh, có provenance và vòng đời |

### 2.1. Đơn vị tri thức

Memory item là một mẩu nhớ có thể được trích dẫn, sửa, phản biện, thay thế hoặc cấp quyền độc lập. Không ép mọi câu thành fact; không gom mãi cả session thành một item. Giữ cùng nhau điều kiện, hành động và kết quả nếu tách chúng làm mất ý nghĩa.

Ví dụ: “Trong môi trường X, A thất bại vì B; C đã hoạt động ở lần thử Y; chưa kiểm chứng ở phiên bản mới hơn” là một lesson hoàn chỉnh.

Envelope chung cần có:

- ID ổn định, schema version, revision; ID không dựa trên tiêu đề/path có thể đổi.
- Zone sở hữu và nội dung có thể đọc bởi người.
- Kind: claim, observation, question, hypothesis, decision, lesson/procedure, synthesis, preference hoặc intention.
- Provenance: nguồn, đoạn tham chiếu, vai trò người nói, người/job tạo, phương thức tạo, bằng chứng xác nhận nếu có.
- Thời gian, trạng thái theo kind và phạm vi áp dụng: project, môi trường, phiên bản/commit nếu biết.
- Collection membership và relation được quản lý riêng khỏi quyền.

UI cho phép nhập văn bản trước; không bắt user điền toàn bộ schema. Core chịu trách nhiệm xác thực và lưu metadata bắt buộc.

### 2.2. Zone

Tạo zone khi ranh giới chia sẻ hoặc retention khác nhau, không phải mỗi khi có topic mới. Ví dụ: personal-private, client-a-private, project-b-private, shared-engineering. “Shared” không có nghĩa public Internet.

Một project có thể có nội dung ở nhiều zone. Một collection có thể tham chiếu nhiều zone nhưng mỗi principal chỉ thấy phần được phép. Mỗi item có một zone sở hữu; chia sẻ sang zone khác là thao tác xuất bản bản dẫn xuất rõ ràng, không lén thêm ACL theo collection.

Cây zone trong UI không mặc nhiên tạo kế thừa quyền. Owner chọn vùng/collection để preview, nhưng grant đọc được chốt thành tập resource IDs và revisions cụ thể tại lúc duyệt; thêm descendant, membership hoặc revision mới không tự mở rộng quyền. Không cấp quyền dựa vào cwd, tên project, tên agent hoặc MCP roots.

### 2.3. Tầng trừu tượng

| Tầng | Vai trò | Ví dụ |
| --- | --- | --- |
| L0 | Sources | Session, ghi chú gốc, đoạn tài liệu/paper |
| L1 | Concrete memories | Một quyết định, một lần thử, câu hỏi cụ thể |
| L2 | Lessons/patterns | Bài học có điều kiện từ một hoặc nhiều trải nghiệm |
| L3+ | Syntheses/frameworks | Tổng hợp rộng hơn từ nhiều chủ đề/dự án |

Tầng cao không mặc nhiên đúng hơn. L0 có thể chứa phỏng đoán sai của AI. Không bắt mọi item đi qua đủ tầng; user có thể viết nguyên tắc tổng quát trực tiếp mà không có bằng chứng thấp tầng. Không bịa provenance để hoàn thiện cây.

Một item có nhiều nguồn và nhiều item dẫn xuất. Dùng mạng quan hệ nhẹ; riêng derived_from không được có chu trình. Tổng hợp nhiều tầng phải truy được về bằng chứng khả dụng, không chỉ lặp summary của summary và làm mất ngoại lệ.

## 3. Nguồn tác giả và tính chất nhận thức

Không dùng một trường confidence để thay thế xuất xứ và kiểm chứng. Phải tách:

| Chiều | Ví dụ |
| --- | --- |
| Tác giả/phát ngôn gốc | User, assistant, tool observation, tác giả ngoài |
| Người thực hiện thao tác | Owner, agent session, structuring job |
| Kiểu nội dung | Question, hypothesis, decision, synthesis… |
| Cách tạo | Viết trực tiếp, trích nguyên văn, diễn giải, tổng hợp |
| Mức căn cứ | Chưa kiểm chứng, nguồn báo cáo, user xác nhận, quan sát thực nghiệm |
| Trạng thái xuất bản | Proposal, published, rejected hoặc withdrawn |

“User thắc mắc X” không trở thành “user tin X”. “User giữ lại summary AI” không trở thành “user tự viết hoặc xác minh summary”. Chỉnh sửa của owner giữ lịch sử tác giả. Lời assistant nói đã sửa không tương đương kết quả chạy xác nhận; kết quả chạy cũng không tự chứng minh code đã được deploy.

Câu hỏi có vòng đời open/resolved/reopened; một câu trả lời của AI chỉ addresses câu hỏi, không tự resolved. Preference và quyết định của user không được suy ra rồi xuất bản như sự thật chỉ từ hành vi lặp lại.

## 4. Liên kết và yêu cầu explicit qua chat

Quan hệ khởi đầu: related_to, derived_from, supports, contradicts, supersedes, applies_to, addresses. Tương đồng embedding chỉ là tín hiệu gợi ý related_to, không phải bằng chứng supports hoặc quan hệ nhân quả.

Mỗi relation có ID, hai endpoint, loại, người/job tạo, lý do/đoạn căn cứ, thời gian và trạng thái proposed/accepted/rejected. Trạng thái chấp nhận link không chứng minh mệnh đề của link là đúng.

- Liên kết tổ chức như related_to có thể theo ID ổn định.
- Liên kết bằng chứng như supports/derived_from phải giữ revision/đoạn đã sử dụng.
- Khi nguồn đổi đáng kể, đánh dấu phần dẫn xuất cần xem lại; không tự sửa kết luận của user.
- Nguồn mất: ghi rõ unavailable. Xóa hoặc thay thế item không được để dangling relation không giải thích được.
- Không bắt related_to thành DAG; chỉ cấm vòng lặp ở quan hệ có yêu cầu đó, nhất là derived_from và chuỗi supersedes.

User có thể nói qua agent: “Nối ý này với research X”, “Bản mới thay thế nhận định cũ”, “Hai ý liên quan nhưng không dùng làm bằng chứng”. Agent resolve tiêu đề/ID chỉ trong đúng revision memory/source đã được đọc; nếu có nhiều đích khớp thì phải hỏi user chọn và không mutate. Với yêu cầu rõ ràng, core thực hiện ngay khi grant có đủ quyền, không hỏi xác nhận lại như một bước authorization thứ hai.

Relation lưu riêng executor/method do server đóng dấu và request evidence do caller báo cáo trong canonical record mã hóa. Không tin `origin=user` tự khai từ agent làm owner confirmation; agent relation API không có host-attested confirmation, không được tự nhận xác nhận user hoặc declassification, và caller text không nâng quyền.

Mọi relation action cần quyền đọc đúng revision của cả hai endpoint (`memory.read` hoặc `source.read` theo loại): `relation.list` cho xem quan hệ, `link.create` tạo, `link.supersede` tạo loại supersedes, `link.remove` unlink và `link.restore` undo. Agent link luôn proposed; undo trở lại proposed, không accepted. Không để title, snippet, relation ngoài endpoint scope hoặc lỗi lookup tiết lộ endpoint; relation/link, title, membership, zone và backlink không cấp thêm quyền đọc hay declassification.

## 5. Bảo mật: encrypted personal vault và chia sẻ có user duyệt

Owner-approved CR01/D016 trong [decision log](../.agents/memory/decisions.md) thay yêu cầu sandbox OS trước đây. Meta Brain bảo vệ tri thức đã lưu và kiểm soát đường chia sẻ qua ứng dụng; không vận hành sandbox cho Codex/OMP.

### 5.1. Chủ thể, key và quyền

Owner sở hữu mọi zone và có toàn quyền quản trị, nhưng đọc/ghi nội dung encrypted vault cần key hợp lệ và vault đang mở khóa. Quyền sở hữu không tự vượt qua mã hóa. User tự bảo quản key/recovery; ứng dụng chỉ dùng key trong bộ nhớ khi mở khóa, không lưu plaintext key cạnh vault, trong repo, agent config/env/argv, model context hoặc logs.

Key giải mã và token truy cập là hai loại khác nhau. Agent không nhận key vault/zone; token không có chức năng giải mã ciphertext. Ứng dụng kiểm tra grant rồi mới giải mã nội dung được phép và trả plaintext đó. Không phát key của một gói tải xuống rồi gọi nó là key “dùng một lần” có thể thu hồi.

Agent mặc định chỉ đọc catalog owner đã công bố; memory/raw sources không được đọc nếu chưa có phiên với grant hợp lệ. Read-only là mặc định. Proposal, revise/link và các operation khác cần owner cấp riêng; publish, declassification, quản trị key/grants và purge không thuộc quyền agent mặc định. Đọc lesson không tự cấp quyền đọc session gốc.

### 5.2. Threat model và lock lifecycle

Bảo vệ bắt buộc: authenticated ciphertext của canonical/private metadata, index/embeddings, cache/journal/temp, snapshots và backup không tiết lộ plaintext khi người đọc file không có key. Encrypted records bind đúng vault/resource identity/schema/revision; tampering/truncation hoặc tráo ciphertext giữa resources fail closed, không phục vụ nội dung ngoại scope dưới allowed ID. Chọn standard crypto library/format/KDF/versioning theo evidence S1, không tự crypto. Passphrase dùng KDF phù hợp như Argon2id; mất key không recovery thì dữ liệu không đọc được.

Không thuộc cam kết: chống agent/malware chạy cùng quyền OS owner đọc process memory/key/plaintext, điều khiển owner UI, lấy bearer token của peer, đọc session nguồn vốn nằm ngoài vault, owner/admin cố tình vượt ứng dụng hoặc kernel compromise. Không claim OS-bound agent identity. Dữ liệu/catalog đã chia sẻ, model context và bản sao bên ngoài không thu hồi được. Metadata được công bố cũng có thể tiết lộ điều nhạy cảm; đó là disclosure cần user duyệt.

Codex/OMP chạy bình thường, không bắt buộc AppContainer, account riêng, patched binary hoặc launcher protected. Windows vẫn là nền tảng đầu tiên. Tách owner workflows và agent API ở ứng dụng; một token agent không gọi được owner operation. Same-owner OS token/localhost/cwd/agent_name không phải chứng cứ user đã duyệt yêu cầu do agent gửi.

Vault khởi động locked; wrong/missing key hoặc tampering fail closed. Lock invalidates pending tokens/live sessions, dừng private reads/ingestion/jobs, chặn decrypt/response/send/commit mới tại effective lock boundary; bytes/context đã trả hoặc gửi provider trước đó không thu hồi được. Không persist plaintext để chạy khi khóa. Restart không auto unlock hoặc hồi sinh live/used tokens. Approved catalog có thể xem khi khóa; private access requests không nhận/persist khi chưa thể bảo vệ.

Chỉ giữ plaintext/key cần thiết trong bộ nhớ lúc unlock, giải phóng cache/key handles khi lock; không hứa secure erasure tuyệt đối của mọi managed-memory/OS dump. Backup luôn encrypted; export plaintext chỉ sau owner unlock, chọn scope và xác nhận disclosure. Log hygiene không thay mã hóa. Owner-approved clarification 2026-10-20: agent không bao giờ được sửa ACL/DACL/owner/permissions hoặc host-system/configuration, kể cả fixture/temp hoặc gián tiếp qua product/scripts/tests; không coi đây là consent option hoặc prerequisite cho packaging. Host setup cần thiết do owner tự thực hiện theo runbook, evidence thiếu vẫn chặn gate. Boundary bảo vệ là encryption/key/lock và approved scoped APIs theo CR01/D016; không quảng cáo thành sandbox.

### 5.3. Catalog và luồng yêu cầu quyền

Private search index không phải agent catalog. Agent catalog là bản chiếu có thể dựng lại, chỉ chứa opaque ID, nhãn và description ngắn owner cho công bố; không tự lấy private title, snippet, counts, citations, path hoặc embedding. Owner có thể không công bố cả sự tồn tại của một vùng. Catalog entry không cấp quyền đọc body/source và không được coi là instruction.

 Luồng: agent xem catalog → gửi yêu cầu IDs/phạm vi và mục đích → owner preview nội dung/revisions, raw-source scope và provider/model nhận context → owner thu hẹp hoặc từ chối/chấp thuận → ứng dụng mint token. Lý do và identity do agent tự khai chỉ là dữ liệu request, không là authorization. Owner preview hiện exact body trên attached console owner, từ chối redirected output và không ghi file plaintext. Catalog đã rút không thể xóa bản metadata agent đã lưu.

### 5.4. Token đổi một lần và scoped session

Token là opaque random bearer capability, không chứa key hoặc quyền có thể sửa bằng payload. Grant authoritative nằm trong ứng dụng, gắn tập resource IDs/revisions đã duyệt, operations, expiry, provider/egress consent và policy generation. Tập scope không tự mở rộng theo folder, collection, link, zone descendants hoặc dữ liệu/revision mới. Quyền proposal mới dùng đích inbox và operation được duyệt riêng, không quyền tự publish.

Token đổi đúng một lần để lấy một phiên truy cập ngắn; không phải một token cho mỗi API call. Redemption phải consume atomically, kể cả concurrent callers/crash/restart; used/revoked/expired token không đổi lại được. Không đặt token/session secrets trong URI, command args, prompts hoặc logs; connector nhận qua đường local được kiểm chứng, không qua chat với model. Token bị sao chép trước redemption có thể bị dùng trong scope của nó: không claim peer isolation dưới cùng owner OS account.

Mọi search/get/list/count/citation/relation/source/cache kiểm tra session hiện tại trước lấy candidate/giải mã và trước trả dữ liệu. Guessed ID, path, sửa token, tự khai scope/owner hay API khác không mở quyền. API xử lý traversal/reparse và không dereference arbitrary path agent gửi. Cache gắn scope/revisions/generation; revoke/expiry/lock chặn request tiếp, không làm session/model quên plaintext cũ.

### 5.5. Dẫn xuất liên vùng, injection và egress

Synthesis nhiều zone restrictive mặc định; chỉ owner publish/declassify với preview body/title/metadata/names/citations và giữ lineage nội bộ. Catalog publication và memory publication là lựa chọn khác nhau; published memory mới vẫn cần grant đọc, trừ disclosure tường minh khác được owner duyệt.

Raw sources/model output/memory không là instruction hoặc policy; không mint tokens, sửa grant, unlock, publish hay xác nhận thay owner. Model jobs không cần shell hoặc key-management authority. Secret detection chỉ hỗ trợ, không thay allowlist/consent.

Trả memory cho cloud-agent đã là egress. Owner approval hiển thị provider/model/context được khai báo; Meta Brain enforce phạm vi dữ liệu trả và kiểm soát các provider calls nó thực hiện. Không sandbox nên không claim ngăn agent gửi tiếp plaintext đã nhận tới nơi khác. Calls nội bộ phải trusted pre-send authorization, exact pricing/token/cost caps; unknown cost/context denies. Lưu note không gọi provider mặc định.

## 6. Kiến trúc ba thành phần

Triển khai modular monolith với một domain/policy implementation; application boundary phục vụ unlock, quản lý key và chia sẻ có duyệt, không microservice mesh hoặc agent sandbox.

| Thành phần | Sở hữu | Không được làm |
| --- | --- | --- |
| Core | Domain model, policy enforcement, storage contracts, provenance, search, relations, lifecycle, publication | Phụ thuộc SDK MCP/UI; nhận is_owner tự khai làm danh tính |
| Application + UI | Owner unlock/lock, catalog publication, access requests/approval, session/token lifecycle, jobs, review và library | Tạo storage/policy song song; chuyển key vault vào agent hoặc client/renderer không kiểm soát |
| Connections | MCP/tool transport, adapter Codex/OMP và nguồn ngoài | Bỏ qua core, tự cấp quyền, ghi trực tiếp canonical store |

Core có interface cho encrypted storage/index, source reader, scoped access và model provider. Không xây plugin/key hierarchy tổng quát. Context quyền lấy từ server-side grant đã đổi token, không từ identity/scope do model tự khai.

Định hướng kiến trúc: application/service giữ key khi owner unlock, tạo catalog projection và pending requests, kiểm tra scope trước khi trả dữ liệu. CR01 hiện thực việc đổi bearer owner-approved một lần lấy session memory-only từ snapshot đã đóng băng; owner có thể liệt kê và thu hồi chọn lọc session đang sống, còn agent pipe cung cấp `agent.access.authorize` để kiểm tra operation cùng source/destination resource IDs/revisions chính xác, expiry, persisted policy generation và unlock epoch.
 Authorization API chỉ trả quyết định allow/deny, không lấy/giải mã/trả nội dung hoặc đọc cache. Lock xóa session trong bộ nhớ, process restart làm mất session, unlock tăng epoch để vô hiệu hóa grant/session cũ. Đây không phải OS-bound agent identity hoặc sandbox; same-owner pipe SID chỉ xác thực transport, không chứng minh con người đã duyệt. MCP bridge (`agent-mcp --agent-pipe <name>`, stdio JSON-RPC 2025-11-25) chỉ phơi agent-channel surface — `agent-catalog-list`, `catalog-query`, `request`/`request-status`, `redeem`, `session`, `agent-read`, `authorize` (decision-only) — qua subprocess owner CLI, không vault/key/mint; `initialize` thương lượng protocol version theo offer của client (hỗ trợ 2025-11-25/2025-06-18/2025-03-26/2024-11-05, fallback bản documented), request cần đúng catalog IDs và purpose với provider/model/disclosure đi kèm khi khai báo; official Codex/OMP đã chạy cùng client catalog→request→approve→redeem→granted read→revoke/lock→denied trên synthetic fixtures (D027, one-time consent).
 Cụ thể, MCP tool `metabrain_authorize` nhận `sessionFile` và `operation` bắt buộc, `resourceId`/`resourceRevision` tùy chọn, rồi trả duy nhất quyết định allow/deny; tool không đọc nội dung resource.

 Implementation hiện là service/CLI Windows với owner control channel và agent access channel riêng: owner dùng `owner.status`, managed-resource `read`/`write --expected-revision`, `memory-create`/`memory-revise --expected-revision`/`memory-import --expected-revision`/`memory-read`, `source-create`/`source-revise --expected-revision`/`source-read`/`research-capture`/`research-read`, `relation-create`/`relation-accept --expected-revision`/`relation-reject --expected-revision`/`relation-unlink --expected-revision`/`relation-relink --expected-revision`/`relation-read`, `provision`, `unlock`, `recover`, `lock`, `grant`/`grants`, `collection-set`/`collections`, `catalog-preview`/`catalog-publish`/`catalog-list`/`catalog-withdraw`, `requests`/`request-preview`/`request-approve`/`request-reject`, `sessions`, `revoke-session`, `redeem`, `session`, `authorize` và `agent-read`; agent dùng `agent-catalog-list`, `catalog-query`, `request`/`request-status`, `redeem`, `session`, `authorize` và scoped `resource.read`/`source.read`/`memory.read`/`agent.source.read`/`agent.relation.read` và tạo link trong grant qua `agent.link.create`. Ghi canonical yêu cầu đúng live revision (tạo mới chỉ khi chưa tồn tại); staged ciphertext commit trước manifest nên crash không để nửa chừng, unlock dọn bytes/temp dở dang, và `memory-import` là biên import tường minh thay mọi sửa plaintext trực tiếp. Relation là canonical encrypted resource trên cùng vault với ID đặt trước `rel-`, hai endpoint đã kiểm tra loại (memory item hoặc registered source) cùng đúng revision đang live tại thời điểm tạo link (span chỉ gắn registered source), kind, người/job tạo, phương thức tạo, lý do, thời gian UTC và vòng đời proposed/accepted/rejected cùng trạng thái unlink có ghi rõ lý do; endpoint và kind không đổi sau khi tạo. `derived_from` và `supersedes` cùng là cạnh dẫn xuất nên cấm mọi chu trình (cả chuỗi pha trộn), còn `related_to` được phép có vòng. Khi endpoint đổi revision hoặc registered source được citation không còn available, link trực tiếp và các phụ thuộc bắc cầu được báo needs_review từ trạng thái live, không sửa nội dung item và không coi link accepted là mệnh đề đã đúng; revision đã ghim vẫn là bản ghi lịch sử trung thực. Đọc relation đòi đúng relation ID/revision được cấp và cả hai endpoint trong snapshot tại revision live với operation khớp loại endpoint (`source.read` cho registered source, `memory.read` cho memory item), nên endpoint liên vùng hoặc ngoài quyền chỉ nhận một denial chung không lộ metadata/title/count/backlink; agent tạo link trong grant `link.create` luôn ở trạng thái proposed với executor agent vì accept là thao tác owner. Source registry giữ metadata registered source (title/locator/version/availability, author khai báo khi có) dưới dạng canonical encrypted trên cùng vault, không copy body của nguồn ngoài; citation của item ghi sourceId cùng source revision đang live cộng span đã kiểm tra biên tại thời điểm ghi, và việc serve item không đọc registry nên grant item không tự mở source API; anchor đã ghi sau khi source đổi revision giữ nguyên là bản ghi lịch sử trung thực thay vì được coi là đã kiểm chứng lại. Từ S3-T4, `research-capture` lưu văn bản research phi cấu trúc nguyên trạng thành một registered source với text verbatim encrypted (với note, text là nội dung gốc duy nhất — không phải bản copy của nguồn ngoài) cùng zone bắt buộc và title/URL/tác giả/ngày nguồn tùy chọn; thiếu trường nào thì trường đó unknown, không suy từ link, không fetch URL, không phân loại/trích xuất, và text chưa từng xuất hiện trên metadata wire của source. `research-read` trả đúng byte văn bản kèm metadata; khi vault locked cả capture lẫn read bị từ chối `vault_locked` và không ghi plaintext draft/queue hay báo saved; hai operation này owner-only và agent channel không tới được (transport allowlist). Lifecycle domain là canonical encrypted record trên cùng vault với ID đặt trước `lc-` suy ra từ subject memory item (một subject một record; zone, kind, trạng thái đầu, executor và revision đều suy ra server-side từ subject envelope, trusted context và vault descriptor, không nhận từ body request), với `lifecycle-create`, `lifecycle-read`, `lifecycle-retained-read --resource-revision` và các act owner `lifecycle-publish`/`lifecycle-reject`/`lifecycle-withdraw`/`lifecycle-repropose`/`lifecycle-question-resolve`/`lifecycle-question-reopen`/`lifecycle-intention-complete`/`lifecycle-intention-cancel`/`lifecycle-intention-supersede`/`lifecycle-intention-notify`/`lifecycle-intention-acknowledge`/`lifecycle-validity-set`/`lifecycle-retain --expected-revision`. Publication proposal/published/rejected/withdrawn chỉ đổi qua act tường minh có actor và lý do ghi trong history append-only; câu hỏi không tự resolved bởi AI answer hay relation addresses; notified/acknowledged là marker riêng chứ không phải done; supersession tường minh tên item thay thế và supersession mâu thuẫn bị từ chối chứ không ghi đè kiểu newest-wins; occurred/recorded/valid_from/valid_until/review_after giữ đúng precision, timezone offset và unknown (unknown không bị bịa ngày). Revision lịch sử còn truy được qua service thật: act `lifecycle-retain` tường minh ghi byte đúng revision đó vào archive resource ID suy ra `arc-<subject>-r<N>` trên cùng store và `lifecycle-retained-read` trả đúng envelope lịch sử kèm cả hai revision, còn grant chỉ trên archive không mở item live hay revision mới hơn; việc owner lưu/sửa note của AI không đổi author, creation method hay evidence level của bản ghi gốc. Membership collection chỉ là selector phía owner, nằm trong encrypted scope state: `owner collection-set`/`collections` đổi membership và tăng policy generation, còn `agent.collection.view` (qua `owner agent-collection-view`) chỉ chiếu lại snapshot đã đóng băng của session thành đúng các resource ID/revision đã duyệt — không có tên collection, thành viên, title, zone, kind, count, total hay paging, và grant trên một tập con của collection không bao giờ thấy thành viên ngoài phạm vi đã duyệt. Thêm member, sửa title/path hay tạo descendant zone/lifecycle/archive mới không vào được view đang sống: session cũ fail closed thay vì tự mở rộng scope, và membership không tự cấp quyền đọc thành viên chưa duyệt hay relation/lifecycle/archive/source chỉ trỏ tới thành viên đã duyệt.
Updated implementation (S4-T6; this supersedes earlier S2-T4 agent link command names): agent `relation.resolve`/`relation.list` use the `relation.list` grant operation; resolve searches only exact frozen memory/source IDs and revisions with matching `memory.read`/`source.read`, returns titles only, and emits `clarification_required=true mutation_performed=false` for ambiguity. List returns relations only when both pinned endpoints independently satisfy those rights. The CLI `agent-relation-create`/`supersede`/`unlink`/`restore` and MCP `metabrain_relation_create`/`supersede`/`unlink`/`restore` route through the same service policy; each requires its corresponding `link.create`/`link.supersede`/`link.remove`/`link.restore` plus both endpoint reads. Agent creation stamps executor `agent`, method `paraphrase`, and state `proposed`; caller-reported request evidence is an encrypted relation event shown only to the owner, not a trusted user-confirmation signal. No relation operation changes endpoint content, opens a revision/zone/membership, or declassifies data.


Mỗi vault có data key ngẫu nhiên được wrap riêng bằng passphrase và recovery key chỉ hiện một lần; .NET chuẩn cung cấp PBKDF2-HMAC-SHA-256 (600.000 vòng) và AES-256-GCM. Manifest và metadata resource nằm trong ciphertext; resource file dùng tên ngẫu nhiên, ciphertext được xác thực gắn với vault/resource ID/schema/revision. Key không được lưu plaintext cạnh vault, trong settings, environment hoặc log. Grant/verifier/request state và owner-managed flat collection membership nằm trong encrypted `scope-grants.enc` sche…

Catalog publication lưu private catalog-ID→resource mapping trong encrypted `owner-catalog.enc` (chỉ explicit owner label/description, không suy từ private text), phục vụ discovery từ plaintext `published-catalog.json` cạnh vault (chỉ approved ID/label/description, rebuildable). Discovery không cần session, hoạt động khi locked/startup/restart; withdrawal xóa record nên list/query/fresh/restart hội tụ `no_match`. Catalog không cấp body/source access. Access request lưu pending/mục đích/agent/provider/disclosure đã khai báo trong encrypted scope state; agent chỉ nhận opaque receipt/status, owner preview đọc exact body/revision/scope/disclosure rồi narrow/approve/reject; chỉ narrowed owner-approved exact scope mint token qua T3. Stale preview (withdrawal/republication/revision/registration/collection change, epoch/generation) phải preview lại, không tự mở scope. Provider egress authorization khớp exact provider/model v…
Phân tách source references, inbox/proposals, published memories, intentions, private index và catalog công bố. Private policy/grants/audit/locators/revisions cũng phải được bảo vệ; chỉ crypto-format envelope tối thiểu không nhạy cảm và metadata owner chủ động công bố được nằm ngoài encryption. Tên thư mục không tiết lộ private zone/title, và adapters không phụ thuộc physical path.

**Historical S4-T2 baseline (superseded by S8-T5):** the SQLite/schema-2 description immediately below is retained to document the original behavior; it is not the current format.
 Implementation (S4-T2): scoped search (`agent.memory.search` trên agent channel với grant operation `memory.search`, `owner agent-search` trên CLI, `metabrain_memory_search` trên MCP bridge) chạy trên đúng protected private index đã materialize (schema 2, memory-only, thêm token-posting và temporal tables keyed theo resource ID và revision): candidate filter theo snapshot IDs/revisions đã duyệt chạy TRƯỚC mọi private retrieval (scope IDs bind trong SQL WHERE của index engine, chỉ page được phục vụ mới decrypt ở đúng frozen revision với authorize trước/sau và registration check từng hit). Query mở rộng cục bộ không embedding/provider (case/diacritic folding, symbol/camelCase split, light English stemmer, stopwords, bảng general-vocabulary EN/VI như error/bug/lỗi, login/đăng nhập — áp dụng đối xứng mọi query, không lookup theo fixture). Ranking theo token overlap có trọng số, hạ withdrawn/rejected/stale; lọc kind/current theo metadata; pagination ổn định trên frozen order với scope-only totals; stale generation fail closed `search_index_stale`; revision mới không vào grant; lock/revoke deny calls tiếp theo. Catalog query giữ nguyên published labels/descriptions, không private ranking/snippets/counts. Chỉ owner channel được materialize private index; agent channel không có operation index nào. Private index (S2-T7) là SQLite standard (`Microsoft.Data.Sqlite` 10.0.12, MIT) chạy hoàn toàn trong bộ nhớ tiến trình (`Data Source=:memory:`, `PRAGMA temp_store=MEMORY`, `journal_mode=OFF`, `locking_mode=EXCLUSIVE`): không có database file, WAL, rollback journal, sort temp hay page cache nào trên disk và engine không giữ key material. `owner index-rebuild` đọc manifest, giải mã từng canonical envelope và ghi lại đúng ID, revision, zone scope, record kind/state cùng các ID được tham chiếu (citation, relation endpoint, lifecycle subject/supersession/retained archive); `owner index-status` fingerprint SHA-256 tập (resource, revision) canonical đang sống và báo generation đã materialize là `stale` thay vì tin nó. Schema index có version (`PRAGMA user_version`, hiện 1) với migration step 0→1 thật và `RequireSupported` fail closed cho version lạ; S8 thêm step mới không phải sửa caller. Rebuild không ghi canonical: canonical file vẫn là truth duy nhất, lock/unlock/restart/crash đều bỏ index, và rebuild không chứng minh được generation khớp canonical thì trả `index_canonical_moved`. Index không tự công bố title/body/count/embedding vào catalog và không mở surface cho agent; ranking/search thuộc S4.

(S8-T5 cutover: the current private index is the persistent encrypted v3 generation, not the
historical S2-T7 SQLite engine. Current source and the latest measured evidence are in
BOUNDED-A14 below; A8/A11/A12/A13 remain historical records. The in-process memory observation
is an adverse proxy result, not service acceptance. Service/UI/IPC and owner R37 gates remain open.)

**Historical D008 baseline (pre-S8-T5 SQLite, real-service/IPC measurement):** the measurements below are retained as history, not as current index, service/UI or A12 evidence.

Ngân sách vận hành (D008) đã đo thực tế trên máy đích qua real service, real named pipe và real owner CLI với synthetic fixture 1,000/10,000/100,000 records (4 KiB mean body, tạo trong một pass O(n) qua seeded vault path). `owner index-rebuild` đọc envelope theo batch giới hạn (128/batch, song song bậc 8) bên trong một counted operation: mỗi file vẫn giữ reparse, AEAD binding và revision gate riêng (S9-T1: không còn filesystem-ACL gate hay custom pipe descriptor trên đường daily), plaintext được zeroize sau khi insert, và lock chỉ xóa key sau khi operation đã drain. Baseline bất lợi được giữ nguyên làm ground truth: binary trước fix, fixture 10,000 cache-cold, cold rebuild 111,511 ms = 89.7 records/s (< ngưỡng 100 records/s). Sau fix, trên binary cuối cùng (lần rebuild đầu tiên sau khi fixture được tạo và aging tự nhiên ~30 phút, không có flush/trim/admin/forcedGC; wall time không phải chứng cứ miss của OS độc lập): 1,930 ms (518.1 records/s) ở 1,000 / 23,418 ms (427.1 records/s ≥ ngưỡng 100 records/s) ở 10,000 / 271,326 ms (4.5 phút, 368.7 records/s ≤ 20 phút) ở 100,000; idle working set tối đa 77,344,768 B / 103,419,904 B / 237,543,424 B (~73.7 / ~98.6 / ~226.5 MiB ≤ 256 MiB) ở ba kích thước sau warm-up trong cửa sổ 5 phút (61 samples, không forced GC hay OS trim); idle CPU 0.020115% / 0.000649% / 0.003244% host (≤ 1%); persistent product data 8,653,390 B / 86,599,822 B / 1,132,272,603 B (~8.3 MiB / ~82.6 MiB / ~1.08 GiB — combined data+staging vượt 1.5 GiB envelope tổng ở 100,000 như A17/A18 đã ghi) và 0 GiB ở mọi kích thước; để hỗ trợ kích thước 100,000 resources đã công bố sẵn có, manifest đọc được cấp cap riêng (64 MiB plaintext / 96 MiB envelope, trên worst case 100,000 descriptor) thay vì dùng chung cap envelope 4 MiB của resource, và request `owner.index.rebuild` được re-arm timeout 25 phút cho riêng operation đó (mọi request khác giữ nguyên guard 10 giây). Lock trong lúc rebuild đang chạy dừng rebuild đó thay vì chờ nó chạy hết: lock request huỷ các batch read đang in-flight tại batch boundary, bounded active I/O drain xong mới clear key (các invariant drain/lifetime giữ nguyên — lock vẫn chỉ hoàn tất sau khi counted operation dispose), rebuild bị huỷ fail closed với status riêng `index_rebuild_cancelled` và phân biệt với timeout 25 phút của chính request đó (vẫn trả `request_timeout`). Đã kiểm chứng trên real pipe ở 10,000 và 100,000 records: lock hoàn tất trong 315 ms với rebuild bị huỷ, cùng một request đọc hợp lệ chỉ bị từ chối khi locked và được phục vụ lại đúng body sau re-unlock, rebuild ở epoch mới sau huỷ hoạt động bình thường, hai lock chồng lấn đều an toàn, và negative tamper không đổi. Combined service+idle-client ≤ 512 MiB và latency owner UI/search không được exercise bởi gate này (không có UI/MCP surface trong S2-T7) nên không có số đo nào được tuyên bố cho các mục đó.

Các invariant bắt buộc:

- Cập nhật có revision check, ngăn lost update; publication và relation changes không để trạng thái nửa chừng sau crash.
- Quyết định thứ tự ghi canonical/index và recovery rõ ràng; không hứa một transaction chung cho file + index nếu chưa có cơ chế thực. (S8-T5: index không còn là SQLite — nó là một generation file mã hóa; xem mục BOUNDED-A8 bên dưới.)
- Journal/recovery chỉ đủ cho invariant cần thiết, không xây event-sourcing platform tổng quát.
- Rebuild index khi unlocked tái hiện đúng scope, revision, lifecycle và nguồn khả dụng; không viết plaintext DB/WAL/temp hoặc trở thành truth store thứ hai.
- External ciphertext sửa đổi/truncated, wrong key và stale revision bị reject/recover theo protocol; không hỗ trợ “sửa trực tiếp plaintext canonical file”. Import/export owner rõ ràng thay đường đó.
- Backup/export không hồi sinh live sessions/tokens; encrypted backup cần key/recovery của user. Chỉ explicit owner export được tạo plaintext với cảnh báo copies không thu hồi; migration không reset/xóa dữ liệu cũ để né chuyển format.

Implementation (S8-T2): `owner backup --control-pipe --output-file <file>` ghi **một** file `.mbbackup` chứa snapshot đã mã hóa của vault đang unlock và key envelope (dạng passphrase-wrapped và recovery-wrapped) của chính vault đó; file không chứa data key plaintext, credential, token, session hay nội dung record dạng đọc được, và không ghi gì vào vault sống. Snapshot được chụp dưới đúng content gate mà mọi read/write của vault dùng nên là **một thời điểm nhất quán**: một write đến giữa lúc chụp thì chờ, rồi commit bình thường, không mất và không áp dở. `owner restore` / `owner restore-with-recovery-code` mở đúng backup đó bằng passphrase hoặc recovery key của owner và dựng một vault sạch trong thư mục **chưa tồn tại**; vault được phục hồi giữ nguyên record, revision, relation, source, lifecycle/intention, archive/retained revision và trạng thái đã xóa, và mở lại được bằng chính passphrase/recovery key đó vì envelope được mang theo chứ không bị re-key. Restore **không** hồi sinh authority: scope-grant state được khôi phục không còn grant sống, unlock epoch về 0, danh sách token đã dùng được giữ nguyên nên token đã redeem không redeem lại được, session file nằm ngoài vault nên không quay lại, và private index được rebuild từ canonical đã phục hồi chứ không được sao chép. Sai key, mất key, container bị sửa hoặc container hỏng đều fail closed: không ghi gì, không để lại vault dở, không reset, không sinh key hay “recovery” giả. `owner export --resource-ids ... --output-file ...` là disclosure tường minh của owner: file plaintext chứa **đúng** các record được chọn ở dạng đầy đủ, một citation tới source ngoài phạm vi bị withhold trọn vẹn (không tên source, không locator, không span) và file tự nói header mình đã withhold bao nhiêu citation/relation/record; file được ghi một lần, không ghi đè, và bản copy đã mang ra ngoài không bị thu hồi bởi purge/lock/export sau đó. Tất cả ba act là owner-only trên owner channel, dùng chung core/owner API, không có UI ghi vault trực tiếp.

Implementation (S8-T3): vault cũ vẫn mở được; một owner act `owner upgrade` (hoặc `upgrade-with-recovery-code`) nâng **content schema** của các file đang cũ lên schema build này ghi — owner scope-grant state v2/v3 → v4, canonical lifecycle v1/v2 → v3, model job v1 → v2 — trong khi envelope/manifest vẫn là format 1 và key envelope không bị viết lại (đây không phải migration envelope và không re-key). Act chỉ chạy khi vault đang **locked**, không giữ key sau khi xong, và giữ nguyên resource ID, zone, attribution, relations, lifecycle, archive và revision của từng record. Trước khi sửa file đầu tiên, act copy đúng các file sẽ sửa vào rollback point `vault\rollback\<id>\` dưới dạng chính ciphertext của vault (không plaintext, không key thứ hai) kèm journal không chứa nội dung; mỗi lần ghi là atomic temp-then-move nên kill giữa chừng để lại mỗi file hoặc bản cũ hoặc bản mới, cả hai đều đọc được, và lần `owner upgrade` sau resume đúng journal rồi verify lossless trước khi đóng dấu complete. `owner status` trả `format=current|upgrade_available|interrupted|unknown` + `rollback=available|none` từ format record không nhạy cảm (`vault-format.json`, chỉ version/count) và journal, không cần credential. `owner rollback` chỉ phục hồi các file upgrade đã ghi và còn đúng như upgrade để lại; file owner đã đổi sau đó được giữ và báo `skipped`; rollback point bị tiêu thụ sau lần rollback đầu, `--discard true` xóa nó. Passphrase/recovery/KDF và header giữ nguyên byte-for-byte nên backup tạo trước upgrade vẫn restore được sau upgrade bằng đúng credential đã dùng. Console có card *Vault format* trong view Backup & export với cùng status/selector/upgrade/rollback; legacy plaintext cutover (`owner migrate`) không còn đổi ACL/owner, chỉ ghi vault + settings rồi xóa bản plaintext đã migrate, và resume được nếu bị ngắt giữa vault và settings khi passphrase mở vault và mọi record legacy còn byte-identical.

Implementation (S8-T2 owner console): view **Backup & export** trong owner console WPF thực hiện đúng ba act đó qua owner control channel (`vault.backup`, `vault.restore`, `vault.export`). Console không mở/ghi vault file, không giữ key, không tự quyết policy và không có store thứ hai: mọi câu trả lời trên màn hình là câu trả lời của service, mọi quyết định về phạm vi thuộc về service. Owner chọn **file** backup bằng picker (có thể gõ đường dẫn), chọn **file** backup + **thư mục** đích cho restore (thư mục đích phải chưa tồn tại) cùng credential (owner passphrase hoặc recovery key), và chọn **scope** export từ danh sách record do service xếp hạng, hiển thị **title** trước (kèm các source mà record được chọn trích dẫn, để owner tự quyết có đưa source vào phạm vi hay không — không có gì được tự động thêm vào phạm vi, và citation tới source ngoài phạm vi vẫn bị withhold trọn vẹn). Credential đi trong **đúng một** request rồi field bị xóa ngay sau đó, không ghi log, không ghi file, không hiện lại. Lock, end session và disconnect xóa path đã chọn, selection export, credential và bỏ mọi câu trả lời đang bay (epoch guard), không tự unlock và không tự duyệt. Backup và export cần vault đang unlocked; restore vẫn dùng được khi vault locked vì nó đọc file backup và ghi thư mục mới. Vault sống không bao giờ bị thay: restore ghi một thư mục mới, và service từ chối khi thư mục đích đã tồn tại.

## 8. Session cũ: giữ nguyên, tham chiếu và chuẩn hóa ở ngoài

Không migrate, đổi tên, xóa, sửa hoặc di chuyển session .codex/.omp theo mặc định. Adapter read-only tạo model chuẩn hóa để index; nguồn gốc vẫn là file runtime quản lý.

Source registry giữ runtime, session identity, locator nội bộ, event/message ID khi có, fingerprint/version, checkpoint và zone mapping. Xử lý append, partial JSONL tail, retry/import lặp, rotation, replacement/truncation và thay đổi format có báo lỗi rõ; không tự bỏ mất dữ liệu để tiếp tục.

Không lấy filename hoặc cwd làm bằng chứng phân quyền. Nguồn không rõ mapping vào private quarantine; session lẫn nhiều dự án không tự mở quyền toàn transcript cho một project. Dedupe không nối hoặc lộ nội dung qua zone trái quyền.

Hai policy:

- Reference-only mặc định: không sao chép toàn bộ lịch sử.
- Preserve selected sources: owner chọn snapshot nguyên trạng/đoạn bằng chứng, trong zone và retention phù hợp; nguồn gốc không bị sửa.

Nguồn mất hoặc đổi: đánh dấu missing/changed, phân biệt bằng chứng còn snapshot với bằng chứng không còn đọc được. Không tiếp tục báo citation đã được kiểm chứng. Chỉ mục/chunk cache có thể là bản sao nhạy cảm dù nguồn dùng reference-only; retention và purge phải bao phủ chúng.

Meta Brain không bảo mật lại original transcripts mà source runtime/agent đã tạo hoặc đọc; source allowlist chỉ kiểm soát ứng dụng import/serve/send. Mọi bản sao, chunk, locator/provenance và snapshot bên trong Meta Brain vẫn encrypted; ingestion/private source expansion dừng khi vault khóa. Quyền source trên API độc lập item, không cam kết chặn agent đọc original bằng quyền OS của nó.

Implementation (S3-T5): `owner source-status --source-id` checks only the exact enrolled locator and hashes its complete bounded contents, so a same-size/same-mtime edit is still `changed`; a missing path is reported as `missing` without scanning or guessing where it moved. `source-relocate` is a separate owner action: it accepts only an explicitly supplied candidate whose length and digest match the enrolled generation, then updates the encrypted enrollment mapping. A subsequent exact status observation synchronizes that verified locator and availability into a new registered-source revision, making grants frozen to the old revision stale. The original file is never modified, moved or deleted by the application.

Reference-only remains the default. `owner source-snapshot-preserve --source-id --source-revision --retain-until-utc` explicitly captures the full bytes of the enrolled generation into one revisioned resource in the shared encrypted canonical vault, together with its source ID/reference revision, generation, zone, length, SHA-256 digest, capture time and owner-selected future UTC retention. `source-snapshot-list/read/retain/purge` operates on that snapshot resource; the CLI read requires an explicit `--output-file` export.

Implementation (S3-T6): `owner source-list` reports every enrolled source with its life, persisted checkpoint, refresh consent and actual encrypted cache scope (journal/snapshot ciphertext sizes and snapshot revision, resolved from the live manifest — never a renamed consent field) without opening any transcript or echoing private locators; `source-status` adds the same source/checkpoint/error/cache-scope view for one source. `source-pause` stops status reads, ingest and refresh for that source with no transcript open, and `source-resume` clears the pause so the next ingest continues from the persisted checkpoint without duplicates. `source-refresh-optin/optout` records one explicit owner consent flag per source; `source-refresh` runs a single idempotent incremental pass over the allowlist that ingests only opted-in, active sources from their checkpoints while unlocked and reports an explicit skip reason with no read for every other source, and an owner-opt-in background loop runs the same pass periodically while unlocked so appended sources advance with no manual command. A requested lock cancels the in-flight pass at its next per-source boundary (the source-byte boundary also polls the signal mid-read, zeroing partial bytes with no checkpoint movement), and pause/disable stop reads at the same boundary. There is no filesystem polling, directory listing, autonomous unlock or plaintext key: lock and restart deny every private read and refresh with no plaintext queue, and unlock resumes checkpoints and consent without duplicates. Revoke stops refresh and an explicit re-enrollment starts a new generation on a fresh checkpoint. Diagnostics carry bounded codes only, with no transcript, locator or secret.

Adapter Codex session JSONL (S3-T2, read-only, coordinator S3-T1) hỗ trợ đúng schema đã quan sát: envelope một JSON object mỗi dòng `{timestamp, ordinal?, type, payload}`; bản ghi `session_meta` (biến thể CLI và Desktop), `response_item` (`message` role `user`/`assistant`/`developer`, `reasoning`, `function_call`, `function_call_output`, `custom_tool_call`, `custom_tool_call_output`, `local_shell_call`, `tool_search_call`, `tool_search_output`, `web_search_call`, `image_generation_call`, `agent_message`, `additional_tools`, `compaction`, `context_compaction`, `configuration_update`, `compaction_trigger`), `event_msg`, `turn_context`, `world_state`, `token_usage_record`, `compacted`, `inter_agent_communication`, `inter_agent_communication_metadata`, `retained_context`, `security_risk_score`, `realtime_item`. Định dạng owner khai báo là `codex.session.jsonl` phiên bản 1. Attribution chỉ lấy từ khai báo của chính nguồn: chỉ `message` role `user` thuộc user; tool call thuộc assistant, tool output thuộc tool; notification/control thuộc system. Mỗi bản ghi chấp nhận tạo đúng một event reference-only (identity, attribution, thời gian, digest và span chính xác), không copy body. Bất kỳ record/payload type ngoài tập đã quan sát/định nghĩa upstream bị từ chối tường minh kèm diagnostic đặt tên tại đúng offset; checkpoint không bao giờ vượt qua byte chưa giải thích và không có silent drop. Giới hạn đã biết: writer chưa quan sát (ví dụ biến thể `event_msg` payload type mới) và bản ghi tool không có trong mẫu owner cho phép thì chưa được kiểm chứng trên session thật; journal giới hạn 4.096 event mỗi generation và snapshot 16 MiB mỗi lần ingest.

Adapter OMP session JSONL (S3-T3, read-only, coordinator S3-T1) hỗ trợ đúng schema đã quan sát trong một session OMP hoàn chỉnh được owner cho phép của project này (session record `version` 3, title record `v` 1; writer là OMP runtime; không có upstream format definition đọc được trong consent nên không claim shape chưa quan sát): envelope một JSON object mỗi dòng; `title` (`v` 1, không có event id nên identity neo theo byte offset), `session` (version 3, branch root mang session identity và cwd context), `message` (role `user`/`assistant`/`toolResult`; user-role chỉ thành user statement khi chính record khai báo attribution `user`; tool result không bao giờ thành user statement), `custom` (envelope `{customType, data}`, data không bao giờ được diễn giải), `custom_message` (envelope `{customType, content, display, details, attribution}` với attribution `user`→user, `agent`→assistant), `thinking_level_change`, `service_tier_change`, `credential_pin`, `title_change`, `compaction` (giữ `firstKeptEntryId` boundary đã validate). Content parts hỗ trợ `text`, `thinking`, `toolCall`. Timestamp envelope là ISO-8601 UTC (`updatedAt` ở title record); message body mang `message.timestamp` dạng epoch-millis và đó là instant event giữ. Định dạng owner khai báo là `omp.session.jsonl` phiên bản 1. Chuỗi id/parentId được giữ nguyên qua event identity (null parent là chain root/branch origin); mỗi bản ghi chấp nhận tạo đúng một event reference-only (identity, attribution, thời gian UTC, digest và span chính xác), không copy body. Bất kỳ record/shape/role/part/stop-reason/attribution/version ngoài tập đã quan sát bị từ chối tường minh kèm diagnostic đặt tên tại đúng offset; checkpoint không bao giờ vượt qua byte chưa giải thích và không có silent drop. Giới hạn đã biết: mẫu thật chứa đúng một session (617 records) nên branch/fork đa nhánh chỉ được chứng minh trên fixture tổng hợp; session writer version khác 3 hoặc title `v` khác 1 bị từ chối thay vì stretch.

### 8.1. Approved contract and S11-T2–T5 implementation — folder enrollment and raw-preserved parsing (CR02/D088, 2026-10-29)

Owner đã duyệt [CR02](CR02-PROPOSAL.md) với **D1=B, D2–D8=A**; quyết định tại
[D088](../.agents/memory/decisions.md) và change record tại
[REQUIREMENTS](REQUIREMENTS.md). Installed 1.0.0 giữ fail-closed contract cũ; source hiện tại đã
triển khai owner folder enrollment (S11-T2), raw-preservation/interpretation separation (S11-T3),
folder refresh/recovery (S11-T4) và owner console ingestion surface (S11-T5). Các section S11-T2–T5
dưới đây mô tả 1.1.0 đã phát hành ngày 2026-11-01; cài đặt 1.0.0 cũ không có các surface này và
giữ fail-closed contract trước đó. Public 1.0.0 links/URLs/assets vẫn giữ nguyên bên cạnh 1.1.0.

**Bốn facet trạng thái độc lập.** Surface owner không được gộp trạng thái thành một nhãn ok/error:

| Facet | Giá trị | Ý nghĩa |
| --- | --- | --- |
| `availability` | `available`, `missing`, `changed`, `locked`, `unreadable`, `empty` | Quan sát trên generation/locator hiện tại; `empty` tách khỏi `unreadable` |
| `preservation` | `reference_only`, `preserved(retention)` | Bytes gốc của generation/record nằm encrypted trong vault; mặc định `reference_only` |
| `interpretation` | `fully_supported`, `partially_interpreted`, `uninterpreted`, `empty` | Mức hiểu **đã đạt**, không phải mức sẽ đạt |
| `derivation` | `not_indexed`, `indexed_text` | 1.1.0 giữ `not_indexed`; không thêm source text indexing |

`preserved` không hàm ý `partially_interpreted`; `partially_interpreted` không hàm ý `preserved`.
`fully_supported` chỉ dùng khi mọi record hoàn chỉnh đã được diễn giải và không còn byte chưa xử lý
ngoài tail dở dang. Status owner phải kèm `pending_tail_bytes`, `preserved_bytes`,
`uninterpreted_records` và `last_diagnostic {code, offset}`; remediation theo file/offset, không echo
transcript.

**Đơn vị lỗi và continuation (D1=B).** Đơn vị là một record hoàn chỉnh theo framing của format (JSONL:
một dòng kết thúc `\n`). Record chưa diễn giải được chỉ được vượt qua khi bytes chính xác của record đó
đã được lưu bền vững, encrypted và đọc lại được trong cùng giao dịch journal/checkpoint; khi đó record
là preserved-uninterpreted và checkpoint được phép đi qua. Không có "skip bytes rồi tiếp tục" khi chưa
preserve; tail dở dang (`NeedMoreBytes`) không bao giờ được preserve như record và không bao giờ đẩy
checkpoint.

**Version/root barrier (D3=A).** Record khai báo version mới của format/root (ví dụ OMP
`session.version > 3`) là barrier: preserve tail nếu owner bật preservation nhưng **không** diễn giải
tiếp, vì version mới có thể đổi nghĩa toàn bộ phần sau. Status phân biệt `unprocessed_bytes` (chưa
preserve) với `preserved_pending_bytes` (đã preserve nhưng checkpoint chưa qua barrier).

**Encoding (D4=A).** Không lossy transcode, không thay byte lỗi bằng `U+FFFD` rồi coi là đã đọc. BOM và
UTF-8 lỗi theo cùng luật: framing xác định thì preservable, nhưng không có BOM tolerance ngầm.

**Offset, attribution, coverage.** Offset tuyệt đối theo byte trong generation đã enroll, không theo ký
tự/dòng. Span do coordinator gán `[start, end)`; normalizer không tự khai span. `[0, checkpoint)` phải
phủ kín bởi event span hoặc preserved span rời nhau, không lỗ hổng. Với `reference_only`, suffix
`[checkpoint, size)` là vùng chưa xử lý hợp lệ và phải báo `unprocessed_bytes`; với `preserved`, suffix
chỉ được còn `pending_tail_bytes`. Attribution chỉ từ khai báo của nguồn: record chưa hiểu không có
author, không suy `User` từ tên file/folder/cwd/nội dung; preserved bytes không được trả như memory
evidence hay citation.

**Chain identity (D5=A, D7=A).** Preserved node chỉ được admit `id`/`parentId` khi envelope của record đã
được validator độc lập của format/version chấp nhận (JSON object, `type` non-empty string, token `id`,
`parentId` null/token, timestamp hợp lệ) — độc lập với `type`/payload đã biết. Envelope không validate
(JSON/encoding/version barrier/thiếu field) không cấp identity; record sau trỏ tới id không được admit
phải refusal `omp_chain_reference_unresolved`, không được chain-valid. Không suy identity/author từ
path/folder/cwd/text/vị trí.

**Preservation consent và retention (D2=A).** Preservation là hành động owner, bật theo nguồn/folder,
mặc định tắt. Bytes preserve dùng đúng cơ chế encrypted vault hiện có (không crypto/key store mới),
không plaintext scratch/log/temp. Preserved span gắn `(source, generation, offset, length, digest)` và
đọc lại phải trả đúng bytes gốc, verify digest. Preserved bytes nằm trong purge/backup/export/retention
và không đi qua MCP/catalog hay cấp quyền agent mới. Không bật preservation thì phải nói rõ khả năng đọc
lại phụ thuộc nguồn.

Retention deadline là hạn đọc lại, không phải purge trigger. Sau deadline, `source-preserved-record-read`
từ chối đúng span đó bằng `source_preserved_record_expired`, trong khi ciphertext đã preserve vẫn nằm
trong journal encrypted, `source-status` vẫn liệt kê span kèm `retainUntilUtc`, và journal revision cùng
bốn facet trạng thái không đổi; expiry không tự purge hay rewrite, nên xoá span vẫn cần owner action
tường minh (purge). Opt-out (`source-raw-preservation-optout`) chỉ dừng preserve record mới: không rút
ngắn retention đã lưu của span cũ và không xoá span. Opt-in mới bắt buộc deadline UTC tương lai (deadline
đã qua bị từ chối), nên không có đường đọc lại quá hạn bằng consent mới.

**Checkpoint, crash, thay đổi nguồn.** Journal + checkpoint vẫn là một resource write nguyên tử.
Retry không tạo event trùng; preserved span idempotent theo `(offset, length, digest)`. Crash trước
commit giữ revision cũ; append giữ anchor cũ; truncation/replacement vẫn quarantine (`generation_truncated`
/ `generation_replaced`), không splice generation mới, preserved span của generation cũ không thành
evidence của generation mới. Pause/lock dừng đọc private và preservation.

**Journal compatibility (D8=A).** Journal v2 chỉ được ghi khi lần ghi thực sự chứa ít nhất một phần tử
chỉ có ở v2 (preserved span hoặc record status v2-only); source reference-only tiếp tục ghi v1 và giữ
hành vi 1.0. Reader 1.1 đọc v1+v2; schema v3+ fail closed, không đoán/rebuild; không auto-downgrade
source đã ghi v2. Upgrade 1.0 → 1.1 không rewrite journal cũ, không migrate vault; source fully
supported phải giữ đúng event/span/digest như 1.0.

**Folder enrollment và reparse boundary (D6=A).** Đơn vị enroll là root do owner chọn tường minh, kèm
include/exclude tường minh và preview trước khi xác nhận một lần; không mặc định scan home/profile hay
toàn ổ đĩa. Enumeration không đi theo reparse point (symlink, junction, mount point); entry bị loại với
lý do `excluded_reparse_point`, không vào allowlist, không được coi là file mới. Không admit path ngoài
root và target của link không tự thành nguồn. Mỗi file admit vẫn qua reader hiện tại: final-path equality
với locator, từ chối reparse/directory, share mode đọc không cho writer, kiểm tra độ dài ổn định. TOCTOU
được thu hẹp bởi final-path check, không tuyên bố loại trừ tuyệt đối. Path Windows so sánh
case-insensitive; không đổi ACL/permission hay cấu hình host.

### 8.1.1. Owner-approved S11-T4 folder refresh policy (2026-10-31)

Owner-approved folder refresh uses explicit `folder-refresh-optin` / `folder-refresh-optout`, default off. The enrollment preview and confirmation are not refresh consent. Opt-in covers current and future matching regular files within the selected root and stored include/exclude rules, subject to the no-reparse enumeration and per-file final-path checks above. New members are admitted and read only while the folder remains opted in and the source's own controls allow it.

A member's pause, revoke, or explicit source refresh opt-out vetoes folder refresh; folder opt-out and vault lock stop the pass at a source boundary without changing member consent or committed data. Deletion leaves canonical/history intact and reports the member missing; it never triggers automatic revoke or purge. Manual and background folder refresh use the same coordinator. This is contract approval, not runtime evidence or a T4 completion claim.


## 9. Research và cấu trúc hóa

Đầu vào bắt buộc là ô nhập văn bản phi cấu trúc, chọn zone, có thể thêm URL/tác giả/ngày nguồn. Không cần crawler hay PDF parser để sử dụng được. URL chỉ là locator, không tự chứng minh đã đọc bài gốc.

Lưu source trước rồi mới chạy job; provider lỗi không làm mất ghi chú. Pipeline dùng chung:

1. Normalize nguồn và attribution; trường chưa biết để unknown.
2. Structure thành item proposal, mỗi item trỏ đoạn nguồn hỗ trợ.
3. Đề xuất relation trong quyền đọc; similarity không thành causality.
4. Core validate schema, provenance, zone và trạng thái.
5. Owner review/publish; policy tự động chỉ áp dụng phần owner đã cho phép.

Implementation (S4-T5): agent gửi proposal qua `agent.propose` (grant operation `proposal.create`, `owner agent-propose`, `metabrain_propose` trên MCP bridge) và proposal chỉ là record pending `prop-` trên canonical encrypted store — không publish, không mutate subject item hay bất kỳ canonical record nào. Proposal cần operation được duyệt VÀ đích inbox riêng (destination scope trong cùng grant); service dẫn xuất ID từ (grant, destination, idempotency key) nên operation lặp lại trùng nội dung trả cùng một record, còn key trùng nội dung khác lỗi không mutate. Create proposal mang nội dung item đề xuất; revise proposal ghim subject item và đúng revision precondition — cả hai phải nằm trong frozen scope đã duyệt và subject phải tồn tại đúng revision đó (guessed/out-of-scope reference dùng chung một generic denial, không phải existence oracle). Executor, zone và recorded time do server stamp; body không thể mang evidence `userConfirmed` và claim `userConfirmed` chỉ được lưu thành claim. Đọc proposal là owner channel (`proposal-read`, `proposal-list`) để review; publish vẫn là hành động owner, không thuộc task này.

Profile:

- Coding: vấn đề, cách thử, bằng chứng kết quả, quyết định, điều kiện môi trường.
- Research/paper note: câu hỏi, phương pháp được mô tả, claim của tác giả, giới hạn, giả thuyết của user, câu hỏi mở.
- Trend: observation theo thời gian, nguồn, phạm vi quan sát và giả thuyết xu hướng.

Ví dụ “tác giả báo cáo Y hiệu quả trên tác vụ ngắn; có thể thử ở project A; chưa rõ session dài” không được thành “Y hiệu quả ở project A”. Nhiều bài nhắc cùng paper không mặc nhiên là nhiều xác nhận độc lập.

Trend mặc định phản ánh nguồn user đã lưu, không tuyên bố bao phủ Internet. Giữ bản tổng hợp từng thời điểm, delta, phản ví dụ và khoảng trống, không ghi đè lịch sử.

Implementation (S3-T4): owner `research-capture --resource-id --zone-id --input-file [--title --source-url --source-author --source-date]` lưu text + zone thành một registered source encrypted qua đúng source registry trước mọi structuring/model job — không cần provider, không fetch URL, không phân loại/trích xuất; `research-read --resource-id [--resource-revision --output-file]` trả văn bản nguyên byte kèm metadata/zone kể cả sau restart/unlock. URL chỉ được giữ như locator (không tự đánh dấu đã đọc bài gốc); trường owner bỏ trống giữ unknown, không suy từ link hoặc từ text; author khai báo giữ tách biệt với executor của thao tác lưu. Khi vault locked cả capture lẫn read trả `vault_locked` rõ ràng, không báo saved và không persist plaintext draft/queue; hai operation chỉ thuộc owner channel.

## 10. Automation, synthesis và vòng lặp hằng ngày

| Việc | Mặc định |
| --- | --- |
| Index nguồn allowlist, checkpoint, ghi provenance | Tự động |
| Phát hiện nguồn đổi/link mất và đánh dấu cần xem lại | Tự động |
| Lưu/nối rõ ràng trong grant | Thực hiện, có khả năng hoàn tác phù hợp |
| Gợi ý liên quan/trùng lặp | Nhẹ, trong ngữ cảnh; không notification cho mọi proposal |
| Cấu trúc hóa/tổng hợp | User yêu cầu hoặc lịch/policy opt-in |
| Chọn agenda, đổi niềm tin, tự đóng câu hỏi | Không tự quyết |
| Share liên vùng, đổi quyền, purge | Owner kiểm soát |

Không extraction mọi token. Agent đang làm việc có thể gửi lesson proposal kèm bằng chứng khi có kết quả; không tự xuất bản kinh nghiệm vĩnh viễn. Bulk consolidation là job hữu hạn với input/output scope, provider policy, version và trạng thái cancel/fail rõ.

Summary phải giữ điều kiện, ngoại lệ, mâu thuẫn, độ thiếu bằng chứng và lineage. Không generalize từ vài dự án nhỏ thành nguyên tắc tuyệt đối. AI-written không đổi thành user-written khi owner lưu lại.

Implementation (S6-T1): scoped model job execution và provider controls (`owner job-create`, `job-start`, `job-cancel`, `job-status`, `job-list`, `job-ledger`, `provider-config`).
Scope snapshot revalidated trước khi đọc nguồn, gọi provider và commit proposal; kiểm tra chặt chẽ destination zone, source zone, và tính toàn vẹn trạng thái enrollment (`ValidateSourceEnrollment`: hỏng/không đọc được metadata fail-closed trung thực).
Toàn bộ lifecycle job gắn vault mã hóa (`job-*`), không second truth store.
Enforce pre-send caps với grounded full-request bound (khớp trực tiếp wire JSON serialization `OpenAiCompatibleProviderAdapter.BuildRequestJson`): 10,000 input tokens, 2,000 output tokens, $0.25/job, và $5.00 rolling 30 ngày (giữ $0.0054 historical reserve).

Tokenizer & context window facts từ primary source chính thức của DeepSeek-V4.1-Flash (không trích dẫn V3/R1 hay middle dot):
- Context window: 1M tokens (1,000,000) cho `deepseek-flash` theo DeepSeek pricing docs (`https://api-docs.deepseek.com/quick_start/pricing`).
- Tokenizer: `PreTrainedTokenizerFast` với ByteLevel BPE (`deepseek-v4.1-bpe`) từ `deepseek-ai/DeepSeek-V4.1-Flash` (`tokenizer.json`, `tokenizer_config.json`). Toàn bộ 256 giá trị byte (`0x00`..`0xFF`) đều có token đơn lẻ trong vocabulary 128,000 entries (xác minh 0 missing byte). Vì phép merge BPE chỉ gộp $\ge 2$ token thành 1 mà không bao giờ chia nhỏ hoặc sinh thêm token, mỗi byte UTF-8 tương ứng tối đa 1 token: `tokens(S) <= utf8_bytes(S)` là định lý toán học bảo toàn cho mọi chuỗi UTF-8.
- Chat template: Jinja2 chat template chính thức (`chat_template.jinja`) và reference implementation (`encoding/encoding.py`) dùng delimiters `<｜begin▁of▁sentence｜>` (ký tự phân cách `▁` U+2581 LOWER ONE EIGHTH BLOCK, không phải middle dot `·`), `<｜System｜>`, `<｜User｜>`, `<｜Assistant｜>`, `<think>` / `</think>`. Đối với model jobs trên DeepSeek, chế độ suy luận được chủ động vô hiệu hóa (`"thinking": { "type": "disabled" }`) qua wire request để tránh reasoning tokens làm cạn kiệt giới hạn 2,000 output tokens. Cận framing token bảo thủ 32 tokens bao trùm cả hai chế độ và được kiểm chứng bởi regression S6-T1 (26/26 checks pass).

Price card chính xác từ primary source (`deepseek:deepseek-flash` với Tokenizer `deepseek-v4.1-bpe`, peak rates $0.30/1M in, $1.20/1M out, $0.006/1M cache hit); unknown provider/pricing/context/tokenizer deny closed (`unknown_context` với tokenizer chưa được xác minh hoặc định danh cũ).
Model output boundary kiểm tra strict schema và grounding excerpt nguyên văn; output chỉ tạo `StoredProposal` mang attribution `SpeakerRole.Assistant` và `ClaimedUserConfirmed = false`, structurally không có shell, grant hay key authority. Không rò rỉ nội dung model ra log/failure reasons (loại bỏ tail diagnostics).
Phân định rõ ràng giữa proven-not-sent (từ chối trước khi gửi byte ra mạng: release reservation) và possibly-sent/ambiguous post-send (TCP drop sau accept, lỗi HTTP, bad JSON, missing usage: retain reservation bảo vệ ceiling $5/30d). Các lệnh gọi hoàn thành ghi nhận billable spend vào ledger; cap violation fail closed mà không xóa spend; true source-revoke và metadata enrollment hỏng giữa create và start fail closed và release reservation; mid-flight revocation hoặc metadata hỏng trước commit chặn ghi proposal, bảo tồn nguồn và commit billable spend; in-flight cancel, timeout và restart recovery dừng commit và retain unknown reservation; credentials lưu encrypted trong vault không nhận qua argv.

Implementation (S6-T2): cấu trúc hóa coding-session qua profile `coding-session` trên cùng hạ tầng model jobs bounded của S6-T1 (`JobScopeSnapshot.Profile = "coding-session"`). Output chuyển hóa thành các proposal rời rạc thuộc các kind `problem`, `attempt`, `result`, `decision`, `lesson` trong inbox đích, không auto-promoted lên canonical items (giữ nguyên attribution `SpeakerRole.Assistant`, `ExecutorKind.StructuringJob`, `ClaimedUserConfirmed = false`):
- Phân biệt minh thị giữa tuyên bố của trợ lý (`assistant diagnosis`/`decision`/`lesson`/`claim`), phản ánh lỗi của user (`user bug report`) và kết quả công cụ quan sát được (`observed tool result`): trợ lý nhận đã triển khai không làm biến kết quả test cục bộ thành kết quả triển khai production (`unverified_inference` fail-closed lexical heuristic chặn các cụm từ triển khai phổ biến; tính đúng đắn ngữ nghĩa toàn diện do owner đối chiếu và quyết định). Tất cả các trường hiển thị (title, body) bảo lưu đúng tư cách người nói/nguồn quan sát, không coi khẳng định bằng lời của trợ lý là kiểm chứng thực nghiệm hay quan hệ nhân quả.
- Phân định thời điểm cam kết (`temporal commit attribution`): các sự kiện trước khi có commit sửa lỗi (`problem`, lần thử đầu, kết quả lỗi, chẩn đoán ban đầu, quyết định kiến trúc trước fix) giữ `version: null`, không kế thừa commit fix tạo ra ở giữa phiên (`9b2d4f1a7e`); các sự kiện sau commit (`attempt` lần 2, test pass, `lesson`) gắn đúng commit tương ứng; production target giữ `null`.
- Nguyên tắc bảo toàn điều kiện áp dụng (`MemoryApplicability`): giữ đúng project, environment, commit hash và version khi được nêu rõ trong transcript; các trường chưa biết giữ `null`, không đoán mò hoặc bịa version/commit (`applicability_ungrounded` fail closed).
- Mỗi proposition quan trọng có trích dẫn nguyên văn đầy đủ (`excerpt`) gắn liền với source span chính xác theo byte UTF-8 trong văn bản gốc (`SourceSpan(Start, End)` trong `MemorySourceReference`), bao quát toàn bộ các khẳng định con (như tham số retry, jitter, ngưỡng throughput, giới hạn multi-region), bảo đảm 100% provenance truy nguyên.
- Hỗ trợ đọc nguồn kép (`dual source reading`) và vòng đời snapshot: `ModelJobService` hỗ trợ cả ghi chú nghiên cứu đã đăng ký (`RegisteredSource`) và bản chụp thế hệ nguồn được lưu giữ (`SourceSnapshotIds`, `mb1_snp_...`). Khi commit proposal, citation reference được tự động giải quyết (rewrite) về ID và revision của resource gốc canonical; byte span bảo đảm đồng nhất byte-for-byte với cả bản chụp và văn bản nguồn gốc; đồng thời `ValidateSourceEnrollment` kiểm tra cả snapshot lẫn canonical source ID để từ chối truy cập (fail closed) khi nguồn bị thu hồi (`revoke`) hoặc cách ly (`quarantine`).
- Hostile directive injection (lệnh chiếm quyền, xóa ledger, mở khóa vault) nằm trong transcript được xử lý hoàn toàn trơ (`inert`), không có bất kỳ quyền hạn thực thi nào.

Implementation (S6-T3): profiles `research-note` (default) and `trend` use the existing bounded model-job lifecycle and the one selected, revision-pinned source text. Both write only pending assistant-authored proposals to the existing encrypted destination inbox; no additional source store, crawler, publication path, or owner-confirmation authority is introduced.

- Research structuring separates reported claims, source-stated limitations, observations, user hypotheses, questions, and explicitly stated intention proposals. A paper claim or limitation remains attributed to its cited source rather than a project result; questions remain questions; contradictory voices stay distinct and unknown speakers remain unknown.
- Trend jobs receive only the selected source revision, not the vault, Internet, or another source. The prompt asks the model to preserve stated scope, time window, uncertainty, contradictions, counterexamples, and gaps, and to treat repeated citations, copies, or mentions of one origin as one source rather than independent confirmations. Distinct origins require the note itself to identify them; if an item's excerpt does not show whether origins are same or different, that item must omit an independence comparison. URLs/citations remain locators, not evidence that linked work was read. These remain semantic instructions, not validator guarantees.
- Research and trend prompts make each item's excerpt its evidence boundary: title/body claims, including comparisons, contradictions, scope and absence statements, must be supported by that single continuous exact excerpt, not another source line or item. Multi-line support includes all intervening text; otherwise the model is asked to split or omit unsupported details. This remains prompt guidance: exact-substring validation does not establish semantic support.
- Each proposal retains the existing source ID/revision and exact verbatim excerpt with its UTF-8 source span. A complete timestamp with an explicit UTC offset or `Z` is accepted as a typed occurrence time only when that exact timestamp appears in the excerpt; it is normalized to UTC. Date-only, relative, ambiguous, timezone-unknown, or future planned times remain unset in `OccurredAtUtc`, while their original wording remains in the cited source. `question` maps to `MemoryKind.Question` with unverified evidence; explicit `intention` maps to `MemoryKind.Intention` and remains a proposal.
- Profile prompts guide voice attribution, grounding, uncertainty, and duplicate-origin handling; they are not semantic validators. No lexical check guarantees that a model interpreted meaning correctly. The owner reviews the real source beside the pending output before any publication decision.

Implementation (S6-T4): explicit bounded multi-level synthesis runs as profile `synthesis` on the same model-job lifecycle, and an owner selects the whole scope: the explicit input set (each pinned to one revision), the objective to address, and the requested level (`l2` or `l3+`). Nothing is discovered, expanded, or scheduled.

- A job scope now records an input list instead of one source (`JobScopeSnapshot.Inputs`). Revalidation runs over every selected input before the private read, before the provider send, and before the proposal commit, so one input that moved, was removed, or was revoked blocks the next step and releases the pre-send reservation. Stored single-source job records are migrated on read into the current scope shape instead of being stranded.
- Synthesis requires at least two distinct selected inputs, an owner-stated objective, and an owner-requested level; a repeated input is refused because reading one resource twice is not a second source. An `l3+` request additionally requires at least one selected input that is itself an `l2`/`l3+` synthesis item, so a higher level is never invented directly from raw sources.
- Items are committed as pending proposals with `MemoryKind.Synthesis`, `CreationMethod.Synthesis`, the owner's level, `SpeakerRole.Assistant` authorship, and reported-source or unverified evidence. Neither the requested level nor the number of cited inputs upgrades evidence, and no synthesis output is automatically a user conclusion.
- Each item carries an item-local grounding set: every entry names one selected input at its pinned revision and copies a verbatim passage from that input's text (a registered source's own text, or a memory item's body). An item declared to span inputs must ground in at least two distinct inputs. A synthesis item carries no source occurrence time, and applicability values must appear in the item's own excerpts. The validator checks verbatim presence within the named input, not semantic support.
- Committed lineage cites each distinct grounded input once, at the revision the job read, with the enclosing span of the passages copied from a registered source's own text. A citation naming a memory item pins that item's revision without a span, because a span is only meaningful against a source's text; the item's own citations continue the trace to its permitted roots. A selected input set whose pinned citation lineage revisits the same resource is refused (`lineage_cycle`) instead of propagating an inconsistent derivation; the scope check and the review below walk that lineage with one shared bounded policy, so both read the same edges under the same node/depth limits.
- The owner review surface serves a computed citation review beside each pending proposal: a cited source that moved, disappeared, became unavailable, or was revoked is reported as needing review, naming the cited resource, and the evaluation continues through a cited memory item's own pinned citations — a root that changed or was revoked underneath an unchanged cited item is served as `citedSourceLineageChanged`, and a cyclic or unbounded lineage is reported instead of being read as current, so a stale derivation is never presented as current. The proposal's own content, revision, level, and citations are never rewritten and no citation is silently re-pinned, and no owner conclusion is changed. The review pane serves the first cited input it can read beside the content: a registered source's exact revision text, or a cited memory item's exact body at the revision the citation pins, through the same owner detail read the library uses. A revision the service does not serve is reported as a refusal — another revision or another resource is never substituted — and every citation stays listed with its pinned revision, while locking or a lost unlocked epoch drops the served evidence with the rest of the private view. The computed review is served on the owner review surface only; an agent proposal reply carries none.
- Restrictions kept: synthesis output stays a pending proposal in the owner-approved destination inbox (cross-zone publication remains a separate owner act), synthesis access remains restrictive by default, and the job never reads an input the owner did not select. Prompt guidance asks for verbatim, input-qualified grounding and explicit conditions, exceptions, unknowns, and conflicts; real-model evidence shows the model can still mis-copy or mis-attribute an excerpt when one input restates another, which the validator refuses rather than repairs. The failed provider output is not retained, so a refusal reports the validation code, not the rejected text.


Implementation (S6-T5): owner declassification publishes an owner-reviewed sanitized derivative into an explicit destination zone through the owner channel — never an ACL copy or a tag change. The default synthesis access stays restrictive across sources; only the owner act opens a cross-zone read.

- The owner previews the entire outbound body, title, metadata and citation set beside the private pins it would stand on, then publishes explicitly with a typed PUBLISH confirmation for exactly that content. The shared copy is an ordinary revision-gated canonical memory item in the destination zone with EMPTY citations; the private lineage is retained only as encrypted accepted `derived_from` relations (one per pinned private source), whose endpoints never sit inside a shared-only grant scope, so raw private sources, backlinks, guessed identifiers and forged owner intent all deny alike.
- Two entry paths share the write: explicit pins (any owner-reviewed private items and/or sources) and a pending create proposal (its cited sources become the pins). Both revalidate every pin at its live revision before the commit, refuse relabeled attribution and non-empty shared citations, and resume an identical retry instead of duplicating; any other content, a moved pin or an occupied destination fails closed.
- Catalog publication stays a separate owner act that never opens a body, and a new shared item or revision never enters an already-frozen grant snapshot. A moved private root marks the shared derivative with a generic `derivative_source_changed` warning — no private identifier crosses — while the owner's sanitized conclusion and the recorded lineage are preserved unchanged.

Implementation (S6-T6): owner job/review workflow và automation opt-in dùng đúng owner control pipe, đúng model-job lifecycle của S6-T1 và đúng review surface của S5 — không second store, không authority mới, không frontend policy.

- View **Jobs** của `MetaBrain.OwnerUi` chạy một note hoặc một batch và quản lý lịch qua `OwnerChannelClient`. Một job đơn là đúng selection owner gõ (`resourceId@revision` mỗi dòng, objective + level cho synthesis); một batch là một bounded job riêng cho MỖI note với state riêng, nên note này fail không che note khác. Cả hai đi qua cùng `owner.job.create`/`owner.job.start`, cùng scope revalidation và cùng proposal sink; UI không tự tạo policy, không tự quyết định due, không tự cancel/publish.
- Model/egress settings đọc lại được qua `owner.provider.status` (provider/model/endpoint + các cap đóng băng) mà KHÔNG bao giờ trả API key; `owner.provider.config` vẫn là nơi duy nhất ghi credential, và key chỉ đi từ `PasswordBox` tới service trong đúng một request.
- Job list/detail/cancel và spend ledger là read/act thật trên encrypted job record: state, scope đã pin, usage/cost provider báo, failure code, proposal IDs. Cancel một job Pending giải phóng pre-send reservation và không gửi gì; một job đã xong được trả nguyên trạng, và không có đường nào undo nội dung đã publish.
- Lịch tổng hợp opt-in là MỘT record encrypted (`sec-synthesis-schedule`, zone `core`) ghi qua `owner.schedule.set` và đọc qua `owner.schedule.status`; `owner.schedule.disable` giữ cấu hình nhưng đặt `enabled=false` và bỏ `nextDueAtUtc`, nên lịch đã tắt không bao giờ tới hạn. Record giữ đúng selection (mỗi input pin một revision), objective, level, destination và provider/model của owner — không tự thêm agenda, không tự expand input.
- Vòng lặp nền `SynthesisScheduleBackgroundService` gọi CHÍNH operation `owner.schedule.run` (cùng policy, cùng handler) mỗi 5 giây: chỉ chạy khi vault unlocked và đã tới hạn, tối đa một bounded synthesis job mỗi lần, và cửa sổ kế tiếp luôn được tính từ lúc lần chạy đó KẾT THÚC (`nextDue = finished + interval`). Slot bị lỡ khi locked, khi service down, hay khi lần chạy trước còn đang chạy không bao giờ được replay thành burst; lock không tự unlock, và lần chạy bị lock cắt thì record vẫn giữ nguyên due để đúng một lần chạy xảy ra sau unlock.
- Output của job (kể cả job theo lịch) vẫn chỉ là pending proposal: không auto-publish, không auto-resolve, không đổi belief, và publication vẫn là act owner tường minh qua review S5 giữ nguyên provenance (author/creationMethod/level/citations).
- Giới hạn: provider send của lịch chạy không qua pipe request nên không bị guard 10 giây của pipe; job start do owner gọi vẫn chịu bound đó (S6-T1). Bằng chứng egress là log request phía endpoint provider được cấu hình, không phải OS-level packet capture, và stub cục bộ tự khai token usage của nó trong khi leg live dùng usage thật của provider.

## 11. Retrieval và MCP

Retrieval ưu tiên lexical/full-text + metadata, thời gian, trạng thái và phạm vi áp dụng. Bổ sung embedding local khi bằng chứng dùng thật cho thấy cần, đặc biệt dữ liệu Việt–Anh; không buộc dependency vector DB nặng từ đầu. Search cần đánh giá truy vấn paraphrase và tên symbol, không chỉ exact match.

Kết quả gồm nội dung, provenance được phép, trạng thái, phạm vi, thời gian và cảnh báo cũ/mâu thuẫn. Brief là bản chiếu ngắn, có thể dựng lại; không là truth store thứ hai hoặc system instruction.

Agent discovery bắt đầu từ catalog owner-approved và access request, không private index toàn kho. Catalog query chỉ dùng metadata đã công bố; memory search/get/brief chỉ sau token redemption và trong IDs/revisions được duyệt. Catalog description được phép công bố không khiến body hoặc citation private trở thành công khai.

Tool surface theo capability, không chốt tên API trước hợp đồng Sprint 1:

- Context/brief; search; get item và source expansion có quyền riêng.
- Propose create/revise; explicit link/unlink/supersede trong grant.
- Feedback hữu ích/sai/cũ; không lấy read count làm độ đúng.
- Intention view và thao tác trạng thái trong quyền.

Quản trị grant, declassification và purge nằm trên owner channel. Trả lỗi không tiết lộ endpoint ngoài quyền. Phiên hết hạn/thu hồi không dùng lại cache, handle hoặc bridge để tiếp tục đọc.

## 12. Thời gian, dự định và quên

    Thời gian tách occurred_at, recorded_at, valid_from/valid_until và review_after; không gán thời điểm import cho thời điểm xảy ra. Giữ độ chính xác/unknown và múi giờ phù hợp; không bịa ngày chính xác từ “quý tới”.

    Implementation (S7-T1): current/as-of/history query và UI timeline chạy trên validity/revision đã có, không đổi temporal schema meaning. Current loại withdrawn/rejected, hết validity và superseded (intention superseded hoặc `supersedes` proposed/accepted); history giữ tất cả kèm lý do gốc. As-of replay history chỉ tới instant đó (UTC, không future) nên current phản ánh đúng điều đã recorded lúc đó. Window coarse không zone không bound gì; unknown không bịa ngày; bounds inclusive; contradicts không newest-wins.

Intention có pending/done/cancelled/superseded, trigger theo thời gian hoặc ngữ cảnh user chọn. Lịch chạy không cần LLM. Không có agent kết nối thì due item vẫn tồn tại. Đã nhắc không phải đã hoàn thành; lịch đã tắt không tiếp tục âm thầm nhắc. Việc hoàn thành cần thao tác/bằng chứng theo policy, không suy từ câu trả lời AI.

Implementation (S7-T2): one-time triggers require an exact ISO-8601 date-time with `Z` or an explicit numeric offset (including minutes); date-only values and timezone-free local timestamps (including repeated DST hours) are rejected rather than guessed, while an explicit offset selects one instant even during a repeated wall-clock hour. Named zones and recurring rules are unsupported. Owner set/edit/cancel acts and the due instant live in the same encrypted, revision-gated lifecycle record; v1 records without triggers and v2 records with time triggers decode in memory as current schema v3, preserving validity and any existing due instant. A no-LLM service pass runs only under an unlocked counted vault operation, writes one notification marker per due pending intention, and resumes overdue markers after explicit unlock. The owner **Intentions** view shows pending due/scheduled state and supports acknowledgement, separate rationale-bearing completion/cancellation, and trigger cancellation; notifications and acknowledgements never mark an intention done. Locked state pauses schedule queries and private view state, does not expose due metadata, and never unlocks for a deadline. The clock is UTC system time: forward or backward corrections can make an unnotified deadline appear due earlier or later; a missed deadline remains due on the next unlocked pass.
Implementation (S7-T3): an owner enables, edits and disables one project/task context trigger per pending intention in that same encrypted lifecycle record (schema v3). Labels are explicit, trimmed owner values; every configured label must match the caller-supplied brief context exactly apart from case, and no project/task context is inferred. The scoped `owner agent-brief` and `metabrain_memory_brief` response includes only enabled, unacknowledged pending intentions whose exact current item ID/revision is already in that session's frozen `memory.context` grant. The bounded intention excerpts share the brief character budget and carry the same stored authorship, evidence, applicability and citation signals as other brief entries. A new intention, collection member or item revision never expands an existing grant; a moved item revision does not produce a contextual reminder until the owner approves a fresh scope. Reading or surfacing a reminder does not write lifecycle state, acknowledgement remains separate from completion, and only an explicit owner completion with rationale marks the intention done. The owner **Intentions** timeline displays the configured context and has explicit enable/disable, acknowledgement and completion controls. Contextual reminders are brief results, not a separate notification channel; locked/revoked sessions return no private trigger metadata or content and never unlock the vault. The owner's recall policy applies to this projection exactly as it applies to the generic brief: an intention the owner set aside is not projected and is named in the omission trace (`owner_archived`), it returns only under an explicit expansion (`includeArchived`), and a restore returns the default reminder; the policy never adds a member, never widens a grant and never names an out-of-scope intention. The contextual reminder set keeps its deterministic ID order, so a demoted intention is still surfaced but is not re-ranked inside that set (the tier ordering belongs to the generic brief, the scoped search and the owner library).

Quên gồm:

1. Giảm ưu tiên trong recall/brief.
2. Archive vẫn tìm lịch sử được.
3. Purge theo owner/policy đã bật, bao phủ canonical, index/cache/snapshot và xử lý lineage liên quan.

Không tự xóa chỉ vì ít truy cập; bài học hiếm có thể rất quan trọng. Tần suất đọc chỉ là tiện ích, không phải độ tin cậy. Pin, restore archive và lý do xếp hạng phải giải thích được. Quyền xóa dữ liệu nguồn ngoài vault là riêng, không mặc định xóa session runtime.

Implementation (S7-T4): quên ở mức 1 và 2 chạy bằng recall policy của owner, lưu trong một encrypted owner state riêng (`recall-policy.enc`) cạnh scope-grant/catalog/source-enrollment, không ghi vào canonical content nên không đổi revision, evidence, verification, citation hay attribution của item và không làm index cũ. Mỗi thay đổi là một owner act tường minh có rationale, actor và UTC time: `owner recall-apply --resource-id --act {demote|pin|archive|restore} [--reason]`, `owner recall-read --resource-id`; demote và archive bắt buộc có lý do, và item đang pin bị từ chối demote/archive cho tới khi owner restore (bảo vệ bài học hiếm). Không ghi nhận read count: đọc nhiều lần không nâng truth, không nâng ưu tiên, không tự hạ ưu tiên. Policy chỉ áp cho subject mà caller đã có trong scope: archived không được mặc định trong scoped search, scoped brief và library của owner, và trở lại qua mở rộng tường minh (`includeArchived` trên search/brief/library) hoặc qua history; demoted vẫn được tìm thấy nhưng xếp dưới item khác; pinned xếp cùng nhóm được bảo vệ. Trong brief, item bị set aside không được project nhưng vẫn được nêu tên trong omission trace (`owner_archived`) để không biến mất âm thầm. Mỗi hit trả kèm disposition của owner và lý do xếp hạng chỉ nêu đúng sự thật đã dùng để xếp, còn rationale của owner chỉ nằm trên owner channel. Restore chỉ trả lại visibility, không mở rộng quyền. Implementation (S7-T5): purge ở mức 3 là owner operation có dry-run, confirm gõ tay và durable bookkeeping: `owner purge-preview --resource-id --resource-revision --reason` bind live revision và tính closed impacted set (target cộng mọi canonical derivative đặt tên nó), `owner purge-confirm --purge-id --resource-id --resource-revision --target-zone --target-kind --impacted id|revision|kind|reason,... --live-generation N [--catalog-ids ...] --reason` xóa đúng set đã bind sau khi owner gõ `PURGE`, `owner purge-resume --purge-id` tiếp tục intent đã confirm nhưng chưa xong. Changed target, mistyped confirm, agent purge và resurrected ID fail closed; private index bị clear tới khi owner rebuild; ledger encrypted (`purge-ledger.enc`) không giữ removed payload; original ngoài vault không bị xóa mặc định; shared derivative ở lại; backup giữ ciphertext tới retention riêng và restore phải re-apply ledger; agent không owner purge.

Implementation (S7-T6): review-after là một *đánh giá* read-time của service trên canonical state sống, không phải một trạng thái tự viết và không phải một phán quyết về niềm tin của owner. Hàng đợi review (`owner review-list` trên owner channel) báo đúng các item có deadline `review_after` đã qua (chỉ khi stamp là exact), có nguồn được trích dẫn đã đi qua revision mà item trích dẫn (stale), có nguồn không còn trong vault (missing), hoặc có nguồn tự khai là unavailable; deadline chỉ biết ở độ chính xác thô (day/month/quarter/year) hoặc unknown được báo `undetermined` chứ không bị thu hẹp thành một ngày giả, và availability chưa ai khẳng định vẫn là unknown chứ không phải available. Mỗi entry mang exact source ID, cited revision, live revision, availability và stamp `review_after` nguyên precision/zone. Owner review (`owner review-record --resource-id --reason`) chỉ ghi vào một encrypted owner state riêng (`review-state.enc` cạnh scope-grant/catalog/source-enrollment/recall-policy): item giữ nguyên body, evidence, citation, revision, publication/intention state; không tự phủ định belief, không tự hoàn thành intention, không đụng grant/collection/session. Một reason chỉ được coi là đã trả lời khi trạng thái mà owner đã nhìn vẫn đúng: revision sống của nguồn được trích dẫn và stamp `review_after` không đổi; nguồn bị revise lại hoặc deadline được đặt lại thì reason mở lại. Purge vẫn là chống hồi sinh: ID đã purge được ledger giữ reserved nên `source.enroll`/`source.ingest`/`source.snapshot.*` cùng mọi write khác từ chối với `resource_purged`, index rebuild không phục hồi item đã xóa, và enrollment của nguồn bị purge bị revoke trong chính lần purge đó (file gốc ngoài vault không bị đụng, giữ nguyên contract của S7-T5/S8).

## 13. Owner application

UI tối thiểu về phạm vi sản phẩm, không phải MVP:

    - Inbox: capture văn bản, nguồn mới, proposals và trạng thái job.
    - Library: duyệt theo zone, project/topic, kind, thời gian, level và tìm kiếm; current/history/as-of, decision history và retained revision của item đang mở.
    - Detail/review: đọc nguồn cạnh nội dung, revision diff, sửa, publish/reject, relations, contradiction và undo phù hợp.
    - Timeline/intentions: lịch sử quyết định, việc đang chờ, due và review-after.
- Access: catalog publication, agent access requests/mục đích, preview/chọn IDs/revisions/operations/provider scope, token handoff, expiry/revoke và “agent này thấy gì”; không protected-runtime launcher.
- Settings/operations: unlock/lock, key/recovery và mất key, nguồn allowlist, model/egress, automation opt-in, retention, encrypted backup/restore, explicit export và health.

Hai câu hỏi phải trả lời được từ UI: “Vì sao hệ thống nhớ điều này?” và “Ai có thể đọc điều này?”. UI không phải feed gây xao nhãng; hỗ trợ thao tác nhanh, keyboard và nội dung Việt–Anh. Security preview dùng đúng policy engine, không approximation ở frontend.

Implementation (S5-T1): owner console WPF `MetaBrain.OwnerUi` mở một owner session tường minh tới service đang chạy qua owner control pipe với client `MetaBrain.Connections.OwnerChannelClient`, và thể hiện trạng thái thật service trả: reachable cùng vault `uninitialized`/`locked`/`locking`/`unlocked`, `service unavailable` (transport_unavailable/denied/timeout), `owner authority denied` (forbidden/unauthorized) và protocol mismatch, mỗi trạng thái kèm code thật. Navigation chỉ gồm view đã triển khai (owner session, vault status); attach/check/end session là hành động owner tường minh. Ranh giới giữ nguyên: không key vault, không đọc/ghi vault file, không fetch/cache private content, không listening socket, không suy body/Origin/localhost/agent claim thành owner authority, và unknown/failed answer không bao giờ hiển thị như unlocked. Unlock/lock, library, capture, review, relations, catalog và session console thuộc S5-T2–S5-T7; same-account OS compromise vẫn ngoài cam kết.

Implementation (S5-T2): view **Vault & key** dùng chính owner control pipe đó để provision/unlock/lock/recovery qua các owner vault command của `OwnerChannelClient`; credential owner chỉ đi từ `PasswordBox` của owner tới service trong đúng một request, console không giữ key và không ghi secret vào log/file/screenshot. Provisioning hiển thị recovery key đúng một lần (element luôn hiện diện và bị **thay text** thành placeholder khi clear — không chỉ ẩn — nên console không còn bản hiển thị nào sau lock/hide/disconnect/end-session; đổi credential trả về `Refused` cũng clear). Refused (`unlock_denied`, `credential_required`, `vault_already_initialized`) được phân biệt với `unavailable`/`forbidden`/protocol mismatch, nên wrong/missing/unusable key hiện là lời từ chối của service và vault vẫn locked; không có reset/backup giả và không autonomous unlock. Lock đi qua đúng `vault.lock` của core: drain in-flight private work, clear key, vô hiệu pending tokens/live sessions, dừng private reads/ingestion/jobs (đã kiểm chứng qua agent-read/authorize, redeem và index-rebuild); restart vẫn locked. Recovery dùng chính recovery key owner tự giữ và mở lại **cùng** vault, không restore/reset dữ liệu. Giới hạn nêu rõ trong UI: erasure của managed .NET/WPF string không được bảo đảm, và same-account OS compromise vẫn ngoài cam kết. Library, capture, review, relations, catalog và session console thuộc S5-T3–S5-T7.

Implementation (S5-T3): view **Library** tìm/browse/filter và đọc evidence detail qua chính owner control pipe đó (`owner.library.search` trên `OwnerChannelClient`, không có operation agent nào). Service — không phải console — áp mọi filter (text Việt–Anh, zone, collection của owner, kind, abstraction level, cửa sổ thời gian UTC inclusive, current/history) và tự rank page: console chỉ render đúng ID/revision/zone/kind/level/title/snippet/score/occurred-recorded/publication service trả, nên không có ranking hay policy thứ hai ở frontend. Ranking/scoring/time/currentness/staleness tái dùng đúng implementation của scoped search trong `ScopedSearchAuthority` (một scope provider mới là chính vault của owner, không grant snapshot); mỗi hit vẫn được đọc ở đúng revision của nó qua operation revision-gated nên row đã bị move sẽ fail closed chứ không phục vụ bytes khác. Collection/zone chỉ là selector: filter chỉ thu hẹp, không cấp quyền cho ai. Generation index đã lệch canonical bị từ chối bằng `search_index_stale` (fail closed như scoped search) và owner rebuild index bằng act tường minh. Detail trả evidence/author/executor/creation method/level/body/applicability + citation với status current/superseded/unknown và warning của service; mở citation đọc đúng registry revision được nêu (revision đã move fail closed, revision live được phục vụ), nguồn thiếu vẫn hiện unknown/unavailable chứ không thành verified. Sửa title dùng đúng `memory.revise` revision-gated: cùng ID, body/citation/times/applicability/attribution/evidence giữ nguyên ở một revision mới, và evidence của author không thể được nâng cấp khi save (R12). Lock (hoặc mất service) xoá page/item/source/query text của console và mọi answer đang in-flight bị bỏ theo private epoch, nên không có view private nào được repopulate; console không ghi cache ra đĩa và không giữ key.

Implementation (S5-T4): view **Inbox** lưu text+zone (+metadata tùy chọn) thành registered source encrypted qua đúng `research.capture` — không provider, không fetch URL, không classify/summarize; text không encode được UTF-8 và capture bị service từ chối đều giữ nguyên text trong editor kèm giải thích, và read-back đọc đúng text vault giữ ở revision đã ghi nên note sống qua restart/provider lỗi (R19). Cùng view đó list/open pending proposals (`proposal.list`/`proposal.read`) với source mà citation chỉ định được đọc cạnh nội dung ở đúng revision citation pin; `owner.proposal.publish` ghi nội dung owner review qua đúng đường revision-gated (`memory.create` tại ID `mi-` dẫn xuất từ proposal, hoặc `memory.revise` đúng revision proposal pin nên item đã bị sửa đồng thời fail closed, không overwrite) rồi ghi quyết định bằng publication state của lifecycle record item; `owner.proposal.reject` ghi quyết định kèm rationale mà không apply proposal (revise proposal không đổi revision/content/publication của item bị pin) và rejected/withdrawn không còn current nhưng vẫn đọc được ở history. Author/creationMethod/evidence của item ghi ra lấy từ chính stored proposal nên approve không bao giờ relabel AI-written/tool-observed thành user-written/user-confirmed (R12); retry sau crash chỉ resume khi stored content đúng bằng reviewed content còn lại thì fail closed và không overwrite; quyết định mà publication state không diễn đạt được bị từ chối trước khi ghi content. Undo dùng đúng `lifecycle.withdraw`/`lifecycle.repropose` trên cùng canonical record với rationale trong history, không tạo bản sao và không relabel; revision-history undo không được claim (archive read thuộc sprint sau). Lock cảnh báo một lần khi editor còn text chưa lưu rồi xoá editor/inbox/source theo private epoch; console không ghi cache hay draft ra đĩa và không giữ key.

Implementation (S5-T5): view **Relations** đọc/ghi quan hệ qua chính owner control pipe. `owner.relation.list` (owner-plane read mới) trả toàn bộ relation của vault đang mở kèm revision sống, provenance và review state service tự tính; `Inspect selected relation` mở đúng revision đã serve với hai endpoint đã pin, lý do, act đổi state cuối và request evidence của caller. Owner chọn hai endpoint tường minh: memory item từ chính library index generation service đã materialize (search bị từ chối `index_not_materialized`/`search_index_stale` cho tới khi owner rebuild — không console nào tự scan), hoặc registered source bằng ID + registry revision sống; owner chọn kind tường minh (`related_to` là association, `supports` là evidence link — console không bao giờ biến similarity thành support), viết reason mà shared validator đòi, và chọn record là kết luận accepted của chính owner hay proposal. Link do agent tạo qua MCP xuất hiện trong cùng set với `executor=agent`, lời caller báo là claim (không phải owner confirmation), state `proposed`; `Confirm link (owner)` ghi acceptance với `stateExecutor=owner` và giữ nguyên lời agent. Evidence endpoint đổi revision được báo **NEEDS REVIEW** kèm reason (`endpointRevisionChanged`) và endpoint service nêu tên, còn association vẫn `current` — related_to không pin revision nên không bị gán cờ; cycle derivation bị từ chối bằng code riêng `relation_cycle` và cùng hai endpoint vẫn được nhận là `related_to`; `Remove link` ghi rationale của owner và `Undo removal` đưa về `proposed` (không tự nhận accepted lại). Console không giữ link store, không tạo visibility policy và không ghi vault: endpoint authority, revision pin và thứ mà agent session thấy do shared core + grant của session quyết định, và lock xóa list, link đang mở, candidate cùng draft của owner.

Implementation (S5-T6): view **Sharing** publish/withdraw catalog metadata, review pending request/mục đích, owner preview/narrow/approve/reject, token local handoff và effective scope/expiry/revoke qua đúng S1 shared APIs trên chính owner control pipe đó — không autofill private title/path/citations vào public metadata, không duplicate authority trong UI. `Preview publication` hiện đúng label/description owner gõ với live mapped revision, mutates nothing và mints nothing; `Publish this exact metadata` lưu đúng triplet đó nên discovery chỉ phục vụ approved ID/label/description, và `Withdraw selected entry` gỡ khỏi discovery tương lai. `Read access requests` list pending request với purpose/catalogs (request không permission); `Preview selected request` bind frozen snapshot với exact resources/revisions/bodies/disclosure cho mắt owner; `Approve this narrowed scope` chỉ mint narrowed subset của IDs/operations đã bound (read-only default, service từ chối mọi growth) với explicit UTC expiry vào DPAPI handoff file mà console chỉ hiện path; `Reject request` ghi terminal refusal không grant. `Read live sessions` list effective frozen scope/expiry/egress của từng session; added members/revisions không tự vào scope; stale preview/used token/unapproved member-revision-raw-provider refusals fail closed; `Revoke selected session`/lock/restart deny reads tiếp theo. Lock/end-session/disconnect clear mọi private view/pending action/inflight response theo private epoch; console không ghi plaintext cache và không đưa key/token vào chat/log/args/screenshot.

Implementation (S5-T7): view **Sources** quản lý allowlist, mapping review, ingestion health, preservation và collections qua đúng shared core/owner APIs trên chính owner control pipe đó — không config edits, không model settings, không duplicate authority trong UI. `source.mapping.submit`/`source.mapping.resolve` (owner operations mới) giữ nguồn chưa rõ zone/format mapping trong private quarantine mà không đọc transcript và không đoán zone mặc định (chỉ explicit enrollment khi đã rõ hoặc withdraw mới xóa); enrollment vẫn đòi explicit mapping và conflicting mapping bị từ chối, không merge. Allowlist hiện life/checkpoint/quarantine/availability/refresh consent/cache scope mà không echo locator; unknown/unmapped bị từ chối state-first, unknown format giữ unsupported refusal (không bao giờ thành completed save), missing source có relocate/revoke remediation, replaced/truncated bị quarantine với checkpoint frozen. Preservation là owner path thật: console đăng ký preservation reference của source đã chọn qua registry create hiện có (locator đã enroll được owner nêu tường minh, revision 1), rồi preserve từ revision đó, list snapshots của source đã chọn, mở một row đã list qua live read có kiểm tra revision, và extend retention từ live revision (stale revision bị từ chối trung thực); collections organize mà không grant; pause/resume và refresh opt-in/out theo shared coordinator; lock/end/disconnect xóa mọi sources view/draft/detail.

Implementation (S11-T5): **Sources** adds the owner-selected folder picker, explicit recursion/include/exclude preview and one typed enrollment confirmation over the existing service owner channel. Enrollment confirmation does not enable folder refresh or raw-byte retention; each requires its own explicit owner act and policy boundary. The view groups current members by the service's folder ID, reports folder refresh plus per-source pause/revoke/refresh vetoes, and displays the service's exact availability, saved/preserved bytes and canonical-event counts, interpretation, derivation/indexing, diagnostic code/offset and retention metadata without transcript content. Exact preserved-span readback remains the owner CLI's digest-checked export to a new file. Lock, disconnect and session end clear folder drafts, previews, member state and late answers. This unreleased surface does not change owner authority, add a vault store/key or claim MCP behavior.

State-action replies without a complete ingestion report mark absent facets `not reported` rather than zero; a same-source/generation full status remains visible while the latest membership, pause and veto fields are applied.

Implementation (S7-T6): view **Review queue** đọc `owner.review.list` qua owner control pipe và hiện đúng lý do do service tính (due, stale, missing, unavailable, undetermined), exact source ID + cited revision + live revision + availability, và stamp `review_after` nguyên precision/zone; console không tự quyết item là sai hay cũ. Nút **Record my review** ghi rationale của owner vào private review state qua `owner.review.record` và không sửa item; lock xóa list, detail và input như các view private khác.

Implementation (S8-T4): view **Operations & audit** đọc `owner.operations.health`/`owner.operations.audit`/`owner.operations.diagnostics` và chạy `owner.operations.retention` qua đúng owner control pipe — owner-only, cần vault unlocked, agent channel từ chối `forbidden`. Health báo trạng thái vault, private index, provider có cấu hình hay không, rolling spend, số audit entry/diagnostic, retention hiện hành, kích thước safe operational log và error code cuối cùng. Audit ghi **actor / action / outcome / cost / error code thật** (owner hay agent; operation token; completed hay refused; chi phí service đã trừ; code lỗi service trả về) và không mang title, body, query, locator, credential hay token. Diagnostics lưu **code lỗi thật** cùng câu hướng dẫn owner-facing nói phải làm gì tiếp. Audit + diagnostics nằm trong **một file encrypted riêng của vault** (`operations-audit.enc`, cùng data key và envelope AEAD như scope-grant/catalog/recall-policy/review-state/purge-ledger): không phải canonical record, không vào private index, không được publish — approved catalog vẫn là disclosure công khai duy nhất. **Safe operational log** (`logs/operations.log`) là file plaintext có chủ đích, chỉ ghi operation + outcome + error code qua sanitizer (mọi giá trị không phải token lowercase ngắn thành `redacted`), nên không credential/token/path/body nào lọt; refusal xảy ra **khi vault đang locked** chỉ có mặt ở log này — private state chỉ tồn tại trong vault đang mở, sản phẩm không giữ key thứ hai, không auto-unlock và không giữ queue plaintext để ghi sau. Retention của owner áp cho audit + diagnostics và báo đúng số entry bị gỡ; **request retention bị từ chối thì không gỡ gì cả** (validate toàn bộ request trước khi xóa, nên log và trail giữ nguyên) và private state được ghi trước khi log bị xóa. **Purge một record cũng gỡ audit entry và diagnostic có nhắc record đó** (tombstone theo sequence nên không hồi sinh sau restart hay restore). Bản backup encrypted chụp trước purge vẫn giữ byte của thời điểm đó — copy không thu hồi được, đúng contract S8-T2 — nhưng watermark purge đi kèm backup sau purge nên restore từ backup đó không hồi sinh entry đã gỡ (cả audit entry lẫn diagnostic). Một refusal trả lời bằng **error code thật** (kèm câu của owner ở field error, code ở field errorCode) nên trail ghi đúng code thật, không phải `unspecified`; **owner channel client đọc đúng contract đó** (code lấy từ field `errorCode`, câu lấy từ field `error`) nên một refusal không bao giờ bị báo thành transport unavailable và câu của owner không bao giờ bị đọc thành code — console hiện cả code thật lẫn câu của service, còn log của console ghi rõ act và code (`owner act: run job -> refused (pricing_unknown)`); khi bản ghi trail không ghi được (file hỏng, key/envelope lỗi, vault lock giữa request và lúc ghi) thì **câu trả lời của chính act đó vẫn tới được owner** và lỗi được báo thật trong safe operational log với `code=audit_persistence_failed`. Build này **không telemetry, không auto-update, không cloud reporting** và không mở kết nối outbound nào; claim của sản phẩm là key + lock + owner-approved APIs, không phải OS isolation trước same-owner access.

## 14. Chất lượng production và nghiệm thu

Sản phẩm hoàn chỉnh cần cài đặt, khởi động lại, nâng cấp/migrate schema, rollback/recovery phù hợp, backup/restore, export và gỡ ứng dụng không xóa nguồn trái ý. Không reset kho để né migration; lỗi/crash không mất memory đã báo lưu thành công.

Giới hạn dataset, latency, dung lượng, chi phí model và tài nguyên nền được đo trên máy đích ở Sprint 1, khóa thành operational envelope trước tuning. Không dựng con số tùy ý hoặc bỏ tiêu chí vì không có benchmark công khai.

**Historical A2/A4 baseline:** the following measurements and SQLite memory/headroom analysis predate the encrypted persistent v3 cutover; they are not current-format results.

Sprint 8 (S8-T5) kept the frozen R23/R38 targets at the time of the A13/A14 record; the owner-approved
2026-10-23 cut-over documented at the end of this section supersedes the numeric latency and
rebuild targets as T5 sign-off gates, and every measurement below stays as recorded. An intended proof surface is not evidence that
it ran. A14 supplies current-source in-process latency, disk and exact-result evidence on a
synthetic encrypted vault, but its 300-second core-proxy working-set averages (401,365,047 B
unlocked / 406,963,128 B locked) exceed the 256 MiB service-idle line. The same process also
seeded, rebuilt and queried the 100k fixture; attribution of retained fixture/query allocations
versus product is unknown. Treat the proxy as adverse evidence requiring review, not a service
pass or waiver. The real service, owner pipe, WPF/UI/IPC, fresh independent review and owner R37
walkthrough remain open. Synthetic scale fixtures do not replace owner-approved corpus judgment.
The persistent index remains a rebuildable encrypted derivative, with canonical records as the
only truth and scope, revision, provenance and ranking contracts unchanged.

**Historical A2 correction and A4 sizing (superseded by encrypted v3):** the old SQLite run reported
62.0 ms p95 at 1,000 records and 453.2 ms at 10,000; its 100k size and memory figures were
extrapolations, not current-format measurements. The earlier 3.3 GiB whole-process unlock/lock
delta was withdrawn as an index estimate; old SQLite index measurements were 18,675,576 B at
1,000 and 185,817,208 B at 10,000, with a 1.73 GiB linear 100k projection. A contemporaneous
3,074,248 KiB available-memory sample and reviewer-estimated 4 GiB planning floor are historical,
not current headroom or a product requirement. The current host headroom is unknown under the
host-inspection restriction; no current 100k authorization follows from these figures. The real
service/IPC DACL and owner R37 gates remain open.

**Historical COMPACT-A6 SQLite baseline, superseded by the encrypted persistent generation:** the details below are preserved for provenance only.

Historical COMPACT-A6 2026-10-22 changed the **representation** of the then-current SQLite private index, not its **search rules**:
mỗi posting giờ chỉ giữ **một** bản của key (doc id dạng số nguyên + token id dạng số nguyên), text
của token nằm đúng một lần trong dictionary riêng của engine, còn danh tính tài liệu (resource id
+ revision) nằm đúng một lần trong temporal row mà posting trỏ tới. Engine vẫn là cùng một cơ sở
SQLite trong bộ nhớ tiến trình, không file/WAL/journal/temp/cache trên đĩa, index vẫn là dữ liệu
dẫn xuất và biến mất khi lock/restart/crash. Đo trong tiến trình trên **cùng** corpus, **cùng**
vault và **cùng** bộ truy vấn, chạy song song engine schema 2 và schema 3: byte của chính engine
giảm từ 17.506.304 B xuống **3.960.832 B** ở 1.000 record (4,42x) và từ 173.809.664 B xuống
**39.809.024 B** ở 10.000 record (4,37x); bộ nhớ native của SQLite giảm 4,35x–4,36x; số posting
và số token khác nhau **không đổi**. So sánh **từng kết quả** trên 128 kịch bản mỗi cỡ (truy vấn
text tự nhiên và symbol chính xác, browse, scope nửa, lọc kind/current/level, cửa sổ thời gian,
as-of và marker của revision thứ hai) cho thấy hai dump **giống nhau từng byte** (0 dòng khác
biệt): cùng resource id, revision, score, thứ tự, publication, provenance và cùng tập token
đúng thứ tự. Rebuild ở 10.000 record giảm từ 24,9 s xuống **16,7 s**, còn p95 recall vẫn ở
**420,1 ms** (trước: 422,3 ms) dưới ngưỡng 500 ms đã khóa. Ở **100.000 record** phép đo đã chạy
thật (in-process, corpus tổng hợp 100.000 record): index gọn đo được **422.518.784 B (403,0 MiB)
trên 16.898.431 posting** (25,0 B/posting), rebuild lạnh **144,7 s (690,9 record/s)**, search p50
545,6 ms và p95 6.706,6 ms. **Mục tiêu đã khóa không được nới**: ngưỡng rebuild 100k **đạt**, còn
ngưỡng **256 MiB idle-RAM không đạt** (riêng index đã là 403,0 MiB so với ≈70 MiB ngân sách
còn lại sau nền ~186 MiB của service → working set dự báo ≈589 MiB, vượt khoảng 2,3 lần; phần
index là **đo**, phần service là **ngoại suy** vì kịch bản service thật vẫn bị chặn bởi DACL của
IPC), và ngưỡng latency p95 100k cũng **không đạt** (6.706,6 ms so với 1.000 ms; **nguyên nhân
đã được sửa ở COMPACT-FIX-A7 2026-10-22: không phải số body phải đọc mỗi truy vấn** — 200 truy vấn
đo chạy không bật bộ lọc level nên không đọc body ứng viên nào, chỉ 10 hit được phục vụ; cái giá
là **materialize ứng viên**: 48.686 ứng viên/truy vấn trung bình, 122 lô 400 dòng ứng viên mỗi
truy vấn, 21,0 GB cấp phát trong 200 truy vấn và 106 lần GC gen-2; phép đo 100k dùng **20 warm-up
+ 200 truy vấn** thay vì phương pháp đã khóa 100 warm-up + 1.000 truy vấn, và dump chỉ xong 24/28
kịch bản, nên nó **không** phải phép đo envelope tuân thủ phương pháp). 100k envelope trên service
thật vẫn chưa đo,
At the COMPACT-A6 snapshot, the private index had no persisted artifact and was rebuilt in process.
That historical no-artifact behavior ended with the encrypted-generation cutover: current v3 stores
`<vault>/private-index/index.v1.bin`, excludes the derivative from backup, and rebuilds it from
unchanged canonical records with explicit `owner index-rebuild` after restore or when unavailable.
No canonical reset or migration is needed to create that derivative.

Within its lifecycle, the current v3 generation is reloaded rather than rebuilt. After a lock and
unlock or a process restart, the first owner search, scoped search or `owner index-status` opens the
published generation, authenticates its sealed metadata with the borrowed vault key, and loads it
only when its canonical digest and row count equal the live canonical resource/revision set; the
read is read-only and the encrypted file is not rewritten. A missing generation still asks for the
explicit rebuild, a generation that describes another canonical set is refused as
`search_index_stale`, and a tampered, truncated or wrong-key generation is refused as
`index_unavailable`; the explicit owner rebuild is the supported recovery for each. The current
format has no record-level add/revise/delete delta: one sealed file describes the whole vault and
its postings, so a changed canonical set goes through the explicit rebuild. The historical results
remain in [worker evidence](../artifacts/sprint-8/task-5.md) §15 and
[handoff](../artifacts/sprint-8/handoffs/S8T5-COMPACT-A6.md).

#### Historical COMPACT-FIX-A7 — SQLite scope/identity fixes and corrected 100k attribution (2026-10-22)

The fixes and evidence below apply to the former SQLite engine only; the current v3 encrypted
implementation and the latest proof are documented in BOUNDED-A14.

- **Bộ lọc scope chạy bên trong SQL của engine.** Scope lớn hơn 900 resource được bind vào bảng
  scope riêng của engine, và câu truy vấn ứng viên **join** bảng đó, nên scope được áp trước khi
  bất kỳ dòng ứng viên nào rời engine. Trước đó engine trả về mọi resource mang token và phía
  gọi lọc lại, nên id ứng viên ngoài scope vẫn vượt qua ranh giới engine. Câu trả lời không đổi.
- **Chính cơ sở dữ liệu của engine cấp danh tính tài liệu.** doc id là rowid của temporal row, nên
  schema 2 → 3 mang sang. Trước đó engine tự đếm id trong bộ nhớ, nên tài liệu đầu tiên được
  index sau khi nâng cấp nhận đúng id mà dòng đã migrate chiếm và ghi thất bại với
  `UNIQUE constraint failed: private_index_temporal.doc_id`. Engine đã nâng cấp nay index tài liệu
  mới, thay tài liệu cùng danh tính và thêm revision y hệt engine mới mở.
- **Nguyên nhân latency 100k không phải đọc body** (chi tiết và bảng số ở
  [runbook vận hành](OPERATIONS.md) §9.2, mục COMPACT-FIX-A7).

Hai hồi quy vĩnh viễn: `MetaBrain.S1T4.Smoke.exe s8-t5-index` khẳng định scope bind trong engine
vẫn loại tài liệu ngoài scope, và khẳng định engine đã migrate giữ dòng cũ, index tài liệu mới,
thay tài liệu cùng danh tính, thêm revision và rebuild thế hệ mà không đụng id nào. Số đo, lệnh và
hồi quy nằm ở [worker evidence](../artifacts/sprint-8/task-5.md) §16 và
[handoff](../artifacts/sprint-8/handoffs/S8T5-COMPACT-FIX-A7.md).

**Historical A8 snapshot:** the format, no-spill statements and numeric results below describe the A8 binaries only. Current A14 behavior and measurements are documented later; no A8 figure is an A14 result.
BOUNDED-A8 2026-10-22 thực hiện hướng owner duyệt: private index trở thành **generation file mã hóa
at-rest** với **bộ nhớ bounded**, và đường truy vấn **stream ứng viên** thay vì materialize chúng.
Mục tiêu đã khóa không nới; số đo lại với cùng ngưỡng.

- **Định dạng.** Một generation là chuỗi record, mỗi record được seal bằng **chính AEAD của vault**
  (AES-GCM) với key suy ra từ data key của vault bằng HKDF-SHA256 dưới nhãn cố định — không có key
  riêng, không tự unlock, không plaintext key cạnh vault. Associated data của mỗi record bind
  **identity của generation** (suy ra từ canonical digest của tập record mà generation dựng từ),
  **kind của record**, **offset của record trong file** và format version: một record không thể bị
  di chuyển, đổi thứ tự, thay thế hay replay từ generation khác mà không hỏng tag. Nonce của mỗi
  record là một prefix ngẫu nhiên theo generation cộng một counter theo record, nên cặp
  (key, nonce) không bao giờ dùng lại. **Không có plaintext index, staging file, journal, WAL hay
  spill file** trên đĩa: staged generation chỉ là ciphertext, và publish là một thay thế atomic.
  Generation không đọc được (tamper, truncate, format mới hơn) thì **fail closed**; owner rebuild
  tường minh, canonical không bao giờ bị đụng. Một đánh giá độc lập có giới hạn (S8-T5) đã trả
  lời: thay thế header không thể ra đường phục vụ, replay của một generation hợp lệ **không** bị
  engine tự phát hiện nhưng bị cả hai đường truy vấn, rebuild và status từ chối; lỗ hổng mà
  đánh giá tìm ra (rebuild có thể stamp digest của canonical sống lên rows của generation khác) đã
  sửa và có hồi quy vĩnh viễn. Đánh giá là **chẩn đoán, không phải phán quyết bảo mật**, và
  **không** được tuyên bố là đã chứng minh tại đây.
- **Bộ nhớ bounded.** Engine giữ đúng hai thứ: **directory** (danh tính, revision và offset của
  từng tài liệu được index) với **ngân sách byte đo được** — generation vượt ngân sách bị từ chối
  thay vì được nạp — và **cache record đã giải mã** byte-bounded, evict least-recently-used. Posting
  **không** bao giờ resident toàn bộ: một truy vấn đọc posting list của đúng các term mình hỏi,
  merge chúng và score ứng viên theo lô. Từ 1.000 record, resident của engine là **94.000 B**
  directory + cache, so với **17.506.304 B** của engine SQLite cũ; ở 10.000 record là **940.000 B**
  so với **173.809.664 B**. Rebuild 1.713 s so với 7.339 s ở 1.000 record và 10.920 s so với
  23.067 s ở 10.000 record; recall p50/p95 **5,1/18,0 ms** so với **35,2/77,2 ms** ở 1.000 record
  và **13,4/24,3 ms** so với **120,5/570,3 ms** ở 10.000 record (ngưỡng đã khóa 250/500 ms).
- **Truy vấn.** Engine tự áp scope của caller, nên id ứng viên ngoài scope không rời engine. **Mọi**
  ứng viên trong scope đều được thăm và score; thứ tự, tie order, token equality/collation, scope,
  revision, date, currentness, provenance, score và excerpt **không đổi** — dump kết quả của cùng bộ
  truy vấn **giống nhau từng byte** so với engine cũ (1.000 record: cùng SHA-256 `d4b6122b…`).
- **Vòng đời.** Lock/restart xóa key, cache và mọi private read; interrupted rebuild không publish
  gì; index là derivative nên **không** nằm trong backup và được dựng lại sau restore. Bản ghi quyết
  định: [D077](../.agents/memory/decisions.md).


#### Historical BOUNDED-A11 — first bounded-builder source and measurement (2026-10-22)

At the A11 source snapshot, the private index used the v3 persistent encrypted generation
(`<vault>/private-index/index.v1.bin`) and built postings through a bounded byte arena with
dynamic multi-pass fan-in. Rebuild-only `index.v1.building`, `index.v1.spill`, and
`index.v1.directory-building` files contained individually authenticated ciphertext under the
existing vault-derived key and separate file identities; no plaintext scratch file or new crypto
was introduced. `File.Move(..., overwrite:true)` published the staged generation. The format
bounds one canonical memory document at 200,900 UTF-16 code units, 200,900 distinct tokens, at
most 1,808,100 UTF-8 token-text bytes, and 2,410,804 serialized token-record bytes. The A11 smoke
exhaustively checked Form-D output for every non-surrogate BMP code unit (maximum nine UTF-8 bytes
per code unit) and verified stem expansion within the token-count bound. This is source-derived for
the current Unicode tables, not an eternal guarantee.

The A11 cutover removed the previous silent 2,048-token/512-byte token rejection. The 256-character
normalized-query boundary and 2 Mi-character service-frame limit remain; every term in a supported
query is resolved in 64-term passes. The actual scoped agent handler accepts canonical
abstraction-level filters while time windows remain owner-only. The nonce counter fails before
wrap; its random 64-bit per-generation prefix makes cross-generation separation probabilistic, not
absolute. Per-bucket vocabulary overflow still refuses a rebuild closed rather than truncating
terms, so this is not a global-scale proof.

**Historical first A11 in-process measurement (not a target pass at 10,000).** The preserved
synthetic corpus generator ran through that source's encrypted vault, lifecycle, index authority
and owner search path, with a cold first query, 100 warm-ups and 1,100 timed queries per size:

| Records | Rebuild / throughput | Generation / resident directory | Cold first query | Warm p50 / p95 | Rebuild peak working set / private / managed | After timed queries working set / private / managed | Warm cache |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000 | 3.927 s / 254.7 rows/s | 1,892,405 B / 166,888 B | 233.44 ms | 9.12 / 62.74 ms | 60,305,408 / 29,876,224 / 13,641,296 B | 61,927,424 / 25,731,072 / 6,603,776 B | 534,392 B |
| 10,000 | 32.584 s / 306.9 rows/s | 17,573,899 B / 1,012,888 B | 906.95 ms | 91.12 / 671.53 ms | 88,662,016 / 59,174,912 / 34,408,456 B | 89,886,720 / 57,565,184 / 16,070,824 B | 2,695,408 B |

The frozen owner-recall p95 limits are 250 ms at 1,000 and 500 ms at 10,000. The A11 1,000-record
p95 was inside its unchanged limit; its 10,000-record p95 **exceeded 500 ms**. Preserve that miss:
it is not a result for the resumed A12 binary and is not erased by A12. The native database metric
is not applicable to this encrypted-file index; working-set, private and managed process values
were reported separately. The exact A11 runs, outputs and binary fingerprints remain in
`.agents/tmp/s8t5/a11/evidence/measurement/README.md`. A11 did not run the 100k envelope, real
service/UI/IPC or owner R37. A12 is historical; the current A14 results follow after the preserved A13 miss.

Historical SCALE-A3/A8 host samples and the old ~4 GiB planning estimate are preserved in the
runbook and task evidence; they are not current headroom evidence, a frozen product requirement,
or authority to start 100k. A3 did not run the 100k envelope or WPF path. Its static DACL finding
still blocks real service/UI/IPC execution under the current safety rules. Current total headroom
is unknown under the host-inspection restriction, so A12 did not run 100k. A future owner-authorized
in-process 100k run requires fresh measured headroom above a predeclared safe floor and the frozen
100-warm-up / at-least-1,000-timed method; it must not close unrelated applications. Owner R37 and
the real service/UI/IPC evidence remain external gates.

#### Historical BOUNDED-A12 — resumed source implementation and evidence

The current persistent index is the encrypted v3 derivative at `<vault>/private-index/index.v1.bin`.
The rebuild uses one immutable canonical descriptor snapshot and releases it before the live
reconciliation snapshot; the canonical SHA-256 digest streams the unchanged legacy byte encoding
in strict ordinal resource-ID order with invariant revisions. A multi-resource, multi-revision
handler fixture compares the generated digest directly with the former encoding. The canonical
store and recall contract were not changed.

The 64 MiB directory ceiling in the R8-reviewed source snapshot is historical; the current sealed
count-derived resident-directory preflight both use 32 MiB. The preflight accounts for the bounded
resource, bucket, row and sample counts, maximum identifier bytes, array references and fixed
managed overhead before allocating decoded arrays, then verifies the decoded footprint again.
Sharing the 32 MiB file/resident cap halves the maximum directory ciphertext-plus-plaintext read
overlap from 128 MiB to 64 MiB; with decoded directory and the separate cache, those known
structures can sum to 112 MiB before any builder, query or runtime memory. This is a budgeting
rationale, not a whole-process limit.

The separate default decrypted-record cache is capped at 16 MiB. This is not a whole-process cap:
an open can transiently hold directory ciphertext and plaintext while decoded arrays become
resident, and builder/query buffers are distinct. The five fixed builder arrays total 5,296,128 B
(4 MiB arena, 65,536 × 16 B pairs, 1,024 × 16 B merge pairs, 32,768 B spill-token storage and
4,096 B term scratch). At 20,090,000,000 maximum pairs (100,000 rows × 200,900 terms), there are at
most 306,550 sorted runs, seven levels and 49 retained level references; final merge adds a
bounded copy of at most 49 references and reads at most eight runs per group. Each spill chunk is
at most 1,024 pairs / 49,160 B plaintext / 49,192 B sealed frame. One term can use at most four
final chunks of 32,768 ordinals / 256 KiB; each 4,096-key bucket has at most 11 pages of 384
entries / 256 KiB. Vocabulary overflow refuses rebuild instead of losing terms.

The final A12 matcher validates each authenticated token record, binary-searches query terms in
exact UTF-8 byte order, and verifies the SHA-256 token identity only for byte-exact matches. This
retains false-positive protection if posting digests collide while avoiding a digest pass over
every token in every candidate. The served-page full-token path is unchanged.

The fresh final-source synthetic harness used a cold first query, 100 warm-ups and 1,100 timed
queries at each size. Memory values are bytes; peaks are sampled every 20 ms during rebuild:

| Rows | Rebuild / throughput | Generation / resident directory | Cold query | Warm p50 / p95 | Peak WS / private / managed | After timed WS / private / managed | Warm cache |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1,000 | 1.647 s / 607.1 rows/s | 1,892,405 / 166,888 | 53.77 ms | 3.74 / 23.15 ms | 58,294,272 / 29,609,984 / 12,732,312 | 72,708,096 / 37,249,024 / 4,770,616 | 534,392 |
| 10,000 | 12.320 s / 811.7 rows/s | 17,573,899 / 1,012,888 | 223.39 ms | 46.47 / 186.48 ms | 91,729,920 / 61,771,776 / 32,994,408 | 98,668,544 / 62,005,248 / 19,313,392 | 2,695,408 |

Both warm p95 values meet their unchanged 250 ms / 500 ms limits. The earlier complete A12
matcher measurement is retained as a miss (1,898.32 ms at 10,000); the timed-out initial attempt
has no p95 and is not reused. The exact-query-byte search was the measured, scope-preserving fix.

The A12 in-process smoke exercises actual owner status/library and scoped agent handlers with
before/after dumps, grant and frozen-revision boundaries, ranking/provenance, lifecycle filters,
pagination/browse, restore-and-rebuild, purge staleness, owner generation substitution, lock,
unmaterialized status, iterator failure/cancellation cleanup, malformed authenticated token
payloads, strict headers and nonce wrap. Exact build/smoke and fresh 1,000/10,000-record evidence
with hashes are kept in `.agents/tmp/s8t5/a12/evidence/README.md`. This synthetic harness does not
exercise the real service, WPF/UI, pipe or IPC. A12 had unknown host headroom and did not run
100,000 records; A14's later in-process run is not service/UI/IPC evidence. Those and owner R37
gates remain open.

#### BOUNDED-A13 — 100k current-source measurement (2026-10-23)

The A13 continuation used the exact R9-reviewed final-03 product assemblies and unchanged
deterministic synthetic corpus/query method; it changed no product code or acceptance target. At
100,000 rows, the in-process rebuild completed in 223.941 s. The preserved owner query method ran
100 warm-ups and 1,100 timed searches; warm p50/p95/max was 647.32 / **1,888.23** / 8,061.21 ms,
missing the frozen 1,000 ms p95 limit. The 2,635,496,576 B encrypted staging/spill peak combined
with the seeded vault for a 3,321,947,786 B build-disk peak, missing the 1.5 GiB limit of
1,610,612,736 B. The final encrypted vault was 858,639,799 B; a below-limit final artifact does
not erase the staging/build-peak miss.

| 100,000-row observation | Result |
| --- | ---: |
| Rebuild / throughput | 223.941 s / 446.5 rows/s |
| Generation / resident directory / warm cache | 172,188,312 / 9,472,888 / 16,752,768 B |
| Cold first natural query | 3,007.19 ms |
| Warm p50 / p95 / max | 647.32 / **1,888.23** / 8,061.21 ms |
| Rebuild peak WS / private / managed live | 1,030,508,544 / 1,352,286,208 / 1,291,200,528 B |
| Peak staging/spill / total vault with staging | 2,635,496,576 / 3,321,947,786 B |

A separate 300 s unlocked/locked core-index idle probe is a process proxy only (not the real
service, owner client, WPF/UI or pipe/IPC). It and bounded at-rest marker checks, the wrapper's
post-WARM collection defect, complete raw metrics and hashes are recorded in
`.agents/tmp/s8t5/a13/evidence/README.md` and the [A13 operations runbook](OPERATIONS.md). The 100k
warm-p95 and staging/disk misses remain binding; service/UI/IPC and owner R37 remain open. No
feature or full-task acceptance claim is made.



Tình huống định tính bắt buộc: quay lại project sau thời gian dài, tìm cách sửa lỗi đã gặp, hỏi lý do lịch sử, quyết định đã đổi, tái sử dụng bài học liên dự án đúng quyền, research còn bất định, explicit linking, reminder đúng lúc và purge/restore.

Security/integrity gates CR01: locked/wrong-key/tamper, ciphertext/private index/temp/backup confidentiality, catalog opt-in disclosure, two scoped bearer sessions, concurrent single-use redemption, guessed IDs/owner spoof, request approval, no scope growth, revoke/expiry/lock/restart/cache, path resolution, injection, source scope, declassification và egress. Không còn gate chống OS/shell/process/peer-credential bypass bởi agent cùng quyền owner. UI phải thao tác thật; crypto/API/CLI/MCP phải exercise thực, không chỉ unit tests.

CR01 replacement S1-T2–S1-T9 đã có evidence gate PASS cùng reviewed-snapshot commits/project checkpoint xác nhận (S1-T2 `db8747580093ddb9aa709ac578a2fa1ec045cf7b`, S1-T3 `f118ea1b1832f5325b896ef86b2ed2d155ed3469`, S1-T4 `1ede35e2a908a090aeb43e94621ba7a8f9293c70`, S1-T5 `15c97faa91e699ffad527a1018579238e4d0f526`, S1-T6 `83b92fa65d7f51f7c46fba52df783054fb757d93`, S1-T7 `f89ed6067d649ec8d0a30398b4e23e77cc045ab3`, S1-T8 `db61a08b0d7cd1b8e3b679cc7c0f4a698dc70a9f`, S1-T9 `127ef6ba4dcef3405cdd6f34474ac7da57915cc6`); final differential deep gate [S1-DEEP-20261007-A2](../artifacts/sprint-1/reviews/sprint-1-S1-DEEP-20261007-A2.md) PASS, no actionable findings. Sprint 2/M2 complete: S2-T1–T7 accepted evidence/checkpoints, final differential [S2-DEEP-20261012-A1](../artifacts/sprint-2/reviews/sprint-2-S2-DEEP-20261012-A1.md) PASS, latest reviewed product commit `e4e7c5f5accebbda6f39d6dfbfb349a51fe80442`; owner confirms all S2 changes committed. Sprint 3 In progress (S3-P0 complete; S3-T1 source enrollment/ingestion coordinator delivered, worker evidence pending review). Sprint 4–8 Not started. Old AppContainer baseline PASS và supplemental protected model proof superseded/stopped; xem [Sprint 1](sprint-plans/sprint-1.md) và [Sprint 2](sprint-plans/sprint-2.md).

#### BOUNDED-A14 — current-source spill/query correction and measurement (2026-10-23)

A14 preserves the published encrypted generation at v3 and changes only rebuild-only spill
scratch plus the authenticated query hot path. Scratch v2 groups sorted full-SHA-256 term
digests, then stores the group count/first ordinal and positive delta-varint ordinals in bounded
1,024-pair chunks with an eight-run merge fan-in. Scratch remains separately identified AEAD
ciphertext under the existing vault-derived key, nonce sequence and offset-bound AAD; no new
crypto or dependency was introduced. For candidate reads, one gate-owned `AesGcm` context and
bounded ciphertext/plaintext/AAD buffers are reused, strict token-record validation still covers
every entry, and exact UTF-8 byte equality remains the membership decision. Borrowed plaintext
is cleared before the iterator advances. The code does not rely on a digest-only shortcut, cap
candidates, or change scoring/filtering.

The one-query profile of an existing 100k generation (`S8T5-A14-QUERY-PHASE-01`, no rebuild)
measured 98,538 candidates. Authenticated token-record reads took 932.785 ms / allocated
116,601,064 B; exact matching took 928.742 ms / allocated 17,310,928 B. Together those phases
were 79.5% of profiled query time. The per-candidate plaintext, AAD and `AesGcm` allocations
were replaced with bounded reuse. This profile diagnosed the hot path; it is not a percentile or
post-fix timing result.

The current-source focused `s8-t5-index` scenario passed. It exercises collision-safe exact token
matching, strict malformed input, partial iterator/cancellation/lock cleanup, supported query
limits, real owner and scoped-agent filter/ranking behavior, and nine popular 100k postings
through grouped multi-pass spill with every final ordinal checked. No profile hooks or counters
remain in product code.

Current-source measurements used the preserved synthetic corpus generator and 100 warm-ups plus
1,100 timed queries at each size:

| Rows | Rebuild / throughput | Cold query | Warm p50 / p95 / max | Generation / directory / cache (B) |
| ---: | ---: | ---: | ---: | ---: |
| 1,000 | 1.490 s / 671.3 rows/s | 39.55 ms | 3.61 / 18.50 / 29.42 ms | 1,892,405 / 166,888 / 534,392 |
| 10,000 | 10.226 s / 977.9 rows/s | 221.59 ms | 8.70 / 60.53 / 79.95 ms | 17,573,899 / 1,012,888 / 2,695,408 |
| 100,000 | 88.003 s / 1,136.3 rows/s | 1,498.11 ms | 118.10 / 680.55 / 847.30 ms | 172,188,312 / 9,472,888 / 16,752,768 |

The warm p95 values are within the unchanged 250 / 500 / 1,000 ms limits. The 100k mean/max
candidate matches were 48,844.3 / 99,996; no terms or candidates were capped. Natural / symbol
expected-hit rates were 0.0218 / 1.0000.

In `S8T5-A14-100K-FINAL-01`, the complete 250 ms vault sampler observed 273,620,804 B peak
private-index staging/spill and 960,082,329 B combined vault-plus-staging peak, with 0
missing-file races. The unchanged combined limit is 1,610,612,736 B, so this local disk target
was measured within bounds. The published index stayed 172,188,312 B and the final vault was
858,650,109 B. The failed A13 p95 and disk peak remain preserved in the preceding historical
section.

The first A14 100k parity claim is withdrawn: the alleged baseline run used current A14
Core/Application DLLs and a Connections DLL matching neither A12 nor current A14, despite
producing the same TSV hash as the current arm. Those retained files are invalid parity evidence.

The corrected isolated baseline used product DLLs hash-verified before execution against the A12
authoritative README (Core `c4031af4e77efa633cbffd502cb6677ae5b07abf4e3e67031d0da44daca2c91f`,
Application `eef97d906bb7440dbcc45fca5af337da1b0787a418770af21a40b69603a7ce4f`, Connections
`82de5472fcbf6d89f4eda4ee7edbfaa85e4ccd5a59f27788af6952c07529cc85`). It opened the existing
copied A14 v3 generation without rebuilding the product or rerunning the current arm. The
corrected A12 baseline and retained current A14 outputs each contain a cold page and all 1,100
timed pages from the frozen 100k method (after 100 warm-ups); each TSV has 1,102 lines and
SHA-256 `9ea9376c081b6494efd6a9d25df9901ca0cd31e34b2863b7ce5cbcff0f413481`. `fc /b` returned 0
with no differences. The fixture query hash is
`9781860aecd43d616c814c9078c78682731fb5dcec3ed41b2cf9b2403fc17292`; the generation index
hash is `966c35c204a349d732229d75af244c504490e3c6b26f283218bb0c4458e95fa2`. The result digest
covers totals and all returned hit fields, scores, order, revisions, reasons and metadata, not
query text/body. It covers the 1,100-query frozen sample, not all 200,000 generated rows.

The copied vault's only pre-run byte difference from its source was encrypted `scope-grants.enc`;
after the baseline open its ciphertext hash changed again. This owner query gets its 100k scope
from `operation.ListResources()` and does not use agent grants, so raw grant-byte parity is not
claimed. The query runner emitted its result marker and complete TSV with empty stderr, but the
PowerShell wrapper failed to capture the child exit code and returned 1 after output emission;
no exit-0 claim is made. See `.agents/tmp/s8t5/a14/evidence/README.md` for component hashes,
the exact grant values, run logs and the invalid earlier artifacts.

**Adverse memory evidence remains open.** The same process seeded, rebuilt and queried before
the 300 s core-proxy idle phases. Unlocked / locked working-set averages were 401,365,047 /
406,963,128 B, above the frozen 256 MiB service-idle line; private-byte averages were
740,235,928 / 746,482,871 B. The same process had already reported 1,268,473,144 B managed
and 1,002,147,840 B working set after seeding and before rebuild. No forced GC or attribution
instrumentation was used. Retained fixture, query/rebuild-phase and product contributions cannot
be separated, so attribution is unknown. This is adverse proxy evidence requiring review, not a
service pass or a waiver; the real service/UI/IPC path was not run.

#### S8T5-RAM-PROVENANCE-A15 — fresh-child owner-query diagnosis (2026-10-23)

Two sequential fresh children used the existing synthetic A14 100k vault. R1 opened the existing v3 index only, made no query, and exited 0; a fresh owner handler correctly refused `owner.library.search` with `index_not_materialized`. After that supported limitation was established, Main authorized one R2 child to invoke the real in-process `owner.index.rebuild` API and run cold + 100 warm-up / 1,100 timed owner searches, followed by 300 s unlocked and 300 s locked idle. R2 rebuilt 100,000 rows with current canonical digest and schema 3, then ran the complete frozen method.

The R2 child wrapper captured its actual `WaitForExit`/`ExitCode` as **11**, not 0. The in-child checker incorrectly required each broad natural query's expected item to appear in the top-10 page; this caused 538 natural-target absence flags. An offline compare of 1,101 cold/timed rows against the current A14 TSV found zero differences in shared query inputs/ID hashes, phase/index, symbol, total, hit count, or A14 hash reference; no equality between the old core `OwnerLibraryPage` hash and new service-wire hash is claimed. The exit 11 and incomplete marker are preserved, not relabeled as success.

Working-set averages/peaks were 286,283,625 / 307,220,480 B during cold/warm/timed queries, 289,931,762 / 290,004,992 B in unlocked idle, and 288,975,235 / 290,709,504 B in locked idle, all adverse to the frozen 268,435,456 B line. After lock the index directory and cache were zero but working set remained elevated. Managed/GC/private/index counters are recorded separately. A bounded snapshot retains at most 512 sampled rows; the owner handler creates per-request full-scope temporary structures; the index directory and cache were 9,472,888 B and 16,752,768 B unlocked. With forced GC disallowed, the remaining heap residency cannot be classified as rooted product data versus uncollected garbage/CLR residency. This is adverse in-process proxy evidence, not service-memory acceptance or proof of a service failure, and does not justify a speculative product change or target relaxation.

R1/R2 used the SDK-pinned A15 harness (10.0.402, roll-forward disabled) on runtime 10.0.13 with R11-matching product DLLs. The 1 GiB free-memory guard stayed above 4,633,284,608 B; there was no host mutation or app closure. The existing A14 full-hash list remains historical for the pre-rebuild derived-index bytes. Raw logs, offline comparison, build/SDK evidence and the bounded artifact manifest are in `.agents/tmp/s8t5/a15/evidence/README.md`; the handoff is `artifacts/sprint-8/handoffs/S8T5-RAM-PROVENANCE-A15.md`. Real service, WPF/owner UI, pipe/IPC, R37 proof, independent review and Main-owned plan/checkpoint gates remain external. No full S8-T5 or security PASS is claimed.


#### S8T5-HANDLER-RESUME-A17 — owner request-local snapshot (2026-10-23)

The owner search continues to refuse a locked vault and any engine generation whose canonical digest differs from the unlocked epoch’s materialized digest. For each accepted request it builds one ordinal-sorted filtered owner scope, obtains one live canonical `ListResources()` snapshot, and checks that exact ID/revision snapshot with the existing digest before using it. The same verified snapshot is passed into the owner-only search path; served-hit freshness uses binary search over that strict canonical ordering instead of constructing another full-vault revision dictionary. The snapshot and scope are request-local only; there is no cross-request cache, weakened freshness check, or changed zone/collection/currentness/time/archive/revision/ranking/tie rule.

The focused final smoke passed the expanded owner-library cases, but it does not close performance acceptance. The existing synthetic 100k actual-handler protocol completed all 1,100 timed calls with p95 2,810.977 ms against the frozen 1,000 ms limit; the normal owner rebuild took 1,645,385.959 ms against the 20-minute limit. The 3,600.21 s runner timeout occurred during unlocked idle sample 92/300; locked idle and actual child exit were not captured. This is adverse in-process evidence, not service/UI/IPC acceptance. Raw evidence and binary/source binding are in `.agents/tmp/s8t5/a17/evidence/README.md`; details and remaining gaps are in [task history](../artifacts/sprint-8/task-5.md) §23 and the [A17 handoff](../artifacts/sprint-8/handoffs/S8T5-HANDLER-RESUME-A17.md).

#### S8T5-GOLD-BOUNDED-A18 — canonical owner scope and measured limits (2026-10-23)

For an owner library request, the service now treats the strictly ordered canonical resource/revision/zone snapshot it already fetches for the generation check as the owner scope: the snapshot must equal the materialized generation digest and the live canonical digest before any search, zone/collection filters are applied to it, and the same snapshot supplies served-hit revision checks. The previous path decrypted and re-projected every materialized index row (~100k at the measured size) on each request; that duplicate read is gone. The superseded-relation scan is evaluated only for `current` queries, because it cannot affect `all`/`history`. Lock refusal, exact revision reads, currentness/history/as-of, archived policy, recall, zone projection, ranking and tie order are unchanged, and no cross-request cache was introduced.

Two Gold runs were spent. The first used a bounded 8-envelope rebuild batch and was stopped by the mandatory 1 GiB host free-memory guard during the rebuild; the batch was withdrawn (the host decline is recorded as a correlation, not proven causation, because no per-process attribution exists). The second used the serial rebuild and completed the full 100k protocol with actual exit 12: handler p95 **1,668.026 ms** (p50 183.937 ms) against the unchanged 1,000 ms target — improved from A15 2,167.332 and A17 2,810.977 but still a miss — average allocation 29,286,018 B, rebuild 1,396,956.547 ms against 20 minutes (miss), combined vault+staging disk 1,132,272,603 B (within 1.5 GiB), unlocked/locked idle working set 207,084,032 / 204,846,357 B and idle CPU 0.0709 % / 0.0729 % (both within). The comparator reported 0 common-field mismatches over 1,101 rows. This is in-process proxy evidence, not service/UI/IPC or owner R37 acceptance; 1k/10k handler p95, UI p95 and provider cost remain unmeasured.

#### OWNER-ACCEPTANCE-CUTOVER-A20 — S8-T5 sign-off basis after the owner decision (2026-10-23)

The owner replaced the frozen latency and rebuild figures **as T5 sign-off gates** after reviewing
A18/R13 and the A19/R14 lifecycle proof. The replacement is recorded as the requirements change
"Owner-approved S8-T5 acceptance cutover — 2026-10-23" ([requirements](REQUIREMENTS.md)) and
[D086](../.agents/memory/decisions.md). Recall p95 at 1,000/10,000/100,000 records (250/500/1,000 ms)
and the 20-minute 100,000-record rebuild remain the Sprint 1 envelope and the gates the A11–A18 runs
were measured against, and every recorded measurement and miss (A11, A13, A14, A15, A17, A18; R11,
R13, R14) stays a historical fact — no PASS is created for them. They no longer decide T5, and the
outstanding mandatory 1k/10k actual-handler reruns are not a T5 condition.

T5's quantitative basis is the observed A18 run 2 practical baseline: 100,000 rows in process,
handler p50/p95 183.937 / 1,668.026 ms, rebuild 1,396,956.547 ms (23 min 16.957 s), comparator 1,101
rows with 0 common-field mismatches. That is a historical observation of that snapshot, not a numeric
ceiling for future builds and not a service/UI/IPC measurement. T5's behavioural basis is A19: an
authenticated generation whose canonical digest and row count match the live canonical set is reused
across lock/unlock and restart without rewriting the encrypted file, and a missing, stale, tampered,
truncated or wrong-key generation fails closed (`index_not_materialized`, `search_index_stale`,
`index_unavailable`) with the explicit owner rebuild as the only recovery; the current format has no
per-record delta.

Unchanged: the bounded resident directory/cache caps and builder bounds, encryption at rest through
the existing vault AEAD and vault-derived key with no new crypto and no plaintext scratch, scope /
revision / currentness / provenance / ranking correctness, lock clearing key, cache and resident
reads, and the prohibition on host/ACL/permission/system mutation. The A18 CPU, disk and idle-RAM
results remain historical observations within the limits they were measured against; no budget is
expanded. Not waived: real service/WPF/owner UI/IPC evidence, the S8-T6 security/recovery replay, the
S8-T7 owner walkthrough with the R37 qualitative judgement, independent review and any checkpoint.

## 15. Tham chiếu và giới hạn bằng chứng

- [MemPalace](https://github.com/mempalace/mempalace): học cách tổ chức và giữ nguyên nguồn; không coi wing/room tự là ACL.
- [The Palace](https://github.com/mempalace/mempalace/blob/main/website/concepts/the-palace.md): metadata filtering và khái niệm drawer/closet; không giả định mọi khái niệm đều là persisted implementation.
- [CogMem architecture](https://github.com/triet4p/cogmem/blob/master/tutorials/ARCHITECTURE/overview.md): provenance/raw snippet, intention, action-effect và lazy synthesis; không mang nguyên graph pipeline sang.
- [CogMem idea](https://github.com/triet4p/cogmem/blob/master/docs/CogMem-Idea.md): ý tưởng nhận thức là tham chiếu, không bằng chứng sản phẩm này đã hoạt động.
- [MCP roots](https://modelcontextprotocol.io/specification/2025-11-25/client/roots): context roots không thay authorization.
- [Windows access tokens](https://learn.microsoft.com/en-us/windows/win32/secauthz/access-tokens) và [AppContainer](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation): tham chiếu cho experiments lịch sử, không dependency của CR01.

Các nguồn trên đã được dùng để tham khảo tài liệu, không phải audit toàn bộ source code hay benchmark độc lập. Mọi lựa chọn chưa kiểm chứng được nêu ở [PLAN](PLAN.md) và phải được giải quyết bằng evidence, không bằng giả định thuận tiện.
