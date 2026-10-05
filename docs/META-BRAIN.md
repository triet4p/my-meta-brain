# Meta Brain — Đặc tả sản phẩm và kiến trúc

- Baseline: 2026-09-28; security scope thay bằng owner-approved CR01/D016 ngày 2026-10-01.
- Trạng thái: Sprint 1 reopened theo CR01/D016. S1-CLEAN đã qua evidence gate PASS cho cleanup, không phải encryption/token acceptance. S1-T2–S1-T5 đã có evidence gate PASS cùng project checkpoints (S1-T4 `1ede35e2a908a090aeb43e94621ba7a8f9293c70`, S1-T5 `15c97faa91e699ffad527a1018579238e4d0f526`). S1-T6 implementation cùng focused Windows service/CLI/IPC smoke đã hoàn tất, evidence/correction Pending (task vẫn [~], chưa PASS). S1-T7–T9 Pending; Sprint 2–8 Not started. Old seven task gates/deep PASS chỉ là lịch sử AppContainer.
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

User có thể nói qua agent: “Nối ý này với research X”, “Bản mới thay thế nhận định cũ”, “Hai ý liên quan nhưng không dùng làm bằng chứng”. Agent resolve ID trong phạm vi được đọc; hỏi khi mơ hồ thực sự; core kiểm tra quyền rồi thực hiện thao tác có thể hoàn tác. Không hỏi xác nhận lại mọi link rõ ràng trong grant.

Lưu riêng executed_by, request evidence và owner confirmation. Không tin trường tự khai origin=user từ agent như bằng chứng owner đã nói. Kênh host có chứng thực có thể nâng mức xác nhận; nếu không, đây là thao tác agent theo ủy quyền. Không chặn mọi liên kết thường ngày vì thiếu chứng thực, nhưng không dùng chúng để mở quyền hoặc declassify.

Chỉ cho tạo/xem liên kết khi người gọi có quyền cần thiết với endpoints và quyền sửa relation tại phạm vi sở hữu. Không để title, snippet, counts, backlink hoặc lỗi lookup tiết lộ endpoint ngoài quyền. Liên kết không cấp quyền. Chủ sở hữu và yêu cầu đọc của relation phải được core lưu và kiểm tra nhất quán, kể cả sau khi đổi zone hoặc thu hồi grant.

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

Luồng: agent xem catalog → gửi yêu cầu IDs/phạm vi và mục đích → owner preview nội dung/revisions, raw-source scope và provider/model nhận context → owner thu hẹp hoặc từ chối/chấp thuận → ứng dụng mint token. Lý do và identity do agent tự khai chỉ là dữ liệu request, không là authorization. Catalog đã rút không thể xóa bản metadata agent đã lưu.

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
Authorization API chỉ trả quyết định allow/deny, không lấy/giải mã/trả nội dung hoặc đọc cache. Lock xóa session trong bộ nhớ, process restart làm mất session, unlock tăng epoch để vô hiệu hóa grant/session cũ. Đây không phải OS-bound agent identity hoặc sandbox; same-owner pipe SID chỉ xác thực transport, không chứng minh con người đã duyệt. MCP bridge không được đọc ciphertext/key hoặc tự cấp quyền; agent resource retrieval và product-client integration vẫn là công việc sau, không được suy ra từ giao thức session/access của service.

Implementation hiện là service/CLI Windows với owner control channel và agent access channel riêng: owner dùng `owner.status`, managed-resource `read`/`write`, `provision`, `unlock`, `recover`, `lock`, `grant`/`grants`, `collection-set`/`collections`, `sessions`, `revoke-session`, `redeem`, `session`, `authorize` và `agent-read`. `sessions` liệt kê snapshot trong bộ nhớ; `revoke-session` chỉ thu hồi session được chọn, không thay thế durable grant revocation. `authorize` gửi yêu cầu tới agent pipe và chỉ trả quyết định; `agent-read`/`agent.resource.read` kiểm tra session cùng operation/resource ID/revision chính xác trước và sau khi mở bytes vault đã mã hóa rồi kiểm tra lại registration trước khi trả nội dung. Từ chối dùng một mã `resource_unavailable` chung; lock/restart/revoke/expiry/generation chặn reads tiếp theo.

