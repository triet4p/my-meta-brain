# Meta Brain — Decision Log

Append-only. Baseline chi tiết: [đặc tả](../../docs/META-BRAIN.md), [yêu cầu và change control](../../docs/REQUIREMENTS.md), [PLAN](../../docs/PLAN.md). Đây là quyết định thiết kế, không phải evidence runtime đã pass.

## [2026-09-28] D001 — Giữ vai trò bộ nhớ trợ giúp

**Decision:** Meta Brain hỗ trợ lưu, tìm và kết nối; các tác vụ thay đổi ý nghĩa hoặc synthesis chạy theo yêu cầu/policy opt-in, không tự chọn agenda hay biến phỏng đoán AI thành belief của user.

**Alternatives considered:** Autonomous research/knowledge agent thường trực; extraction mọi session; tự cập nhật user profile từ hành vi.

**Reason:** Owner cần bộ nhớ thứ hai nhanh/gọn, không hệ thống dẫn dắt suy nghĩ; giảm noise, cost và việc lưu sai ý định.

**Consequences:** Question, hypothesis, authorship và evidence là metadata hạng nhất; những automation có ý nghĩa phải có UI control và explicit provenance. R01, R12, R20–R22 không được hạ để tiết kiệm task.

## [2026-09-28] D002 — Tách owner toàn quyền khỏi agent runtime

**Decision:** Owner toàn quyền mặc định; agent dùng OS-isolated runtime và session grants qua memory service, không trực tiếp đọc vault.

**Alternatives considered:** Chỉ filter zone trong MCP; chạy mọi process dưới owner token; mở ACL folder theo agent_name.

**Reason:** Shell/process/credential access có thể đi vòng MCP nếu agent thừa hưởng quyền owner. Agent khác grant cũng phải cách ly credential.

**Consequences:** Sprint 1 phải chứng minh cả Codex và OMP thật, không fallback toàn quyền. Cơ chế Windows cụ thể chưa chốt; chọn bằng probe/compatibility evidence. Không chống owner/admin cố tình phá isolation hoặc thu hồi dữ liệu đã đọc.

## [2026-09-28] D003 — Tách core, application và connections

**Decision:** Dùng modular architecture với một core domain/policy, owner application/UI và connections như MCP/source adapters; chỉ tách process theo security/lifecycle thực tế.

**Alternatives considered:** MCP server làm toàn bộ sản phẩm; UI ghi trực tiếp vault; microservices cho từng concern.

**Reason:** UI và nhiều runtime phải dùng chung quyền/provenance/lifecycle, nhưng thay transport không cần viết lại nghiệp vụ; microservices chưa có nhu cầu chứng minh.

**Consequences:** Không UI/MCP SDK trong core; adapters không ghi canonical trực tiếp; stack/IPC/UI host được Sprint 1 lựa chọn theo toàn lộ trình, chưa ấn định thư viện trong baseline.

## [2026-09-28] D004 — File-first canonical và reference-only sessions

**Decision:** Tri thức canonical có thể đọc/export bằng local files, SQLite index có thể dựng lại; session .codex/.omp giữ nguyên tại chỗ, import mặc định chỉ đọc và tham chiếu.

**Alternatives considered:** Migrate/rewrite toàn bộ logs; chỉ lưu vector DB; copy toàn bộ profile mặc định; dùng file và DB như hai nguồn sự thật độc lập.

**Reason:** Bảo toàn lịch sử runtime, tính di động và nguồn kiểm chứng; không thu thập dữ liệu riêng tư quá nhu cầu. Index cần hiệu quả nhưng không giữ dữ liệu duy nhất.

**Consequences:** Cần crash/revision/reconciliation protocol, source availability và snapshot opt-in. Reference-only không bảo đảm nguồn tồn tại mãi; cache/chunk vẫn nằm trong privacy/retention scope. Format serialization cụ thể chọn ở Sprint 2, phải giải quyết xung đột shared rules nếu có.

## [2026-09-28] D005 — Tách zone, collection và tầng trừu tượng

**Decision:** Zone là quyền; collection là tổ chức; abstraction level là mức khái quát; item có stable ID/revision và typed relations có provenance.

**Alternatives considered:** Một cây folder đồng thời làm ACL/topic/hierarchy; mọi câu là fact; graph database/ontology đầy đủ ngay từ đầu.

**Reason:** Research và project có nhiều liên hệ nhưng không thể cấp quyền theo topic hoặc coi tầng cao là chân lý. Explicit links qua chat cần ngữ nghĩa và nguồn tác giả rõ.

**Consequences:** Relation về logic là graph nhẹ, không bắt buộc graph infrastructure; derived_from có cycle guard, source revisions giữ lineage; link không share dữ liệu. Multi-source synthesis mặc định restrictive, owner declassification là thao tác riêng.

## [2026-09-28] D006 — Khóa outcome theo chặng, không gọi demo là sản phẩm

**Decision:** Tám sprint có requirement IDs, acceptance gates và change control; sản phẩm daily production chỉ hoàn thành sau toàn bộ R01–R40 cùng owner acceptance.

**Alternatives considered:** MVP trước rồi để security/backup/provenance làm lại sau; khóa cứng mọi thư viện trước thực nghiệm; thay mục tiêu khi một check thất bại.

