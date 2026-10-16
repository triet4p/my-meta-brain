# Meta Brain — Đặc tả sản phẩm và kiến trúc

- Trạng thái: Sprint 1/M1 replacement complete (S1-T2–T9 evidence/checkpoints PASS, doc correction PASS/checkpoint `911c12a0f157e7d176587528a0a32b52c8345407`, final differential deep [S1-DEEP-20261007-A2](../artifacts/sprint-1/reviews/sprint-1-S1-DEEP-20261007-A2.md) PASS). Sprint 2/M2 complete (S2-T1–T7 accepted evidence/checkpoints, final differential [S2-DEEP-20261012-A1](../artifacts/sprint-2/reviews/sprint-2-S2-DEEP-20261012-A1.md) PASS, latest reviewed product commit `e4e7c5f5accebbda6f39d6dfbfb349a51fe80442`; owner confirms all S2 changes committed). Sprint 3 In progress (S3-P0 complete; S3-T1 source enrollment/ingestion coordinator accepted with evidence/checkpoint; S3-T2 Codex session JSONL read-only adapter delivered, worker evidence pending review). Sprint 4–8 Not started. CR01 threat model, budgets và unexercised downstream limits unchanged; historical S1/S2 evidence trong [Sprint 1](sprint-plans/sprint-1.md) và [Sprint 2](sprint-plans/sprint-2.md).
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

