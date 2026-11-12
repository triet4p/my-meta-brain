# Meta Brain — Định hướng sau 1.0.0

- Ngày: 2026-10-29.
- Trạng thái: **Định hướng; CLI-first support contract đã owner-approved qua CR03 ngày 2026-11-01, phần chưa chốt vẫn cần approval trước implementation.**
- Baseline phát hành hiện tại: [PLAN](PLAN.md), [Sprint 11](sprint-plans/sprint-11.md), [release notes 1.1.0](release-notes-1.1.0.md); 1.0.0 giữ lịch sử.
- Hợp đồng hiện hành: [đặc tả](META-BRAIN.md), [R01–R40/CR01](REQUIREMENTS.md), [decision log](../.agents/memory/decisions.md). Tài liệu này không tự supersede các hợp đồng đó.
- Cập nhật 2026-11-01: Sprint 11/1.1.0 đã published; CR02 folder/partial parsing đã approved/implemented. Owner-approved [CR03](REQUIREMENTS.md#approved-change-record--cr03-2026-11-01)/D089 chốt CLI + MCP từ Sprint 12 và optional browser document preview/edit từ Sprint 13. Owner đã approve [D090](META-BRAIN.md#134-approved-workspace-contract--d090) cho storage/key/metadata và installed transition; share/engine/migration còn mở. Approval không chứng minh implementation.

## 1. Vấn đề và mục tiêu

Owner phản hồi sau khi sử dụng 1.0.0:

- Các tính năng nhìn chung hoạt động ổn, nhưng sản phẩm quá khó sử dụng.
- Enrollment/cấp quyền theo từng file gây nhiều thao tác; chưa đáp ứng cách làm việc theo folder.
- Nhập folder JSONL OMP gặp lỗi schema mismatch so với dữ liệu đã test; định dạng upstream tiếp tục thay đổi.
- Owner khó nhìn thấy và theo dõi tường minh tri thức của mình; trải nghiệm nghiêng về quản trị một kho bị khóa hơn là sử dụng bộ nhớ.

Đây là phản hồi sử dụng của owner, không phải kết quả tái hiện mới. Không chạy lại các flows chỉ để xác nhận lại phản hồi hoặc nghiệm thu 1.0.0.

**Mục tiêu:** chuyển trọng tâm sang một workspace tri thức file-first, được mã hóa khi lưu, dễ duyệt và lấy lại nguyên bản; bổ sung bộ nhớ phân loại, quan hệ và recall để giúp owner và LLM tìm đúng bối cảnh.

Không đánh đổi provenance hoặc khả năng sửa sai để lấy sự đơn giản. Giảm gánh nặng thao tác security, không đồng nghĩa bỏ mã hóa hay cho phép egress ngầm.

## 2. Hướng đã thống nhất và phần chưa chốt

### Hướng sản phẩm đã thống nhất trong trao đổi

- File/folder là nền tổ chức dễ hiểu; một vùng tri thức có thể là một subfolder.
- File được lưu mã hóa. Khi mở bằng Meta Brain với key hợp lệ, nội dung được giải mã và xử lý theo định dạng ban đầu; không mặc định lưu plaintext.
- Owner có thể chủ động export lại định dạng gốc sau khi mở khóa hợp lệ.
- File là đơn vị tài liệu độc lập, có thể liên kết với file và folder khác; trong một file có thể có nhiều đơn vị trí nhớ.
- Search cần kết hợp nội dung, cấu trúc, phạm vi, thời gian và quan hệ; phục vụ cả điều hướng của owner lẫn context cho LLM.
- Giữ ý tưởng bộ nhớ phân loại ban đầu, không thu hẹp Meta Brain thành file explorer có semantic search.
- Ưu tiên hiệu năng và CLI owner-complete, optional browser chỉ đọc/chỉnh tài liệu; tránh buộc owner hiểu resource ID, grant snapshot hoặc index internals để dùng tri thức của chính mình.

### Đề xuất cần xác nhận trước implementation

- Workspace storage/key, identity, metadata, export và installed transition đã chốt qua [D090](META-BRAIN.md#134-approved-workspace-contract--d090); implementation và verification còn thuộc S12.
- Semantics chia sẻ theo folder, nhất là file/revision mới sau khi owner cấp quyền.
- Embedding model, search/index libraries và cách triển khai relation engine; không còn mở lựa chọn tiếp tục WPF/full web console sau CR03.
- CR02 đã chốt và triển khai folder/partial parsing ở 1.1.0; source text indexing và editor support mới vẫn theo sprint tương ứng, không suy preservation thành đã hiểu.

[Roadmap tới 2.0.0](ROADMAP-2.0.md): Sprint 11/1.1.0, 12/1.2.0, 13/1.3.0, 14/1.4.0 và 15/1.5.0 Complete/published; Sprint 16–18/G2 Planned. Mỗi sprint có release 1.x.0 rồi G2 major; CR03 đổi từ ba products sang CLI installer + MCP ZIP từ 1.2.0, browser assets bundle từ 1.3.0. Không deadline, performance threshold hoặc implementation authorization mới.

## 3. Mô hình workspace file-first

### File và folder là nền, không phải toàn bộ trí nhớ

| Thành phần | Vai trò định hướng |
| --- | --- |
| Workspace / vùng tri thức | Owner chọn nơi tổ chức, mở khóa và quản lý dữ liệu |
| Folder / subfolder | Tổ chức và khoanh vùng; duyệt, tìm và chọn nguồn theo vùng |
| File gốc | Giữ nội dung và định dạng, có thể mở/export mà không phụ thuộc một schema memory item duy nhất |
| Section / record / passage | Đơn vị truy hồi chi tiết bên trong file; không bắt owner quản lý chunk |
| Metadata và trí nhớ có cấu trúc | Phân loại, attribution, thời gian, diễn giải và quan hệ có provenance |
| Index dẫn xuất | Tăng tốc tìm kiếm; có thể dựng lại, không là nguồn sự thật thứ hai |

Một file Markdown dài có thể chứa kiến thức, trải nghiệm, kết quả đo, quyết định và dự định. Không ép toàn bộ file vào một loại trí nhớ duy nhất.

Identity sống qua rename/move; D090 chốt copy, replacement, xung đột và import là bản sao độc lập, không sync chỉnh sửa ngoài ứng dụng.

Owner có thể duyệt theo cây folder thật và theo các góc nhìn logic. Một tài liệu xuất hiện ở nhiều góc nhìn không có nghĩa nhân bản file gốc.

### Bảo toàn nguyên bản

- Đọc lại và export nguyên bản chưa chỉnh sửa phải bảo toàn bytes, encoding và định dạng, không chỉ dựng lại văn bản tương đương.
- Nếu owner chỉnh sửa nội dung, phân biệt revision mới với bản gốc; không gọi bản đã normalize hoặc rewrite là nguyên bản.
- Với định dạng chưa hỗ trợ editor, phải nêu giới hạn preview/chỉnh sửa, không giả vờ đã hiểu nội dung.
- Tên file, folder và metadata nhạy cảm được mã hóa theo D090; ciphertext length/count/timing/access và minimal envelope vẫn có thể lộ.

## 4. Mã hóa ở nền, kiểm soát chia sẻ tại ranh giới

Mong muốn của owner là giảm độ khó của security workflow, **không phải chuyển sang plaintext storage**.

- Key hợp lệ cho phép Meta Brain giải mã nội dung cần dùng. Unlock không bắt buộc giải mã toàn workspace.
- Mở trong Meta Brain không mặc định tạo file plaintext tạm. Export là hành động owner tường minh, có đích đến rõ ràng.
- Mở bằng editor bên ngoài là một boundary riêng: autosave/cache/bản sao của công cụ ngoài không nằm hoàn toàn trong kiểm soát Meta Brain.
- D090 reuse key và lifecycle vault hiện có; không hứa managed-memory erasure tuyệt đối hoặc bảo vệ trước same-account OS compromise.
- Agent không nhận master key/decryption key. Bản plaintext đã export/chia sẻ không thể được thu hồi bằng lock hoặc revoke.

“Bảo vệ output” phải bao phủ **mọi nơi dữ liệu rời workspace**: agent retrieval, context gửi provider/model, export và publication. Gửi dữ liệu vào cloud model đã là egress trước khi có output cuối.

Owner sử dụng nội dung của chính mình không nên phải thực hiện lại quy trình catalog/request/token. Agent vẫn cần quyền được owner hiểu và kiểm soát; cơ chế cụ thể sẽ được review theo change control.

### Chia sẻ theo folder

Đơn vị thao tác nên hỗ trợ folder/vùng tri thức, không lặp approval cho từng file. Cần phân biệt hai semantics trước khi chọn:

- **Snapshot:** tập file/revision tại thời điểm duyệt; nội dung mới không tự được thêm.
- **Theo vùng liên tục:** file/revision mới trong vùng có thể vào phạm vi theo consent đã nêu rõ.

Đây là lựa chọn còn mở. Không lặng lẽ đổi frozen scope 1.0.0 thành quyền động. Quan hệ, membership hoặc traversal không tự cấp quyền sang vùng khác.

## 5. Bộ nhớ phân loại: giữ chiều sâu của ý tưởng ban đầu

### Nguồn tham khảo và giới hạn

- [CogMem](https://github.com/triet4p/cogmem): README mô tả sáu memory networks World, Experience, Opinion, Habit, Intention, Action–Effect và truy hồi kết hợp semantic, lexical, graph, temporal theo loại query.
- [MemPalace](https://github.com/mempalace/mempalace): README mô tả bảo toàn nội dung nguyên văn và tổ chức index theo wings, rooms, drawers để search có phạm vi.

Chỉ dùng làm tham khảo ý tưởng. Chưa audit source, kiểm chứng benchmark hoặc quyết định tái sử dụng dependencies/code của hai repo. Không sao chép claim hiệu quả thành acceptance của Meta Brain.

### Hai trục bổ sung nhau

**Không gian tri thức trả lời “nằm ở đâu”; loại trí nhớ trả lời “có ý nghĩa gì đối với owner”.** Không ép folder taxonomy thành cognitive taxonomy.

| Loại trí nhớ định hướng | Giá trị khi recall | Điều cần tránh |
| --- | --- | --- |
| World / hiểu biết | Kiến thức, claims, nguồn và mức bằng chứng | Gắn nhãn mọi nội dung nguồn là fact đã xác thực |
| Experience / trải nghiệm | Đã gặp gì, trong hoàn cảnh nào, đã thử gì | Biến lời assistant thành trải nghiệm của owner |
| Opinion / quan điểm | Owner từng nghĩ hoặc lựa chọn gì, đã thay đổi ra sao | Biến AI proposal thành belief của owner |
| Habit / thói quen | Cách làm được khai báo hoặc mẫu cần owner xem xét | Suy thói quen từ một lần nhắc |
| Intention / dự định | Muốn làm gì, còn mở hay đã hoàn thành | Coi đã nhắc/đã nhắc lại là đã làm xong |
| Action–Effect / hành động–kết quả | Cách làm, điều kiện và kết quả quan sát được | Suy quan hệ nhân quả chắc chắn từ tương quan |

Decision, lesson, question và synthesis vẫn giữ như kind độc lập, không bị ép thành một trong sáu nhãn. Owner đã duyệt taxonomy facets và mapping trong S14-T1; xem [contract](META-BRAIN.md#24-cognitive-facets-s14-t1). Lưu/review annotation và truy vấn facets chưa được cung cấp bởi T1.

### Phân loại là trợ giúp, không phải biểu mẫu bắt buộc

- Một đoạn có thể có nhiều nhãn và xuất hiện trong nhiều góc nhìn; không cần copy file.
- Owner có thể xem, sửa hoặc bác phân loại/quan hệ; AI đề xuất phải phân biệt với owner xác nhận.
- Giữ riêng author, executor, lời nguồn báo cáo, quan sát và suy luận.
- Nội dung gốc vẫn dùng được trước khi có model hoặc trước khi structuring hoàn tất; không yêu cầu extraction cho mọi token.
- Phân loại hoặc ghi chú owner đã chỉnh là dữ liệu bền vững, không chỉ cache. Kết quả tự sinh có thể dựng lại phải phân biệt với quyết định owner đã giữ.

## 6. Search và quan hệ

### Các lớp retrieval bổ sung nhau

| Lớp | Nhu cầu |
| --- | --- |
| Tên file, đường dẫn, heading | Tìm tài liệu và vùng tri thức quen thuộc |
| Lexical / full-text | Thuật ngữ, mã lỗi, symbol, trích dẫn chính xác |
| Cấu trúc và metadata | Folder, định dạng, loại trí nhớ, thời gian và trạng thái |
| Semantic | Cùng ý nhưng khác cách diễn đạt, nhu cầu Việt–Anh |
| Quan hệ | Nguồn bằng chứng, phản biện, quyết định trước/sau và điều kiện áp dụng |

Kết quả trả về file kèm đoạn/vị trí khớp và lý do được chọn. CSV cần ngữ cảnh tên cột; Markdown cần heading; JSONL chỉ dùng attribution đã hiểu và có bằng chứng. Không bắt owner xem một kho chunk rời rạc.

Semantic retrieval bổ sung lexical, không thay thế exact match. Embedding/model/index mechanism chưa chọn; không mặc định vector DB hoặc graph database.

### Quan hệ có nghĩa và có nguồn gốc

Phân biệt membership file–folder, link có sẵn, quan hệ ngữ nghĩa explicit và gợi ý tương đồng. Những kind như related_to, supports, contradicts, supersedes, applies_to và derived_from cần semantics rõ, không gom thành một loại “liên quan”.

- Similarity không đồng nghĩa support; related_to không tự thành bằng chứng.
- Quan hệ ghi nguồn tạo: owner, link từ file hoặc AI đề xuất; giữ lịch sử sửa/bác.
- Quan hệ file–folder có thể biểu đạt phạm vi áp dụng, nhưng không tự chứng minh nội dung của mọi file trong folder.
- Evidence link cần phát hiện nguồn thay đổi/mất và nêu trạng thái cần xem lại.
- Quan hệ do owner tạo phải được backup/export và giữ qua index rebuild.

### Recall theo mục đích

| Câu hỏi | Hướng recall |
| --- | --- |
| “Tôi từng gặp lỗi này chưa?” | Trải nghiệm, triệu chứng, cách xử lý, kết quả |
| “Vì sao tôi chọn phương án này?” | Quyết định, lý do, bằng chứng, phương án đã bỏ và thời gian |
| “Tôi còn định làm gì?” | Dự định và lifecycle, không tự đóng việc |
| “Cách này có hiệu quả không?” | Hành động–kết quả, điều kiện, cả trường hợp thất bại |
| “Tôi biết gì về chủ đề này?” | Kiến thức, nguồn, mâu thuẫn và phần chưa chắc chắn |

Pipeline định hướng: khoanh vùng được đọc → tìm seed files/passages → mở rộng theo quan hệ phù hợp → rank lại và loại trùng → đóng gói theo ngân sách context. Giới hạn số bước, số ứng viên và lượng nội dung phải được thiết kế/đo; không quét toàn graph hoặc đưa cả workspace vào LLM.

Routing có thể bắt đầu bằng filters và quy tắc đơn giản; không mặc định gọi LLM cho mọi query. Folder cùng vị trí không có nghĩa mọi file đều liên quan như nhau.

## 7. Hai trải nghiệm trên cùng nền tri thức

### Owner: tìm về vùng tri thức của mình

- Duyệt cây folder và mở đúng vị trí trong file.
- Chuyển sang góc nhìn trải nghiệm, quyết định, dự định hoặc chủ đề mà không phải di chuyển dữ liệu.
- Tìm một vùng kiến thức, xem các file nổi bật và lý do vùng đó phù hợp.
- Inspect nguồn, quan hệ, lịch sử và điểm cần review qua CLI shared APIs; optional `open` chỉ trình bày tài liệu đã chọn, không relation/review controls hoặc graph canvas.
- Theo dõi rõ file nào đã lưu, đã index, diễn giải một phần, chưa hỗ trợ hoặc lỗi; không đồng nhất “đã nhập” với “đã hiểu”.

### LLM: nhận gói context có bằng chứng

Context nên có đoạn liên quan, file identity/revision/digest và vị trí citation; nêu lý do chọn, trạng thái hiện hành/lịch sử, mâu thuẫn và khoảng trống quan trọng. Trích nguyên văn phải tách khỏi tóm tắt do engine tạo.

LLM có thể yêu cầu mở rộng nguồn cụ thể trong quyền đã duyệt. Nội dung file là dữ liệu, không phải instruction có thể tự cấp quyền hoặc thay policy. Không hứa context “tốt nhất” cho mọi câu hỏi; đánh giá bằng relevance, đủ bằng chứng, ít nhiễu và truy ngược nguồn được.

## 8. Ingestion chịu được thay đổi upstream

Tách hai khả năng: **giữ/đọc lại nguyên bản** và **diễn giải có cấu trúc**.

- Với nguồn owner đã chọn/cho phép, bản gốc không nên mất khả năng lưu hoặc đọc lại chỉ vì một record chưa được adapter hiểu.
- Adapter phải báo rõ record/shape nào chưa hỗ trợ và vị trí tương ứng; không nhận sai role/attribution, không silent drop.
- Trạng thái theo CR02/1.1: bốn facets `availability`, `preservation`, `interpretation`, `derivation` độc lập; preserved không đồng nghĩa đã hiểu/index. Các trạng thái này không tự thành search evidence.
- Folder enrollment/refresh phải có phạm vi rõ, không mở rộng thành scan toàn profile hoặc sửa original sessions.
- Append, file dở dang, schema mới, encoding lỗi và file bị thay thế cần có hành vi quan sát được, không biến mọi lỗi thành một thông báo “schema mismatch” không có hướng xử lý.

CR02/D088 đã giải quyết thay đổi parser/folder contract trong Sprint 11 và shipped 1.1.0: bốn facets độc lập, encrypted exact-byte preservation atomic cùng checkpoint trước continuation, version/root barrier, không lossy transcode hoặc silent drop. Xem specification §8.1; không dùng tolerant parsing để giả record chưa hiểu là hợp lệ.

## 9. Hiệu năng, giao diện và stack

Đề xuất giữ C#/.NET cho core/application, crypto, storage và các phần có thể tái sử dụng. Hướng file-first không tự tạo lý do viết lại backend bằng ngôn ngữ khác. Chưa có benchmark kiến trúc mới để khẳng định hiệu năng.

- Unlock không materialize toàn workspace; đọc/giải mã theo nhu cầu.
- Xử lý file lớn theo streaming/incremental; preview có giới hạn, paging và virtualization phù hợp.
- Dùng index dẫn xuất, tránh parse lại toàn bộ file cho từng query.
- Background work không chặn CLI/document rendering; đổi query hoặc đóng document session hủy tác vụ không còn cần.
- Giới hạn cache và lifetime plaintext; không dùng hiệu năng làm lý do ghi plaintext scratch ra đĩa ngầm.

**CR03/D089 đã chọn CLI-first, không tiếp tục WPF hoặc chuyển full console sang web/desktop shell.** Browser chỉ là document preview/text editor do foreground CLI `open` mở qua local bridge; không owner authority/key, không unlock/review/sharing/settings workflows. Chọn renderer/editor dependency và bridge authentication bằng runnable evidence ở S13-T1, không mở lại desktop framework contest.

Không tạo HTML chứa plaintext xuống disk: serve document trong phiên giới hạn document/revision/operations/lifetime, không app-managed browser cache/storage/service worker, active document script/HTML hoặc remote assets. Markdown formatted preview, CSV table preview và raw-text edit trước; save explicit expected revision, preview/toggle giữ nguyên bytes. Lock/disconnect/expiry/CLI exit revoke/clear; không claim absolute browser/OS/extension erasure hoặc thay host policy. Plaintext export là act riêng.

Lát cắt đánh giá: CLI browse/search/read/revise hoàn toàn không browser → optional open Markdown/CSV/text → preview/edit/save/conflict → lock/exit. Đo startup/unlock, search/open latency, CLI/service/browser RAM, document responsiveness khi indexing và lifecycle negatives theo corpus/budgets được duyệt. Source JSONL sessions read-only; unsupported format có limits thật, không mọi file đều editable.

## 10. Các chặng định hướng và liên kết kế hoạch

| Chặng | Outcome cần có trước khi đi tiếp |
| --- | --- |
| A — Chốt trải nghiệm và change proposal | User journeys, workspace/key/share semantics, taxonomy ban đầu và impact CR01/R01–R40 được owner review |
| B — Chứng minh lát cắt rủi ro | Repo-local synthetic encrypted round-trip, CLI folder/search/read/revise và scoped document preview/edit/lock; không giả là sản phẩm đã ship |
| C — Chốt thiết kế và migration | Storage/identity/metadata/index boundary, document-bridge/engine dependencies, export/backup/restore và migration từ 1.0/1.1 có acceptance rõ |
| D — Lập sprint và triển khai tuần tự | Task atomic theo dependencies, gate đúng changed surfaces; usable file browsing/search trước, rồi tăng cường cognitive/relational recall và context delivery |

Bảng trên mô tả logic phụ thuộc; [roadmap](ROADMAP-2.0.md) cụ thể hóa thành các sprint/release planned, không phải lịch giao hàng. Cognitive memory là mục tiêu của thiết kế ngay từ đầu, không phải phần tùy chọn bị bỏ sau khi xây file browser. Không viết lại mọi tính năng 1.0.0 hoặc mở thêm provider/telemetry/sync ngoài nhu cầu này.

## 11. Compatibility, migration và change control

1.0.0 vẫn là release baseline đã được nghiệm thu. Không đổi tag/assets hay chuyển bằng chứng cũ thành PASS cho kiến trúc mới.

Trước khi cutover cần có change proposal và owner approval cho các thay đổi storage, scope/threat model hoặc contract downstream; append decision rồi đồng bộ specification, requirements, PLAN và sprint liên quan.

Impact review tối thiểu:

- Mapping canonical items/sources/revisions hiện tại sang file và metadata mới; dữ liệu nào không có original file phải được biểu diễn trung thực.
- Bảo toàn author/executor, citation, relation, lifecycle và phân loại owner đã xác nhận; không relabel AI-written thành user-written.
- Chính sách cho catalog/grants/tokens/sessions cũ; không lặng lẽ mở rộng quyền trong migration.
- Key/recovery, encrypted backup/restore, format version và đường phục hồi khi migration gián đoạn; không reset/xóa vault để né cutover.
- CLI/MCP/document bridge cùng dùng một core/policy và authoritative state; WPF legacy frozen, không promise old UI/new runtime compatibility. S12 phải chốt installed-component transition/version refusal/remediation an toàn trước 1.2.0.
- Search/extraction cũ không tự trở thành bằng chứng mới; mỗi changed surface có smoke/evidence tương ứng khi triển khai.

Không thực hiện migration, sửa private data, host configuration, ACL/permissions hoặc agent profiles trong phạm vi tài liệu này.

## 12. Cách đánh giá hướng mới

Các scenario sau là đầu vào để xây acceptance, chưa phải kết quả đã đạt:

- Owner thêm một folder được chọn, duyệt được nội dung và trạng thái mà không enrollment thủ công từng file.
- File chưa chỉnh sửa round-trip bytes qua encrypted persistence, restart/unlock và export original format.
- Schema OMP mới giữ được nguyên bản và báo rõ phần chưa hiểu, không ngụy tạo attribution hay completion.
- Đổi tên/chuyển file không làm đứt identity và quan hệ đã xác nhận; thay nguồn làm evidence cũ được báo cần review.
- Search tìm đúng đoạn và vùng tri thức cho cả exact symbol, cách nói tương đương, lịch sử quyết định và dự định còn mở.
- Relation expansion cung cấp bằng chứng/phản biện hữu ích, không vượt scope hoặc làm ngập context.
- Owner nhìn được sự khác biệt giữa lời nguồn, suy luận AI và điều mình xác nhận; chỉnh sửa được phân loại mà không sửa file gốc ngoài ý muốn.
- Lock chặn private reads và xóa private views theo contract đã duyệt; không có plaintext temp/cache ngoài hành động export rõ ràng.
- Tác vụ trên corpus đại diện vẫn responsive trong ngân sách được duyệt; chất lượng retrieval đánh giá bằng câu hỏi sử dụng thật, không chỉ benchmark marketing.

## 13. Quyết định cần chuẩn bị tiếp theo

- Folder là đơn vị tổ chức, unlock và chia sẻ đến mức nào; các boundary này có trùng nhau hay không?
- Master key vault quản lý những key nào; recovery và multi-workspace isolation ra sao?
- Metadata/quan hệ owner-authored lưu và export ở đâu để vừa tường minh vừa mã hóa khi nghỉ?
- Dữ liệu đã diễn giải một phần được search/cite như thế nào mà không gây hiểu nhầm?
- S13 document bridge authenticate/bind/lifetime, safe-rendering/editor support matrix và browser persistence/erasure limits được chứng minh bằng lát cắt nào? CLI-first đã chọn, không mở lại WPF hoặc thêm đa nền tảng.
- Ngân sách tài nguyên và bộ câu hỏi/corpus đại diện nào dùng để chọn search engine và đo chất lượng?

[Roadmap 2.0.0](ROADMAP-2.0.md) và Sprint 12–18/G2 đã đồng bộ CR03; Sprint 11/1.1.0 Complete. Execution tiếp theo chỉ khi owner authorize Sprint 12, bắt đầu S12-T1 storage/key/identity và CLI support transition; browser thuộc S13. Lần cập nhật này chỉ planning, không implementation, runtime/gate PASS, commit hoặc publication.