**Reason:** Owner yêu cầu cân nhắc downstream ngay từ đầu nhưng tránh over-engineering. Outcome cần cứng; implementation cần được điều chỉnh theo evidence tốt hơn.

**Consequences:** Security kiểm chứng sớm, durability từ core; packaging/restore/release vẫn là scope bắt buộc. Thay requirement cần evidence, impact map, owner approval và cập nhật đồng bộ docs/gates. Planning không phải implementation evidence.

## [2026-09-28] D007 — Chọn stack Windows và candidate isolation

**Decision:** Dùng C#/.NET 10 LTS cho Core/application, Windows Service không chạy administrator, WPF owner client, named pipes versioned cho owner/agent, self-contained win-x64 packaging; thử AppContainer theo grant ở S1-T3 nhưng không coi là đã cách ly.

**Alternatives considered:** Rust có compiler 1.91.1 và đã build/chạy một PE probe 133,632 byte; Bun/Node 22 và OMP đều hiện diện; Python 3.13.3/uv 0.9.7 cũng sẵn. HTTP localhost thuận tiện cho nhiều ngôn ngữ nhưng không tự xác thực caller; Windows PowerShell 5.1/.NET Framework đã round-trip named pipe giữa hai process cùng user, chưa thử ACL/token. WPF/WebView2, Windows Service/task chạy lúc login, AppContainer/VM/identity riêng và framework-dependent/self-contained package là các phương án được cân nhắc; WebView2 runtime có nhưng SDK .NET không có.

**Reason:** Windows là deployment đầu tiên; .NET 10 và Windows Desktop runtime đã cài, pipe probe xác nhận API Windows có thể trao đổi liên tiến trình cùng user, và một stack C# có thể dùng chung contract cho service, owner UI/CLI và adapters thay vì thêm service wrapper cho Node/Bun hay Python. Rust build được nhưng service/UI/pipe API sẽ cần platform crates hoặc native glue; chọn được compiler không chứng minh toàn bộ boundary. Self-contained giảm phụ thuộc runtime trên máy đích. Tuy nhiên không có .NET SDK nên chưa build/publish stack đã chọn; đó là prerequisite chưa được giải quyết, không phải bằng chứng pass.

**Consequences:** Service virtual identity `NT SERVICE\MetaBrain` chỉ được cấp ACL cần thiết; owner control và agent data dùng named-pipe endpoints riêng, auth lấy từ OS token chứ không từ body. Core không phụ thuộc UI/MCP SDK; owner app không đọc vault trực tiếp; MCP adapters chỉ map DTOs. AppContainer SID riêng là thử nghiệm S1-T3; nếu Codex hoặc OMP không tương thích thì dừng và tìm lựa chọn mới theo evidence, không fallback toàn quyền. Cần owner-approved build/OS setup trước khi cài service, tạo identity hoặc thử ACL; chưa cài gì trong S1-T1.

## [2026-09-28] D008 — Khóa operational envelope trước tuning

**Decision:** Khóa bộ synthetic 1,000/10,000/100,000 records và các ngưỡng p95, indexing, idle CPU/RAM, disk và provider spend ở Sprint 1; S8 đo lại nguyên ngưỡng, không đổi để che fail.

**Alternatives considered:** Chờ corpus thật rồi mới đặt budget; chỉ benchmark một kích thước; dùng leaderboard/corpus công khai; hoặc để provider/model tự quyết định spend.

**Reason:** R38 đòi target đo được trước tuning. Thiết bị khảo sát có 4 core/8 logical, 16 GiB RAM và F: còn 48.7 GiB trống, nên fixture growth 100,000 records và giới hạn 512 MiB idle RAM/1.5 GiB data đặt phạm vi có thể bác bỏ trên máy đích; không có approved corpus hay model price card để tuyên bố số đo. Provider mặc định $0 và hard caps chỉ áp dụng sau opt-in; unknown cost phải deny.

**Consequences:** Synthetic fixture chỉ kiểm tra hành vi kỹ thuật, không đại diện distribution owner; S8 cần corpus owner-selected có allowlist/consent và giữ riêng truy vấn chất lượng. Target p95 recall/UI, 100 records/second, 20-minute rebuild, 1% CPU, 256/512 MiB RAM, 2.5 GiB combined data+installed cap (1.5 GiB data + 1 GiB binaries), $0.25/job, $5/30 days and 10k/2k token caps remain frozen pending change control. S1-T1 không chạy indexing, product UI hoặc provider measurement.

## [2026-09-28] D009 — Bounded S1-T1 source sample

**Decision:** After the owner's read-only testing authorization for `F:/ai-ml`, use the fixed six-document sample recorded in `artifacts/sprint-1/task-1.md`: two Meta Brain documents, three ecommerce-agent-databricks documents and one Bridge Research kickoff note.

**Rationale:** This bounded, purposive sample spans two project work areas and a research note, with bilingual planning/decision/contract content and a Python source file suitable for later recall and UI/indexing scenarios without copying source data.