Chỉ giữ plaintext/key cần thiết trong bộ nhớ lúc unlock, giải phóng cache/key handles khi lock; không hứa secure erasure tuyệt đối của mọi managed-memory/OS dump. Backup luôn encrypted; export plaintext chỉ sau owner unlock, chọn scope và xác nhận disclosure. ACL và log hygiene là defense-in-depth, không thay mã hóa hoặc được quảng cáo thành sandbox.

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

 Implementation hiện là service/CLI Windows với owner control channel và agent access channel riêng: owner dùng `owner.status`, managed-resource `read`/`write --expected-revision`, `memory-create`/`memory-revise --expected-revision`/`memory-import --expected-revision`/`memory-read`, `source-create`/`source-revise --expected-revision`/`source-read`/`research-capture`/`research-read`, `relation-create`/`relation-accept --expected-revision`/`relation-reject --expected-revision`/`relation-unlink --expected-revision`/`relation-relink --expected-revision`/`relation-read`, `provision`, `unlock`, `recover`, `lock`, `grant`/`grants`, `collection-set`/`collections`, `catalog-preview`/`catalog-publish`/`catalog-list`/`catalog-withdraw`, `requests`/`request-preview`/`request-approve`/`request-reject`, `sessions`, `revoke-session`, `redeem`, `session`, `authorize` và `agent-read`; agent dùng `agent-catalog-list`, `catalog-query`, `request`/`request-status`, `redeem`, `session`, `authorize` và scoped `resource.read`/`source.read`/`memory.read`/`agent.source.read`/`agent.relation.read` và tạo link trong grant qua `agent.link.create`. Ghi canonical yêu cầu đúng live revision (tạo mới chỉ khi chưa tồn tại); staged ciphertext commit trước manifest nên crash không để nửa chừng, unlock dọn bytes/temp dở dang, và `memory-import` là biên import tường minh thay mọi sửa plaintext trực tiếp. Relation là canonical encrypted resource trên cùng vault với ID đặt trước `rel-`, hai endpoint đã kiểm tra loại (memory item hoặc registered source) cùng đúng revision đang live tại thời điểm tạo link (span chỉ gắn registered source), kind, người/job tạo, phương thức tạo, lý do, thời gian UTC và vòng đời proposed/accepted/rejected cùng trạng thái unlink có ghi rõ lý do; endpoint và kind không đổi sau khi tạo. `derived_from` và `supersedes` cùng là cạnh dẫn xuất nên cấm mọi chu trình (cả chuỗi pha trộn), còn `related_to` được phép có vòng. Khi endpoint đổi revision hoặc registered source được citation không còn available, link trực tiếp và các phụ thuộc bắc cầu được báo needs_review từ trạng thái live, không sửa nội dung item và không coi link accepted là mệnh đề đã đúng; revision đã ghim vẫn là bản ghi lịch sử trung thực. Đọc relation đòi đúng relation ID/revision được cấp và cả hai endpoint trong snapshot tại revision live với operation khớp loại endpoint (`source.read` cho registered source, `memory.read` cho memory item), nên endpoint liên vùng hoặc ngoài quyền chỉ nhận một denial chung không lộ metadata/title/count/backlink; agent tạo link trong grant `link.create` luôn ở trạng thái proposed với executor agent vì accept là thao tác owner. Source registry giữ metadata registered source (title/locator/version/availability, author khai báo khi có) dưới dạng canonical encrypted trên cùng vault, không copy body của nguồn ngoài; citation của item ghi sourceId cùng source revision đang live cộng span đã kiểm tra biên tại thời điểm ghi, và việc serve item không đọc registry nên grant item không tự mở source API; anchor đã ghi sau khi source đổi revision giữ nguyên là bản ghi lịch sử trung thực thay vì được coi là đã kiểm chứng lại. Từ S3-T4, `research-capture` lưu văn bản research phi cấu trúc nguyên trạng thành một registered source với text verbatim encrypted (với note, text là nội dung gốc duy nhất — không phải bản copy của nguồn ngoài) cùng zone bắt buộc và title/URL/tác giả/ngày nguồn tùy chọn; thiếu trường nào thì trường đó unknown, không suy từ link, không fetch URL, không phân loại/trích xuất, và text chưa từng xuất hiện trên metadata wire của source. `research-read` trả đúng byte văn bản kèm metadata; khi vault locked cả capture lẫn read bị từ chối `vault_locked` và không ghi plaintext draft/queue hay báo saved; hai operation này owner-only và agent channel không tới được (transport allowlist). Lifecycle domain là canonical encrypted record trên cùng vault với ID đặt trước `lc-` suy ra từ subject memory item (một subject một record; zone, kind, trạng thái đầu, executor và revision đều suy ra server-side từ subject envelope, trusted context và vault descriptor, không nhận từ body request), với `lifecycle-create`, `lifecycle-read`, `lifecycle-retained-read --resource-revision` và các act owner `lifecycle-publish`/`lifecycle-reject`/`lifecycle-withdraw`/`lifecycle-repropose`/`lifecycle-question-resolve`/`lifecycle-question-reopen`/`lifecycle-intention-complete`/`lifecycle-intention-cancel`/`lifecycle-intention-supersede`/`lifecycle-intention-notify`/`lifecycle-intention-acknowledge`/`lifecycle-validity-set`/`lifecycle-retain --expected-revision`. Publication proposal/published/rejected/withdrawn chỉ đổi qua act tường minh có actor và lý do ghi trong history append-only; câu hỏi không tự resolved bởi AI answer hay relation addresses; notified/acknowledged là marker riêng chứ không phải done; supersession tường minh tên item thay thế và supersession mâu thuẫn bị từ chối chứ không ghi đè kiểu newest-wins; occurred/recorded/valid_from/valid_until/review_after giữ đúng precision, timezone offset và unknown (unknown không bị bịa ngày). Revision lịch sử còn truy được qua service thật: act `lifecycle-retain` tường minh ghi byte đúng revision đó vào archive resource ID suy ra `arc-<subject>-r<N>` trên cùng store và `lifecycle-retained-read` trả đúng envelope lịch sử kèm cả hai revision, còn grant chỉ trên archive không mở item live hay revision mới hơn; việc owner lưu/sửa note của AI không đổi author, creation method hay evidence level của bản ghi gốc. Membership collection chỉ là selector phía owner, nằm trong encrypted scope state: `owner collection-set`/`collections` đổi membership và tăng policy generation, còn `agent.collection.view` (qua `owner agent-collection-view`) chỉ chiếu lại snapshot đã đóng băng của session thành đúng các resource ID/revision đã duyệt — không có tên collection, thành viên, title, zone, kind, count, total hay paging, và grant trên một tập con của collection không bao giờ thấy thành viên ngoài phạm vi đã duyệt. Thêm member, sửa title/path hay tạo descendant zone/lifecycle/archive mới không vào được view đang sống: session cũ fail closed thay vì tự mở rộng scope, và membership không tự cấp quyền đọc thành viên chưa duyệt hay relation/lifecycle/archive/source chỉ trỏ tới thành viên đã duyệt.
