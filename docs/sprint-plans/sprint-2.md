# Sprint 2 — Durable core và mô hình tri thức

## Sprint Goal

Lưu tri thức canonical mã hóa có revision/provenance/relations/time, private index được bảo vệ và rebuildable; phục hồi write/index interruption mà không leak plaintext hoặc mất dữ liệu.

## Status and Dependencies

- Status: Not started — CR01 updated. Entry: replacement Sprint 1 encryption/key/catalog/token gate PASS; old AppContainer PASS không đủ. Dùng shared encrypted storage và scoped application boundary, không OS identities/launcher dependency.
- [Global plan](../PLAN.md), [specification](../META-BRAIN.md), [requirements](../REQUIREMENTS.md).
- Requirements: R10, R11, R12, R14, R32, R33; bảo toàn R02–R06, R36, chuẩn bị R13, R16, R26, R27, R39.

## Downstream Contracts

Envelope giữ source locators/availability, attribution/schema/revision/time/level từ đầu nhưng persisted private fields encrypted. Catalog là projection được owner công bố, không expose envelope/index. Grant chốt IDs/revisions; source và item permissions độc lập. Canonical schema/encrypted envelope có migration boundary, không direct plaintext editing. Intentions/questions/publication state machines chưa scheduler/UI; relations giữ confidential lineage.

## Atomic Tasks

Status legend: [ ] pending / [~] in progress / [x] done. OMP role selection, evidence review, record ownership, and exact-snapshot checkpoints follow the [canonical execution contract](../PLAN.md#planning-and-execution-contract). Worker artifact for S2-T<M>: `artifacts/sprint-2/task-<M>.md`; reviewers and checkpoint executors own their separate records under the sprint artifact tree.

- [ ] **S2-T1 — Triển khai domain envelope và zone/resource semantics.** Requirements: R10, R11, R12, R32.
    - Scope: một model dùng chung cho source reference và typed memory metadata, validation/identity; không profile extraction.
    - Acceptance: IDs độc lập path/title; unknown không bị bịa; kind/tác giả/executor/cách tạo/evidence tách biệt; zone/collection/level không interchangeable. APIs dùng scoped trusted context; thêm item/membership/revision không tăng grant snapshot.
    - Evidence: tạo/read các item user question, AI synthesis, tool observation qua core/service thật; reject invalid attribution/schema; không đơn thuần assert class wiring.

- [ ] **S2-T2 — Encrypted canonical files có revision conflict và write recovery.** Requirements: R04, R10, R33, R36, R39.
    - Scope: serialize human-readable logical records bên trong shared encrypted envelope S1; durable atomic visibility/revision protocol, protected journal/temp. Không plaintext truth store thứ hai.
    - Acceptance: saved data còn sau restart/unlock, stale update conflict, crash không half-published. Wrong-key/tamper/truncation/resource-swap hoặc revision mismatch fail closed; canonical/metadata/journal/temp không plaintext. Cutover old resource format/callers nếu có, versioned migration không reset/xóa dữ liệu; external plaintext editing thay explicit import/export.
    - Evidence: write/restart/unlock, interruption/concurrent writes, ciphertext corruption và inspect persisted files; real old-format fixture migration nếu tồn tại, không fabricated compatibility proof.

- [ ] **S2-T3 — Lưu source registry và provenance spans độc lập item.** Requirements: R10, R12, R16, R18.
    - Scope: encrypted registered source metadata/version/locator/availability và item-source refs; adapter JSONL ở Sprint 3, original transcripts ngoài vault protection.
    - Acceptance: item readable nhưng source API scope riêng; unknown/missing không fake verified citation; revisions/spans đúng; citation projection không lộ private path/title; private registry encrypted và locked không source expansion.
    - Evidence: tạo source fixture, link item, thay availability/revision và mở source dưới hai grants khác nhau.

- [ ] **S2-T4 — Triển khai relation store và revision-aware invalidation.** Requirements: R13, R14.
    - Scope: typed relations, provenance, state, endpoints, ownership/access requirements; không inference bằng model.
    - Acceptance: related_to theo ID; derived_from/supports giữ revision; cấm cycle của derived_from/supersedes nhưng cho related_to cycles; nguồn thay/mất đánh dấu dependent item cần review; không lộ endpoint ngoài quyền. Đổi zone/revoke giữ policy đúng.
    - Evidence: multi-source relation chain, cycle rejection, revision change, unlink/relink và cross-zone negative reads; không chỉ test dữ liệu mẫu copy qua API.

- [ ] **S2-T5 — Triển khai domain lifecycle và temporal validity.** Requirements: R12, R26, R27.
    - Scope: state transitions cho proposal/publication, question, intention và supersession; scheduler/retention policies để Sprint 7.
    - Acceptance: draft publish/reject có actor/history; question không tự resolved bởi AI answer; intention notified khác done; occurred/recorded/validity/precision/timezone giữ đúng; invalid transitions bị reject; mâu thuẫn không newest-wins.
    - Evidence: chạy các transition qua service, historical revisions còn truy được; user save AI note không đổi authorship/verification.

- [ ] **S2-T6 — Triển khai collections và permission-filtered views.** Requirements: R11, R32.
    - Scope: organization memberships và projection contracts, không UI hoặc smart brief.
    - Acceptance: multiple memberships không nhân bản truth/grant; agent views chỉ trong approved ID/revision scope, không hidden counts/titles; collection/zone descendants mới không tăng quyền. Catalog view chỉ owner-published metadata, khác private collection view.
    - Evidence: owner và hai agents duyệt cùng collection, thay membership/title/path không mất identity; direct denied access vẫn bị chặn.

- [ ] **S2-T7 — Protected private SQLite index và rebuild/reconciliation.** Requirements: R04, R06, R33, R36, R39.
    - Scope: index materialization/rebuild và at-rest protection cho DB/WAL/journal/temp/cache; chọn standard encryption hoặc memory-only index bằng evidence, không tự crypto. Ranking ở S4, catalog projection riêng.
    - Acceptance: index disposable, rebuild khi unlocked tái hiện ID/revision/scope/lifecycle/source/relation; crash reconcile, không stale unauthorized reads/lost commits. Không plaintext private DB/temp persist khi locked hoặc crash. Không auto publish titles/embeddings/counts vào catalog; không nới frozen budgets để chọn mechanism.
    - Evidence: corruption/removal/rebuild trong fixture, concurrent writer/reindex, lock/restart và inspection file/WAL/temp leak boundary; schema/migration extension points cho S8.

## Sprint Acceptance Gate

Unlock và tạo source/question/synthesis/intention ở hai zone; save/link/revise/supersede/rebuild private index, rồi lock/restart. Ciphertext/private metadata/index/temp không plaintext, wrong-key/tamper deny; owner unlock đọc được. Agent chỉ theo grant snapshot, không raw-source/link/membership/revision expansion. Crash/concurrent writes không lost update/half-publication; one core không UI/MCP dependency.

## Notes / Blockers

Encrypted envelope/storage và recovery/index-protection là implementation decisions cần approved-package evidence; human-readable là sau unlock/export, không file plaintext at rest. Nếu package rules xung đột, báo trước; không tự thay truth store hoặc nới memory/latency targets. Key management dùng S1, không parallel key hierarchy.

## Gate Record

Pending. Chưa có task evidence hoặc review pass.