**Consequences:** Count the six selected files as source-document records only, not indexed memory items; the decision log had eight prior entries (D001–D008) when sampled. This correction adds D009 as a worker-authored evidence note, not owner-authored corpus content or a quality-evaluation target. The sample does not establish statistical representativeness, a 500-record holdout or 200 owner-approved prompts/relevance labels. Synthetic 1,000/10,000/100,000-record stress fixtures and all frozen R38 targets remain separate and unchanged; no product indexing or performance measurement was run.

## [2026-09-29] D010 — Keep the Codex compatibility build fixture-only

**Decision:** Build official Codex `rust-v0.46.0` with the narrow `find_codex_home` canonicalize-to-absolute fallback and use the hash-pinned executable only in the reversible S1-T3 fixture.

**Alternatives considered:** Widen AppContainer filesystem access, keep using installed unpatched Codex, replace the installed Codex package, or treat a source patch without a built client as evidence.

**Reason:** Native fixture evidence isolated the denial to DOS final-path resolution although direct reads and NT final-path resolution succeeded; weakening ACLs or silently substituting the installed client would invalidate the R04/R05 boundary test. The owner authorized only an official-source build staged inside a new, temporary fixture.

**Consequences:** The fixture runner must fail closed on an absent or mismatched SHA256, must not change the installed Codex, and must record source/dependency provenance. This compatibility binary is test evidence, not a product release or installer.

## [2026-09-30] D011 — Separate owner-control and AppContainer-agent channels

**Decision:** Use two versioned local Windows named pipes. The control channel derives owner context from the impersonated client token's user SID and non-AppContainer status; the server sends its authenticated ready response before reading an owner request. The agent channel admits only configured AppContainer SIDs, verifies the server-observed user/container SIDs and credential ID, then checks a fresh HMAC-SHA-256 challenge proof. Core owns the immutable authenticated context and policy, Application consumes it, and Connections owns the Windows boundary. Request-body identity claims are ignored. There is no owner bearer credential.

**Alternatives considered:** A shared endpoint with caller-supplied roles; a bearer credential for owner authority; localhost HTTP or request-body identity; one transport implementation per client.

**Reason:** Windows token inspection and separate pipe ACLs bind the channel to OS identity instead of process claims. A per-session AppContainer SID plus a protected credential proof distinguishes agent sessions; the challenge binds the proof to the protocol and data endpoint without transmitting the raw secret.

**Consequences:** The current settings contain preprovisioned principal/session bindings and credential verifiers; the smoke fixture uses two synthetic AppContainers and verifies real named-pipe requests, spoofing, peer credential denial, and channel separation. This does not implement grant lifecycle, resource reads, production credential provisioning/service installation, or product Codex/OMP integration. Those remain downstream work; no fallback to owner credentials is allowed.

## [2026-09-30] D012 — Persist grants as a protected generation-checked snapshot

**Decision:** Store the complete service grant policy in one versioned `grant-policy.json` beside protected service settings, serializing mutations in-process and replacing the snapshot through a protected same-directory temporary file with a durable flush and monotonically increasing generation.

**Alternatives considered:** A SQLite policy database coupled to the rebuildable index; an append-only journal requiring replay and compaction.

**Reason:** The service is the single policy authority and grants are a small bounded control-plane state; a single snapshot keeps startup, backup behavior, and restart semantics explicit without introducing a second database or recovery subsystem. An expected-generation check makes out-of-process changes fail closed, while the service/System/Administrators ACL matches the existing owner/admin-outside-threat-model boundary.

**Consequences:** Policy evolution requires explicit snapshot schema migration; policy and derived/index data remain separate sources of truth. A missing snapshot starts with no grants, while corrupt or inaccessible state prevents policy service from starting. The single-writer file contract depends on the service instance owning the named-pipe endpoints.

## [2026-09-30] D013 — Route OMP resource reads through the authenticated service

**Decision:** OMP's per-session stdio MCP bridge maps `metabrain://resource/<id>` reads to `resource.read` on that session's authenticated agent pipe, using the existing service grant policy.

**Alternatives considered:** Let OMP read protected files directly; add a parallel MCP-only authorization path; use ACP or a client-side helper instead of the service resource API.

**Reason:** The installed OMP client can invoke its real `read mcp://` command without a model/provider turn. Reusing the authenticated agent pipe and `resource.read` keeps principal/session identity, grant scope, and revocation enforcement in the service; a helper-generated read would not prove the consumer path.

**Consequences:** OMP resource reads require a live resource grant and never carry the raw credential in the URI or process arguments. Revocation denies the next read. The pinned Codex 0.46.0 `mcp list` path does not provide a no-model resource read, so OMP evidence does not establish Codex integration.

## [2026-09-30] D014 — Upgrade official Codex without bypassing proof gates

**Decision:** Use the latest official stable `@openai/codex` 0.159.2 package for the authorized global CLI upgrade and generate a separate session-local Codex MCP configuration. Do not replace Codex with a patched build, inspect or migrate owner auth/session state, widen host ACLs, or send a `gpt-6-luna` request until fixture-specific authentication and enforceable provider egress/cost controls are available.

**Alternatives considered:** Keep the installed 0.46.0 client; stage only the previous pinned compatibility fixture; patch/replace the new binary; reuse owner OAuth/API credentials; widen AppContainer access to solve `CODEX_HOME`; or issue a provider request without a trusted, budget-enforcing route.