Updated implementation (S4-T6; this supersedes earlier S2-T4 agent link command names): agent `relation.resolve`/`relation.list` use the `relation.list` grant operation; resolve searches only exact frozen memory/source IDs and revisions with matching `memory.read`/`source.read`, returns titles only, and emits `clarification_required=true mutation_performed=false` for ambiguity. List returns relations only when both pinned endpoints independently satisfy those rights. The CLI `agent-relation-create`/`supersede`/`unlink`/`restore` and MCP `metabrain_relation_create`/`supersede`/`unlink`/`restore` route through the same service policy; each requires its corresponding `link.create`/`link.supersede`/`link.remove`/`link.restore` plus both endpoint reads. Agent creation stamps executor `agent`, method `paraphrase`, and state `proposed`; caller-reported request evidence is an encrypted relation event shown only to the owner, not a trusted user-confirmation signal. No relation operation changes endpoint content, opens a revision/zone/membership, or declassifies data.


Mỗi vault có data key ngẫu nhiên được wrap riêng bằng passphrase và recovery key chỉ hiện một lần; .NET chuẩn cung cấp PBKDF2-HMAC-SHA-256 (600.000 vòng) và AES-256-GCM. Manifest và metadata resource nằm trong ciphertext; resource file dùng tên ngẫu nhiên, ciphertext được xác thực gắn với vault/resource ID/schema/revision. Key không được lưu plaintext cạnh vault, trong settings, environment hoặc log. Grant/verifier/request state và owner-managed flat collection membership nằm trong encrypted `scope-grants.enc` sche…

Catalog publication lưu private catalog-ID→resource mapping trong encrypted `owner-catalog.enc` (chỉ explicit owner label/description, không suy từ private text), phục vụ discovery từ plaintext `published-catalog.json` cạnh vault (chỉ approved ID/label/description, rebuildable). Discovery không cần session, hoạt động khi locked/startup/restart; withdrawal xóa record nên list/query/fresh/restart hội tụ `no_match`. Catalog không cấp body/source access. Access request lưu pending/mục đích/agent/provider/disclosure đã khai báo trong encrypted scope state; agent chỉ nhận opaque receipt/status, owner preview đọc exact body/revision/scope/disclosure rồi narrow/approve/reject; chỉ narrowed owner-approved exact scope mint token qua T3. Stale preview (withdrawal/republication/revision/registration/collection change, epoch/generation) phải preview lại, không tự mở scope. Provider egress authorization khớp exact provider/model v…
Phân tách source references, inbox/proposals, published memories, intentions, private index và catalog công bố. Private policy/grants/audit/locators/revisions cũng phải được bảo vệ; chỉ crypto-format envelope tối thiểu không nhạy cảm và metadata owner chủ động công bố được nằm ngoài encryption. Tên thư mục không tiết lộ private zone/title, và adapters không phụ thuộc physical path.

 Implementation (S4-T2): scoped search (`agent.memory.search` trên agent channel với grant operation `memory.search`, `owner agent-search` trên CLI, `metabrain_memory_search` trên MCP bridge) chạy trên đúng protected private index đã materialize (schema 2, memory-only, thêm token-posting và temporal tables keyed theo resource ID và revision): candidate filter theo snapshot IDs/revisions đã duyệt chạy TRƯỚC mọi private retrieval (scope IDs bind trong SQL WHERE của index engine, chỉ page được phục vụ mới decrypt ở đúng frozen revision với authorize trước/sau và registration check từng hit). Query mở rộng cục bộ không embedding/provider (case/diacritic folding, symbol/camelCase split, light English stemmer, stopwords, bảng general-vocabulary EN/VI như error/bug/lỗi, login/đăng nhập — áp dụng đối xứng mọi query, không lookup theo fixture). Ranking theo token overlap có trọng số, hạ withdrawn/rejected/stale; lọc kind/current theo metadata; pagination ổn định trên frozen order với scope-only totals; stale generation fail closed `search_index_stale`; revision mới không vào grant; lock/revoke deny calls tiếp theo. Catalog query giữ nguyên published labels/descriptions, không private ranking/snippets/counts. Chỉ owner channel được materialize private index; agent channel không có operation index nào. Private index (S2-T7) là SQLite standard (`Microsoft.Data.Sqlite` 10.0.12, MIT) chạy hoàn toàn trong bộ nhớ tiến trình (`Data Source=:memory:`, `PRAGMA temp_store=MEMORY`, `journal_mode=OFF`, `locking_mode=EXCLUSIVE`): không có database file, WAL, rollback journal, sort temp hay page cache nào trên disk và engine không giữ key material. `owner index-rebuild` đọc manifest, giải mã từng canonical envelope và ghi lại đúng ID, revision, zone scope, record kind/state cùng các ID được tham chiếu (citation, relation endpoint, lifecycle subject/supersession/retained archive); `owner index-status` fingerprint SHA-256 tập (resource, revision) canonical đang sống và báo generation đã materialize là `stale` thay vì tin nó. Schema index có version (`PRAGMA user_version`, hiện 1) với migration step 0→1 thật và `RequireSupported` fail closed cho version lạ; S8 thêm step mới không phải sửa caller. Rebuild không ghi canonical: canonical file vẫn là truth duy nhất, lock/unlock/restart/crash đều bỏ index, và rebuild không chứng minh được generation khớp canonical thì trả `index_canonical_moved`. Index không tự công bố title/body/count/embedding vào catalog và không mở surface cho agent; ranking/search thuộc S4.