Mỗi vault có data key ngẫu nhiên được wrap riêng bằng passphrase và recovery key chỉ hiện một lần; .NET chuẩn cung cấp PBKDF2-HMAC-SHA-256 (600.000 vòng) và AES-256-GCM. Manifest và metadata resource nằm trong ciphertext; resource file dùng tên ngẫu nhiên, ciphertext được xác thực gắn với vault/resource ID/schema/revision. Key không được lưu plaintext cạnh vault, trong settings, environment hoặc log. Grant/verifier state và owner-managed flat collection membership nằm trong encrypted `scope-grants.enc` schema v3; authenticated schema-v2 state được nâng cấp nguyên tử, grants hiện hữu được giữ nguyên và state ghi unlock epoch cùng verifier tombstones đã tiêu thụ. Zone, collection hoặc exact-ID selectors được chốt thành resource IDs/revisions cụ thể, không có hierarchy/discovery; membership changes không sửa grants đã cấp. `grant` mặc định read-only; proposal/link cần destination scope riêng, provider egress cần provider/model/cost rõ ràng. Owner CLI yêu cầu explicit expiry UTC và gõ `ISSUE`; collection membership cần gõ `SET`. Bearer 256-bit chỉ persist dưới dạng SHA-256 verifier, được tiêu thụ bền vững trước khi session được trả; raw token không in ra terminal hoặc lưu plaintext và chỉ đi qua protected local handoff. Session authority chỉ tồn tại trong process từ snapshot owner-approved; policy-generation change, lock và restart vô hiệu hóa session; unlock epoch invalidates grant/session cũ. Service khởi động locked; lock chặn operation mới, chờ lease/in-flight response drain, zero data-key bytes trong ứng dụng và bỏ reference manifest. Đây không phải bảo đảm xóa string/managed memory, pagefile/dump hoặc mọi bản sao trên thiết bị lưu trữ. Cùng quyền OS owner vẫn có thể lấy key/plaintext khi vault mở.

Provider egress authorization khớp exact provider/model và từ chối cost hoặc token estimate vượt $0.25/job, 10.000 input hoặc 2.000 output tokens; agent-supplied IPC vẫn bị từ chối vì chưa có provider adapter/pricing context đáng tin cậy. Chưa có provider/model call path hoặc $5/30-day rolling-spend ledger, và không claim những giới hạn đó đã được thực thi end-to-end. Scoped agent resource retrieval qua `agent.resource.read` đã hiện thực/kiểm chứng ở S1-T6; MCP/product-client integration còn lại, AppContainer/protected launcher không còn là dependency.

## 7. Lưu trữ local và tính toàn vẹn

File-first: canonical serialization có schema/version và human-readable sau owner unlock/export, nhưng file lưu trên đĩa là authenticated ciphertext, không plaintext Markdown/JSON. SQLite/private index chỉ là dữ liệu dẫn xuất có thể dựng lại và phải được bảo vệ at rest; catalog công bố là projection riêng. Không coi file ngoài ứng dụng là input hợp lệ chỉ vì tên/extension.

Phân tách source references, inbox/proposals, published memories, intentions, private index và catalog công bố. Private policy/grants/audit/locators/revisions cũng phải được bảo vệ; chỉ crypto-format envelope tối thiểu không nhạy cảm và metadata owner chủ động công bố được nằm ngoài encryption. Tên thư mục không tiết lộ private zone/title, và adapters không phụ thuộc physical path.

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

## 9. Research và cấu trúc hóa

Đầu vào bắt buộc là ô nhập văn bản phi cấu trúc, chọn zone, có thể thêm URL/tác giả/ngày nguồn. Không cần crawler hay PDF parser để sử dụng được. URL chỉ là locator, không tự chứng minh đã đọc bài gốc.

Lưu source trước rồi mới chạy job; provider lỗi không làm mất ghi chú. Pipeline dùng chung:

1. Normalize nguồn và attribution; trường chưa biết để unknown.
2. Structure thành item proposal, mỗi item trỏ đoạn nguồn hỗ trợ.
3. Đề xuất relation trong quyền đọc; similarity không thành causality.
4. Core validate schema, provenance, zone và trạng thái.
5. Owner review/publish; policy tự động chỉ áp dụng phần owner đã cho phép.

Profile:

- Coding: vấn đề, cách thử, bằng chứng kết quả, quyết định, điều kiện môi trường.
- Research/paper note: câu hỏi, phương pháp được mô tả, claim của tác giả, giới hạn, giả thuyết của user, câu hỏi mở.
- Trend: observation theo thời gian, nguồn, phạm vi quan sát và giả thuyết xu hướng.