**Reason:** The owner explicitly approved upgrading the official installed CLI, but preserved owner sessions/configuration and restricted host changes. The official 0.159.2 package provenance and model pricing were verified. The latest Codex AppContainer probe can run `--version` but `mcp list` fails while canonicalizing `CODEX_HOME` with access denied; no Codex read/revoke or model turn was proved. The official app-server documents a direct resource-read API, but that API did not reach the configured service in this fixture. The API price card does not enforce the per-job and rolling budget.

**Consequences:** Keep the installation pinned to the official 0.159.2 release/hash, preserve D010 as historical evidence, and keep S1-T7 `[~]`. A fixture-only API credential plus a trusted per-job/rolling egress and budget controller are still required for the authorized model-backed pair; an AppContainer-compatible `CODEX_HOME` path is also unresolved. No owner auth/session data was accessed or changed, and no model request was sent.

## [2026-09-30] D015 — Keep latest Codex compatibility and provider proof fixture-bound

**Decision:** For S1-T7, build the official Codex `rust-v0.159.2` source at `ff6aec96948b70d94983af2641a6b67c94faeff5` with only the authorized `CODEX_HOME` canonicalize-to-absolute fallback in a temporary fixture; keep the installed 0.159.2 binary untouched and use `deepseek-flash` only for a bounded synthetic-context proof if its configured provider, exact pricing, protocol, and enforceable budgets are verified.

**Alternatives considered:** Widen AppContainer access, patch/replace the installed client, use another model or provider alias, or send a request without verified provider pricing and trusted egress/budget enforcement.

**Reason:** The known startup failure is the upstream home-directory canonicalization path, while widening access would weaken the actual protected-runtime boundary. The owner explicitly approved a fixture-only fallback and temporarily selected `deepseek-flash` using only `OPENAI_BASE_URL` and `OPENAI_API_KEY` from the repository `.env`; other settings and profile data remain out of scope.

**Consequences:** Preserve source revision, exact patch, lockfile and executable hash provenance; do not change the installed binary. A model request remains fail-closed unless the exact configured provider/model pricing and a trusted controller enforce at most two jobs, $0.25/job, $5/rolling 30 days, 10k input and 2k output tokens per job. Credentials must stay outside the protected agent and all output.


## [2026-10-01] D016 — CR01: encrypted personal vault và scoped access tokens, không sandbox agents

**Decision:** Owner phê duyệt thay baseline sang vault mã hóa do user kiểm soát key, catalog công bố có chọn lọc và token ngẫu nhiên đổi một lần lấy phiên truy cập có phạm vi; Meta Brain không còn phụ trách cách ly OS của Codex/OMP hoặc bảo mật lại session nguồn mà agent đã thấy.

**Alternatives considered:** Giữ AppContainer/per-agent OS isolation; đưa key giải mã vault/zone cho agent; tạo gói chia sẻ mã hóa với key riêng. Chọn giải mã trong ứng dụng và trả nội dung đã duyệt, vì key của gói tải xuống không thể hết hạn/thu hồi và OS sandbox vượt threat model cá nhân owner cần.

**Reason:** Owner xác nhận bảo mật ở mức encrypted storage và kiểm soát chia sẻ là đủ. Probe thực tế cho thấy AppContainer cần fixture-only Codex compatibility build và supplemental native turn còn lỗi request-builder trước broker; đây là evidence về chi phí tương thích, không chứng minh provider/key lỗi hoặc mọi OS isolation bất khả thi. Thay đổi là scope/security tradeoff được owner chấp thuận, không phải làm pass một check cũ.

**Consequences:** CR01 thay R03–R06, R16, R18, R24, R31, R33–R36 và hợp đồng Sprint 1–8; R02, R07–R09, R11 và R29 giữ outcome nhưng làm rõ lock/catalog/consent/copies. Supersede yêu cầu isolation của D002, phần AppContainer/service-identity dependency của D007/D011 và đường protected-launch/provider proof D010/D014/D015; giữ C#/.NET 10, WPF, versioned named pipes, modular boundaries và frozen budgets D008. Key vault không vào agent argv/env/config/context/logs; token không là key giải mã. Scope là tập resource IDs/revisions được preview lúc duyệt, không quyền folder/descendant/future membership. Catalog riêng với private index; chỉ metadata owner công bố được agent thấy. Session/token scopes được enforce tại mọi API/cache; read-only mặc định, mutation chỉ khi owner cấp riêng; expiry/revoke chặn lần đọc tiếp, không làm agent quên.

**Threat-model limit:** Bảo vệ ciphertext/backup khi không có key và đường chia sẻ qua Meta Brain; không chống agent/malware cùng quyền OS owner lấy key/plaintext trong lúc mở khóa, đọc nguồn gốc hoặc điều khiển owner UI. Bearer token bị sao chép có thể dùng trong scope của nó; không claim OS-bound agent identity. Không lưu key plaintext cạnh vault; wrong/missing key, tampering và locked access fail closed. Key mất không có recovery thì dữ liệu không đọc được.