Ngân sách vận hành (D008) đã đo thực tế trên máy đích qua real service, real named pipe và real owner CLI với synthetic fixture 1,000/10,000/100,000 records (4 KiB mean body, tạo trong một pass O(n) qua seeded vault path). `owner index-rebuild` đọc envelope theo batch giới hạn (128/batch, song song bậc 8) bên trong một counted operation: mỗi file vẫn giữ reparse/ACL, AEAD binding và revision gate riêng, plaintext được zeroize sau khi insert, và lock chỉ xóa key sau khi operation đã drain. Baseline bất lợi được giữ nguyên làm ground truth: binary trước fix, fixture 10,000 cache-cold, cold rebuild 111,511 ms = 89.7 records/s (< ngưỡng 100 records/s). Sau fix, trên binary cuối cùng (lần rebuild đầu tiên sau khi fixture được tạo và aging tự nhiên ~30 phút, không có flush/trim/admin/forcedGC; wall time không phải chứng cứ miss của OS độc lập): 1,930 ms (518.1 records/s) ở 1,000 / 23,418 ms (427.1 records/s ≥ ngưỡng 100 records/s) ở 10,000 / 271,326 ms (4.5 phút, 368.7 records/s ≤ 20 phút) ở 100,000; idle working set tối đa 77,344,768 B / 103,419,904 B / 237,543,424 B (~73.7 / ~98.6 / ~226.5 MiB ≤ 256 MiB) ở ba kích thước sau warm-up trong cửa sổ 5 phút (61 samples, không forced GC hay OS trim); idle CPU 0.020115% / 0.000649% / 0.003244% host (≤ 1%); persistent product data 8,653,390 B / 86,599,822 B / 866,137,705 B (~826 MiB ≤ 1.5 GiB), index thêm 0 byte persistent và 0 sidecar ở mọi kích thước. Để hỗ trợ kích thước 100,000 resources đã công bố sẵn có, manifest đọc được cấp cap riêng (64 MiB plaintext / 96 MiB envelope, trên worst case 100,000 descriptor) thay vì dùng chung cap envelope 4 MiB của resource, và request `owner.index.rebuild` được re-arm timeout 25 phút cho riêng operation đó (mọi request khác giữ nguyên guard 10 giây). Lock trong lúc rebuild đang chạy dừng rebuild đó thay vì chờ nó chạy hết: lock request huỷ các batch read đang in-flight tại batch boundary, bounded active I/O drain xong mới clear key (các invariant drain/lifetime giữ nguyên — lock vẫn chỉ hoàn tất sau khi counted operation dispose), rebuild bị huỷ fail closed với status riêng `index_rebuild_cancelled` và không publish generation, phân biệt với timeout 25 phút của chính request đó (vẫn trả `request_timeout`). Đã kiểm chứng trên real pipe ở 10,000 và 100,000 records: lock hoàn tất trong 315 ms với rebuild bị huỷ, cùng một request đọc hợp lệ chỉ bị từ chối khi locked và được phục vụ lại đúng body sau re-unlock, rebuild ở epoch mới sau huỷ hoạt động bình thường, hai lock chồng lấn đều an toàn, và negative tamper không đổi. Combined service+idle-client ≤ 512 MiB và latency owner UI/search không được exercise bởi gate này (không có UI/MCP surface trong S2-T7) nên không có số đo nào được tuyên bố cho các mục đó.