Ví dụ “tác giả báo cáo Y hiệu quả trên tác vụ ngắn; có thể thử ở project A; chưa rõ session dài” không được thành “Y hiệu quả ở project A”. Nhiều bài nhắc cùng paper không mặc nhiên là nhiều xác nhận độc lập.

Trend mặc định phản ánh nguồn user đã lưu, không tuyên bố bao phủ Internet. Giữ bản tổng hợp từng thời điểm, delta, phản ví dụ và khoảng trống, không ghi đè lịch sử.

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

## 14. Chất lượng production và nghiệm thu

Sản phẩm hoàn chỉnh cần cài đặt, khởi động lại, nâng cấp/migrate schema, rollback/recovery phù hợp, backup/restore, export và gỡ ứng dụng không xóa nguồn trái ý. Không reset kho để né migration; lỗi/crash không mất memory đã báo lưu thành công.

Giới hạn dataset, latency, dung lượng, chi phí model và tài nguyên nền được đo trên máy đích ở Sprint 1, khóa thành operational envelope trước tuning. Không dựng con số tùy ý hoặc bỏ tiêu chí vì không có benchmark công khai.

Tình huống định tính bắt buộc: quay lại project sau thời gian dài, tìm cách sửa lỗi đã gặp, hỏi lý do lịch sử, quyết định đã đổi, tái sử dụng bài học liên dự án đúng quyền, research còn bất định, explicit linking, reminder đúng lúc và purge/restore.

Security/integrity gates CR01: locked/wrong-key/tamper, ciphertext/private index/temp/backup confidentiality, catalog opt-in disclosure, two scoped bearer sessions, concurrent single-use redemption, guessed IDs/owner spoof, request approval, no scope growth, revoke/expiry/lock/restart/cache, path resolution, injection, source scope, declassification và egress. Không còn gate chống OS/shell/process/peer-credential bypass bởi agent cùng quyền owner. UI phải thao tác thật; crypto/API/CLI/MCP phải exercise thực, không chỉ unit tests.

Đến 2026-10-01, bảy task gates và deep review AppContainer baseline cũ đã PASS trong synthetic fixtures. CR01 reopen Sprint 1; cleanup S1-CLEAN mới đã qua agent://CR01CleanupEvidenceGate PASS, không encryption/token acceptance. Current CR01 status: S1-T2 và S1-T3 đã có evidence gate PASS cùng reviewed-snapshot commits xác nhận; S1-T4 implementation và focused real-IPC evidence đã được worker báo cáo, đang chờ Main evidence review và project checkpoint (không claim T4 PASS); S1-T5–T9 Pending, Sprint 2–8 chưa bắt đầu. Lịch sử và stopped supplemental model proof ở [Sprint 1](sprint-plans/sprint-1.md), không chuyển PASS cũ thành nghiệm thu mới.

## 15. Tham chiếu và giới hạn bằng chứng

- [MemPalace](https://github.com/mempalace/mempalace): học cách tổ chức và giữ nguyên nguồn; không coi wing/room tự là ACL.
- [The Palace](https://github.com/mempalace/mempalace/blob/main/website/concepts/the-palace.md): metadata filtering và khái niệm drawer/closet; không giả định mọi khái niệm đều là persisted implementation.
- [CogMem architecture](https://github.com/triet4p/cogmem/blob/master/tutorials/ARCHITECTURE/overview.md): provenance/raw snippet, intention, action-effect và lazy synthesis; không mang nguyên graph pipeline sang.
- [CogMem idea](https://github.com/triet4p/cogmem/blob/master/docs/CogMem-Idea.md): ý tưởng nhận thức là tham chiếu, không bằng chứng sản phẩm này đã hoạt động.
- [MCP roots](https://modelcontextprotocol.io/specification/2025-11-25/client/roots): context roots không thay authorization.
- [Windows access tokens](https://learn.microsoft.com/en-us/windows/win32/secauthz/access-tokens) và [AppContainer](https://learn.microsoft.com/en-us/windows/win32/secauthz/appcontainer-isolation): tham chiếu cho experiments lịch sử, không dependency của CR01.

Các nguồn trên đã được dùng để tham khảo tài liệu, không phải audit toàn bộ source code hay benchmark độc lập. Mọi lựa chọn chưa kiểm chứng được nêu ở [PLAN](PLAN.md) và phải được giải quyết bằng evidence, không bằng giả định thuận tiện.