**Data and migration impact:** Canonical vẫn file-first nhưng encrypted at rest; readable/export chỉ qua owner unlock và explicit export. Private metadata, index/embeddings, journal/temp/cache, snapshots, policy/audit chứa nội dung nhạy cảm cũng phải được bảo vệ; công khai duy nhất catalog opt-in và envelope vận hành tối thiểu không nhạy cảm. Không migrate/xóa nguồn .codex/.omp hoặc reset dữ liệu để cutover. Product cutover phải migrate mọi caller và bỏ AppContainer binding/launcher/shims đã obsolete, nhưng giữ artifacts và gate record lịch sử.

**Status and verification impact:** Sprint 1 old-baseline fixture PASS được giữ làm lịch sử, không chứng minh CR01. M1/Sprint 1 reopen; S1-T1 operational targets/stack evidence giữ nguyên, các replacement security tasks Pending, Sprint 2–8 Not started. Supplemental protected deepseek-flash proof dừng/superseded, không pass; giữ unknown prior $0.0054 reservation, không reset budget hoặc suy diễn charge. Provider integration thật thuộc S6-T1 với consent/budgets mới được xác minh khi thực thi. Đây là documentation-only approval/change; không code, crypto/runtime/production proof hoặc owner daily-use acceptance mới.


## [2026-10-02] D017 — CR01: user-keyed authenticated vault and draining lock lifecycle

**Decision:** Implement the current owner-only encrypted vault with .NET 10 cryptographic primitives: a random 256-bit data key, AES-256-GCM resource/manifest envelopes, and PBKDF2-HMAC-SHA-256 with a fixed 600,000 iterations to wrap the data key separately under the owner passphrase and a random 256-bit recovery code. Resource ciphertext uses random nonces and opaque storage names; authenticated data binds vault ID, resource ID, schema version, and revision. Keep the manifest and private resource metadata encrypted. Start every service instance locked; an operation lease spans response serialization, lock denies new leases, drains active responses, zeros application-held data-key bytes, and drops the in-memory manifest reference.

**Alternatives considered:** Persist plaintext keys under ACLs, encrypt every file directly with the user passphrase, depend on an external crypto package, use only OS-account-bound key protection, or allow lock to return while a private response is still in flight.

**Reason:** Standard library cryptography avoids a new dependency while providing authenticated encryption and a separately wrapped random data key. A user-held recovery code permits explicit recovery without a fake key-loss path. The lease/drain boundary makes acknowledgement meaningful even when a private pipe response is backpressured. Explicit v1 migration can preserve configured resources and optional legacy policy state without touching runtime session sources.

**Consequences:** The owner must preserve the one-time recovery code outside the vault; losing both passphrase and recovery code makes ciphertext unrecoverable. The service's v1-to-v2 migration runs only when the owner service is stopped, rewrites minimal settings, encrypts only configured managed resources plus optional legacy policy state, and removes only those exact legacy copies after cutover. Wrong/missing credentials and tampered/swapped/truncated ciphertext fail closed. Key-byte zeroing and manifest-reference release do not guarantee erasure of immutable .NET strings, garbage-collected copies, pagefile/dumps, or deleted media; a process with the same OS owner rights can observe plaintext while unlocked. This implementation does not add tokens, approvals, agent sharing, S3 ingestion, or S6 jobs. S1-T2 remains pending evidence review and the Main-owned commit checkpoint.

## [2026-10-02] D018 — Encrypt owner-approved scope snapshots and persist token verifiers

**Decision:** Store owner-controlled collection-to-resource-ID memberships plus each approved grant's concrete resource/revision snapshot, allowed operations, destination snapshot, expiry, provider/model/egress limit, grant generation, and SHA-256 verifier of a random 256-bit bearer in the schema-v2 AES-GCM `scope-grants.enc` envelope inside the existing vault. Resolve zone, collection, or exact-ID selectors to immutable concrete records; require explicit owner CLI confirmations for membership changes and issuance; hand the raw token once through an owner/SYSTEM/Administrators-protected local file.

**Alternatives considered:** Keep the prior plaintext grant-policy snapshot beside service settings, persist raw bearer tokens for direct lookup, add a separate database/journal, or expose the token on command arguments/terminal output.

**Reason:** CR01 requires private grant metadata encrypted at rest and a bearer independent of the vault key. Reusing the already unlocked vault data key and AEAD implementation avoids another secret or package; a hash verifier is sufficient for a high-entropy random bearer, and a protected handoff file avoids placing it in process arguments or logs. The current resource manifest has no collection contract, so T3 adds only an owner-managed encrypted ID membership list (no hierarchy, discovery, or publication); every collection selector is resolved to current concrete resource IDs/revisions before preview.

**Consequences:** Grant state and owner collection membership can be read or changed only while the vault is unlocked; missing state means no grants or collections, while malformed, unauthenticated, or inaccessible state fails closed. Membership updates advance policy generation and invalidate pending previews but never mutate issued scope snapshots. Token loss before handoff cannot be recovered from persisted state. Redemption/session creation, token consumption, revocation, and publication remain later task responsibilities; downstream consumers must honor frozen revisions, operations, destination scope, egress context, and generation.

## [2026-10-02] D019 — Make redemption durable and sessions epoch-bound