Các invariant bắt buộc:

- Cập nhật có revision check, ngăn lost update; publication và relation changes không để trạng thái nửa chừng sau crash.
- Quyết định thứ tự ghi canonical/index và recovery rõ ràng; không hứa một transaction chung cho file + SQLite nếu chưa có cơ chế thực.
- Journal/recovery chỉ đủ cho invariant cần thiết, không xây event-sourcing platform tổng quát.
- Rebuild index khi unlocked tái hiện đúng scope, revision, lifecycle và nguồn khả dụng; không viết plaintext DB/WAL/temp hoặc trở thành truth store thứ hai.
- External ciphertext sửa đổi/truncated, wrong key và stale revision bị reject/recover theo protocol; không hỗ trợ “sửa trực tiếp plaintext canonical file”. Import/export owner rõ ràng thay đường đó.
- Backup/export không hồi sinh live sessions/tokens; encrypted backup cần key/recovery của user. Chỉ explicit owner export được tạo plaintext với cảnh báo copies không thu hồi; migration không reset/xóa dữ liệu cũ để né chuyển format.

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

Thay quyết định giữ lịch sử supersedes. Current query tránh lời khuyên hết hiệu lực; historical query có thể truy lại. Mâu thuẫn chưa phân giải vẫn hiển thị là mâu thuẫn, không mặc định newest wins.

Intention có pending/done/cancelled/superseded, trigger theo thời gian hoặc ngữ cảnh user chọn. Lịch chạy không cần LLM. Không có agent kết nối thì due item vẫn tồn tại. Đã nhắc không phải đã hoàn thành; lịch đã tắt không tiếp tục âm thầm nhắc. Việc hoàn thành cần thao tác/bằng chứng theo policy, không suy từ câu trả lời AI.

Quên gồm:

1. Giảm ưu tiên trong recall/brief.
2. Archive vẫn tìm lịch sử được.
3. Purge theo owner/policy đã bật, bao phủ canonical, index/cache/snapshot và xử lý lineage liên quan.

Không tự xóa chỉ vì ít truy cập; bài học hiếm có thể rất quan trọng. Tần suất đọc chỉ là tiện ích, không phải độ tin cậy. Pin, restore archive và lý do xếp hạng phải giải thích được. Quyền xóa dữ liệu nguồn ngoài vault là riêng, không mặc định xóa session runtime.

## 13. Owner application

UI tối thiểu về phạm vi sản phẩm, không phải MVP:

- Inbox: capture văn bản, nguồn mới, proposals và trạng thái job.
- Library: duyệt theo zone, project/topic, kind, thời gian, level và tìm kiếm.
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

## 14. Chất lượng production và nghiệm thu

Sản phẩm hoàn chỉnh cần cài đặt, khởi động lại, nâng cấp/migrate schema, rollback/recovery phù hợp, backup/restore, export và gỡ ứng dụng không xóa nguồn trái ý. Không reset kho để né migration; lỗi/crash không mất memory đã báo lưu thành công.

Giới hạn dataset, latency, dung lượng, chi phí model và tài nguyên nền được đo trên máy đích ở Sprint 1, khóa thành operational envelope trước tuning. Không dựng con số tùy ý hoặc bỏ tiêu chí vì không có benchmark công khai.