**Decision:** Persist consumed grant verifiers atomically in the encrypted schema-v3 scope state before returning an agent session, while keeping sessions process-local and tied to the owner-approved frozen snapshot and persisted unlock epoch.

**Alternatives considered:** Persist session bearers or long-lived sessions on disk; disclose the session before consuming the grant; advance policy generation on every grant issuance; or retain schema-v2 grant generations without rebasing them.

**Reason:** A crash after durable consumption but before the response must not resurrect a bearer or disclose a second session, and storing only a verifier avoids persisting raw bearer material. Process-local sessions disappear on lock/process exit; the epoch rejects outstanding grants and sessions after lock/unlock without needing session persistence. Grant issuance is not a policy mutation, so it must not invalidate another explicitly approved grant; collection membership changes remain the policy-generation boundary. Rebasing existing v2 grant generations during authenticated migration preserves their approved snapshots under the v3 semantics.

**Consequences:** Every consume and unlock-epoch transition depends on an atomic encrypted-state replacement that fails closed if it cannot commit; service restart requires re-redemption, and old sessions cannot be restored. Schema-v2 authenticated data and grant records require an explicit schema-v3 migration path. Agent resource retrieval, MCP/client integration, and OS-bound agent identity remain separate concerns.

## [2026-10-04] D020 — Keep S1-T5 access authorization decision-only and fail-closed for egress

**Decision:** Keep one Core-owned memory-only session authority. The owner control channel may list active session snapshots and selectively revoke one session; the agent channel may request an authorization decision only for its exact frozen operation, source/destination resource IDs and revisions, expiry, current policy generation, and unlock epoch. The authorization operation does not retrieve, decrypt, or return managed content. Check the exact provider/model and reject cost estimates above $0.25/job or token estimates above 10,000 input/2,000 output tokens, then deny agent-supplied egress until a trusted provider adapter/pricing context exists.

**Alternatives considered:** Treat agent-supplied provider/model and price estimates as trusted; perform provider calls from the access-check endpoint; or combine this task's authorization response with S1-T6 content retrieval.

**Reason:** The existing service has no trusted provider call/pricing adapter, and owner approval must remain authoritative rather than being inferred from agent IPC fields. A decision-only operation preserves the single policy authority while enabling real IPC lifecycle checks without returning plaintext or claiming a downstream consumer.

**Consequences:** Policy-generation changes invalidate live sessions; lock and process restart clear process-local sessions, while unlock epochs supersede old grants. Selective session revocation is not durable grant revocation. Agent egress remains denied even for exact, in-cap synthetic requests; no provider/model call or $5/30-day rolling-spend ledger is implemented. Managed-resource retrieval/decryption remains a separate downstream task.

## [2026-10-05] D021 — Serve scoped reads through authorize-decrypt-reauthorize on the agent channel

**Decision:** Serve `agent.resource.read` on the agent pipe by authorizing the exact frozen operation/ID/revision against current epoch/generation, decrypting the vault bytes, re-authorizing against fresh epoch/generation, re-validating the live registration/revision, then returning content; every other outcome uses one generic `resource_unavailable` denial. Keep `resource.read` and `source.read` as distinct grantable operations so a readable summary cannot expand into an ungranted source endpoint.

**Alternatives considered:** Single-check-then-read without the serve-boundary recheck; a second canonical content store or S2 search/catalog framework inside T6; inheriting A1's revision-threaded store API without the serve binding; trusting agent-supplied paths or extra zone/body fields as scope.

**Reason:** T5 sessions already freeze operation/ID/revision with epoch/generation invalidation, and the S1 vault binds ciphertext to resource/schema/revision; the double check plus registration/revision validation closes TOCTOU between approval and serve without a second policy, while the generic denial and opaque-ID-only resolution close existence oracles and path injection. `source.read` separation preserves the R06 catalog/body boundary for S2–S4 reuse.

**Consequences:** Warm reads and cached credentials deny after revoke/expiry/generation/lock; restart plus unlock epoch retires old sessions and pending tokens. Stale revisions fail closed instead of serving newer bytes under an older grant. MCP/product-client integration, provider egress, and timing-channel hardening remain downstream/out of scope.

## [2026-10-06] D022 — Serve catalog discovery from a plaintext projection beside the encrypted private mapping

**Decision:** Keep one Core-owned owner catalog authority with an encrypted `owner-catalog.enc` private catalog-ID to resource mapping inside the vault, and serve `catalog.list`/`catalog.query` on both pipes from a plaintext `published-catalog.json` projection that holds only exact owner-approved ID/label/description triplets. Owner `catalog-preview`/`catalog-publish`/`catalog-list`/`catalog-withdraw` require unlock plus explicit confirmation; publication rewrites the projection atomically after the private-mapping commit, withdrawal deletes the record so future serve boundaries converge to generic `no_match`. T8 resolves catalog IDs by revalidating the stored mapping against the live manifest at request time.

**Alternatives considered:** Serving discovery from the encrypted store (would require unlock/key and break locked/startup discovery); a second canonical content/catalog store or S2/S4 search framework inside T7; deriving labels/descriptions from private titles/bodies/paths; trusting agent-supplied owner/identity/bearer fields as publication authority.

**Reason:** The vault must stay locked by default while approved discovery stays available, and the private resource mapping must never leave encryption. A rebuildable plaintext projection of exact owner text preserves the R06 private-index/catalog boundary without a second truth store, while explicit owner text plus live-revision revalidation keeps publication honest for T8 request approval without minting access.

**Consequences:** Locked/startup/restart discovery works with no key or decrypt; malformed projections and tampered private mappings fail closed; withdrawn metadata converges on fresh list/query/restart with no resurrection (already delivered copies cannot be recalled); catalog entries never grant body/source reads. MCP/product-client integration, access requests/approval, provider egress, and timing-channel hardening remain downstream/out of scope.

## [2026-10-06] D023 — Approve access requests through narrow-only owner previews bound to the existing token authority

**Decision:** Keep access-request state inside the existing encrypted schema-v4 `scope-grants.enc` envelope, accept only live published catalog IDs with purpose plus declared agent/provider/model/disclosure context over the agent channel, return only an opaque receipt/status to agents, and let owners list/preview/narrow/approve/reject through explicit confirmations that bind the shown exact resource/revision/body/scope/disclosure snapshot before minting through the existing T3 single-use token authority.
**Alternatives considered:** A separate request database or plaintext pending queue; trusting agent-supplied resource IDs, revisions, operations, or owner-confirmation flags as approval; allowing the owner approval to enlarge scope beyond the preview; minting a second bearer outside the T3 verifier/epoch/generation lifecycle.
**Reason:** CR01 requires pending requests to stay encrypted and lock-safe without a second truth store, while approval must remain an explicit owner act on exact bytes rather than an inference from agent reason, identity, or same-owner transport. Reusing the vault envelope plus T3 preview/generation/epoch checks preserves atomic approve-and-mint, stale-preview fail-closed behavior, and T4/T5 redemption lifecycle without a parallel grant path.
**Consequences:** Unknown/guessed/private IDs fail closed with one generic code and no existence oracle; narrowed or excluded resources deny; stale catalog/revision/registration/collection/epoch/generation changes require a fresh preview; rejected requests mint nothing and repeat/concurrent approvals cannot duplicate grants; provider egress still enforces exact declared context within the frozen per-job ceilings at the returned-resource boundary.

## [2026-10-06] D024 — Keep owner request previews in memory on the owner console, never as plaintext files
 
 **Decision:** Show the exact request body on the owner console only during `owner request-preview`, write no `.preview.bin` file, accept no `--output-dir`, and keep the approval binding on the in-memory 5-minute preview snapshot cleared at lock. Withdrawn catalog IDs and unregistered resources invalidate the held preview at approve time, and republication never revives it.
 **Alternatives considered:** Keeping the operator-chosen output directory and calling it consent; encrypting preview files or deleting them after approval; omitting the body from preview and approving from metadata alone.
 **Reason:** An output directory is not disclosure consent, and cleanup-after-write still leaves indefinite plaintext outside the vault across lock/restart. Owner review stays useful on the console the owner already trusts for reads, while approve-time revalidation keeps every catalog/revision/registration/collection change fail-closed.
 **Consequences:** Redirected preview output is refused fail-closed (`preview_redirect_denied`) before any private body is requested or emitted; the smoke suite asserts the redirected denial plus zero leaked markers/files and covers revision, collection-membership, withdrawal, and withdrawal-then-republication staleness with fresh-preview recovery.

## [2026-10-06] D025 — Refuse redirected owner request previews and prove the attached console positively

**Decision:** Check `Console.IsOutputRedirected` at the top of `owner request-preview` and refuse redirected output fail-closed (`preview_redirect_denied` on stderr, exit 3) before requesting or emitting any private body; keep the existing in-memory exact-body console preview only on an attached owner console with buffer zeroization. Prove the positive surface with an owned isolated console screen buffer in the `s1-t8` smoke (spawn with console handles, scrape and match exact bodies/scope/disclosure in test memory, parse preview IDs from the scrape), not with redirected stdout capture. Rejected: the A2 warning-only assumption that a banner plus treating a redirect as an owner-held copy satisfies R04/R33/R36.

**Alternatives considered:** Explicit interactive export consent on redirected streams; a test-only bypass flag; omitting bodies from preview; a ConPTY harness (evaluated and abandoned after discriminating evidence showed child processes run but zero bytes reach the parent pipe in this sandbox across flag/handle/reader variants).

**Reason:** Ordinary preview is visual review before approval, not scoped export; R04/R33/R36 require private data outside explicit owner-authorized export to stay encrypted at rest. The guard runs before the preview IPC so no plaintext is fetched when output cannot be shown safely. The smoke keeps all prior stale/group/catalog negatives intact while adding a real-console positive that exercises the actual product emission path.