Tình huống định tính bắt buộc: quay lại project sau thời gian dài, tìm cách sửa lỗi đã gặp, hỏi lý do lịch sử, quyết định đã đổi, tái sử dụng bài học liên dự án đúng quyền, research còn bất định, explicit linking, reminder đúng lúc và purge/restore.

Security/integrity gates CR01: locked/wrong-key/tamper, ciphertext/private index/temp/backup confidentiality, catalog opt-in disclosure, two scoped bearer sessions, concurrent single-use redemption, guessed IDs/owner spoof, request approval, no scope growth, revoke/expiry/lock/restart/cache, path resolution, injection, source scope, declassification và egress. Không còn gate chống OS/shell/process/peer-credential bypass bởi agent cùng quyền owner. UI phải thao tác thật; crypto/API/CLI/MCP phải exercise thực, không chỉ unit tests.

CR01 replacement S1-T2–S1-T9 đã có evidence gate PASS cùng reviewed-snapshot commits/project checkpoint xác nhận (S1-T2 `db8747580093ddb9aa709ac578a2fa1ec045cf7b`, S1-T3 `f118ea1b1832f5325b896ef86b2ed2d155ed3469`, S1-T4 `1ede35e2a908a090aeb43e94621ba7a8f9293c70`, S1-T5 `15c97faa91e699ffad527a1018579238e4d0f526`, S1-T6 `83b92fa65d7f51f7c46fba52df783054fb757d93`, S1-T7 `f89ed6067d649ec8d0a30398b4e23e77cc045ab3`, S1-T8 `db61a08b0d7cd1b8e3b679cc7c0f4a698dc70a9f`, S1-T9 `127ef6ba4dcef3405cdd6f34474ac7da57915cc6`); final differential deep gate [S1-DEEP-20261007-A2](../artifacts/sprint-1/reviews/sprint-1-S1-DEEP-20261007-A2.md) PASS, no actionable findings. Sprint 2/M2 complete: S2-T1–T7 accepted evidence/checkpoints, final differential [S2-DEEP-20261012-A1](../artifacts/sprint-2/reviews/sprint-2-S2-DEEP-20261012-A1.md) PASS, latest reviewed product commit `e4e7c5f5accebbda6f39d6dfbfb349a51fe80442`; owner confirms all S2 changes committed. Sprint 3 In progress (S3-P0 complete; S3-T1 source enrollment/ingestion coordinator delivered, worker evidence pending review). Sprint 4–8 Not started. Old AppContainer baseline PASS và supplemental protected model proof superseded/stopped; xem [Sprint 1](sprint-plans/sprint-1.md) và [Sprint 2](sprint-plans/sprint-2.md).

## 15. Tham chiếu và giới hạn bằng chứng

- [MemPalace](https://github.com/mempalace/mempalace): học cách tổ chức và giữ nguyên nguồn; không coi wing/room tự là ACL.
- [The Palace](https://github.com/mempalace/mempalace/blob/main/website/concepts/the-palace.md): metadata filtering và khái niệm drawer/closet; không giả định mọi khái niệm đều là persisted implementation.
- [CogMem architecture](https://github.com/triet4p/cogmem/blob/master/tutorials/ARCHITECTURE/overview.md): provenance/raw snippet, intention, action-effect và lazy synthesis; không mang nguyên graph pipeline sang.
- [CogMem idea](https://github.com/triet4p/cogmem/blob/master/docs/CogMem-Idea.md): ý tưởng nhận thức là tham chiếu, không bằng chứng sản phẩm này đã hoạt động.
- [MCP roots](https://modelcontextprotocol.io/specification/2025-11-25/client/roots): context roots không thay authorization.
- [Windows access tokens](https://learn.microsoft.com/en-us/windows/win32/secauthz/access-tokens) và [AppContainer](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation): tham chiếu cho experiments lịch sử, không dependency của CR01.

Các nguồn trên đã được dùng để tham khảo tài liệu, không phải audit toàn bộ source code hay benchmark độc lập. Mọi lựa chọn chưa kiểm chứng được nêu ở [PLAN](PLAN.md) và phải được giải quyết bằng evidence, không bằng giả định thuận tiện.