**Consequences:** `owner request-preview` under any redirected stdout fails closed with no bodies, no files, and no preview fetched; only attached-console runs show exact bodies and feed preview IDs into narrow approve/redeem/read checks. ConPTY remains unevaluated as a future alternative; the current positive proof is the isolated screen-buffer harness.
 
 ## [2026-10-06] D026 — Serve the agent channel through a vault-blind MCP stdio bridge with file-path handoffs
 
 **Decision:** Add `agent-mcp --agent-pipe <name>` (MCP 2025-11-25 stdio `initialize`/`tools/list`/`tools/call` over stdin/stdout) plus `owner agent-catalog-list`, exposing only the existing agent-channel operations — catalog discovery, access request/status, handoff-path redemption, session inspection, scoped reads — as subprocess owner-CLI calls. The bridge holds no vault/key/ciphertext, mints no grants, performs no owner approval, rejects raw bearer material in tool arguments (`bridge_raw_bearer_denied`), and redacts bearer-shaped substrings in tool output; token/session handoffs cross only as DPAPI-protected file paths resolved locally by the owner CLI. Interactive stdin confirms (ISSUE/SET/PUBLISH/APPROVE/REJECT) and the attached-console preview guard stay unchanged; no `--credential-file` or `--confirm` bypass is introduced.
 **Alternatives considered:** Credential-file unlock and `--confirm` headless flags (rejected: they weaken explicit owner consent and risk plaintext key persistence beside the vault); a second grant path inside the bridge (rejected: duplicates the T3/T8 authority); raw token/session bearer tool inputs (rejected: puts key/token material into model context/args/logs).
 **Reason:** Official Codex/OMP clients speak MCP stdio and need one policy (Core/Application) with a bridge that cannot vault/key/mint, while headless owner unlock/approve must keep the T8 explicit-consent and console-preview guarantees. Subprocess reuse of the exact owner CLI preserves channel guards, DPAPI handoff ACLs, and bearer-free outputs without a parallel implementation.
 **Consequences:** The bridge is client plumbing, not client proof: official Codex/OMP model-driven walkthroughs (catalog → request → approve → redeem → granted read → revoke/lock → denied through the same client, two scopes, all negatives) remain Pending behind separate owner consent for model turns with trusted pricing inside the frozen per-job $0.25 / 10k-input / 2k-output and $5/30-day ceilings; unknown cost denies. Synthetic approved bodies entering a model turn would enter that turn's context plus client-retained session, which is disclosed, never claimed as no-logs.


## [2026-10-06] D027 — Prove scoped sharing through official Codex/OMP model turns under a one-time synthetic walkthrough consent

**Decision:** Run the S1-T9 same-client walkthroughs as approved bounded model turns (Codex `gpt-6-luna`, OMP `openai-codex/gpt-6-luna`, at most 6 single-purpose turns per client) against the vault-blind bridge, with owner unlock/preview/narrow-approve/mint kept in explicit operator CLI steps, DPAPI file-path handoffs only, and raw bearer material denied at the bridge boundary. Register the bridge transiently per turn (Codex session-only `-c mcp_servers.metabrain.*` overrides; OMP transient project `.omp/mcp.json` removed after the run); never mutate installed client profiles.
**Alternatives considered:** Permanent MCP registration in installed profiles (rejected: mutates owner daily-use client state); model-performed owner approve/mint (rejected: approval must stay an explicit operator act, never model output); raw token/session bearers as tool arguments (rejected: puts key/token material into model context/args/logs); reusing the helper stdio proof as client proof (rejected: helper tautology, not official-client evidence).
**Reason:** T9 acceptance requires real Codex/OMP tool-call events through the same client (catalog, request, redeem, granted read, revoke-deny, lock-deny, negatives), which cannot be produced without model turns; the owner accepted cost uncertainty and absent client-side hard caps for this synthetic proof only, with input kept small and isolated per turn.
**Consequences:** Both official clients demonstrably complete catalog-to-denial chains on two scopes with all negatives failing closed; approved SYNTHETIC bodies entered model context and client transcripts per the consent, while keys/raw bearers never did. Product/S6 budgets, privacy policy, and the no-hard-cap baseline are unchanged; the one-time exception covers only this walkthrough. A harness-side owner-channel wedge observed mid-walkthrough (owner `transport_unavailable` while the agent channel served, recovered by restart) is recorded as an open diagnostic finding, not product proof.

## [2026-10-07] D028 — Deliver approved scoped bodies in memory over MCP with no default plaintext file

**Decision:** Return the exact approved body in the `metabrain_scoped_read` MCP tool result (strict-UTF8 text, else a lossless base64 blob) and stream it framed base64 on `owner agent-read` stdout; remove `--output-file` from the agent read path and assert no default `*.bin`/`*.read*` resource plaintext files, while the owner `read --output-file` export stays an explicit owner act.
**Alternatives considered:** Keeping the byte-count reply plus harness file-match as client proof (rejected: the corrected FAIL review proved clients never received bodies); raw-byte stdout streaming (rejected: text-capturing callers corrupt binary); a parallel explicit-export flag on the agent path (rejected: unrequested framework, second truth store).
**Reason:** Ordinary agent retrieval is consumption under existing Application policy, not an owner-disclosed permanent export; R04/R33/R34/R36 require private data outside explicit owner export to stay encrypted at rest, and T9 acceptance requires the client itself to receive and consume the body.
**Consequences:** Both official clients quote scope-unique synthetic facts from their own tool results; revoked/locked/unknown/unselected/source/body-spoof/owner-op reads deny with `resource_unavailable` and zero body bytes; binary bodies stay readable as base64 instead of denied.