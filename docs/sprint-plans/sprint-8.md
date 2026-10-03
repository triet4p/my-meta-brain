# Sprint 8 — Production vận hành hằng ngày

## Sprint Goal

Owner cài, vận hành, nâng cấp, khôi phục và dùng Meta Brain hằng ngày với toàn bộ yêu cầu được chứng minh, không còn đường demo hoặc gate bị hoãn.

## Status and Dependencies

- Status: Not started — CR01 updated. Entry: Sprint 7 PASS, replacement encryption/catalog/token gates đã qua review, artifacts đọc được và S1 frozen targets không đổi. Old AppContainer PASS không release evidence mới.
- [Global plan](../PLAN.md), [specification](../META-BRAIN.md), [requirements](../REQUIREMENTS.md).
- Requirements chính: R34, R35, R36, R37, R38, R39, R40. Release phải có evidence cho toàn bộ R01–R40.
- Đây là production-readiness work, không thay cho security/integrity checks đã bắt buộc từ các sprint trước.

## Downstream Contracts

Release không reset dữ liệu. Versioned encrypted envelope/key/control/canonical/index và backup compatibility có docs; restore cần key và không hồi sinh live sessions/used tokens/purged data. Original source sessions ngoài vault untouched. Windows/actual provider/client scope công bố đúng, không sandbox/cross-platform claim.

## Atomic Tasks

Status legend: [ ] pending / [~] in progress / [x] done. OMP role selection, evidence review, record ownership, and exact-snapshot checkpoints follow the [canonical execution contract](../PLAN.md#planning-and-execution-contract). Worker artifact for S8-T<M>: `artifacts/sprint-8/task-<M>.md`; reviewers and checkpoint executors own their separate records under the sprint artifact tree.

- [ ] **S8-T1 — Production installation và locked lifecycle packaging.** Requirements: R35, R02, R04, R05.
    - Scope: installer/application-service startup, autostart opt-in, health/uninstall; shared S1 key/session lifecycle, không account isolation hoặc patched agent runtime.
    - Acceptance: install/open startup locked, owner unlock, normal official Codex/OMP connect; restart/logout/reboot locked và no session resurrection. Không admin thường trực; uninstall không xóa vault/source/backup/recovery material khi chưa đồng ý. No AppContainer/protected-launch prerequisite.
    - Evidence: install/start/stop/restart/uninstall thực trên môi trường Windows kiểm soát, phân biệt reboot đã thực hiện với chưa thực hiện; nếu reboot là supported daily path thì phải kiểm chứng trước gate.

- [ ] **S8-T2 — Encrypted backup/restore và explicit portable export.** Requirements: R04, R34, R29, R36.
    - Scope: consistent encrypted snapshot/lineage/lifecycle, restore/key-recovery và owner-scoped export; không live credentials/master key kèm backup.
    - Acceptance: wrong/missing key và tampered backup fail closed; clean restore sau unlock giữ records/revisions/relations/source/intention/archive/deletions, rebuild private index; no live grant/session/used token resurrection. Key/recovery user tự giữ, mất key không fake recovery/reset. Plaintext export phải explicit scope/disclosure, no private citations ngoài phạm vi; encrypted backup/retention limits rõ.
    - Evidence: actual concurrent backup, inspect ciphertext/temp, restore clean location với valid/wrong/recovery key và corruption; lost-key denial; export đọc được ngoài app chỉ qua owner-selected disclosure, old copied data không thu hồi.

- [ ] **S8-T3 — Encrypted schema/key-format migration và interrupted recovery.** Requirements: R04, R35, R39, R33.
    - Scope: versioned crypto-envelope/canonical/control/private-index migration và rollback/recovery; key-format/KDF compatibility, không speculative key hierarchy hoặc reset.
    - Acceptance: real prior dataset giữ IDs/scopes/attribution/relations/lifecycle; migration temp/rollback encrypted, interruption recover và wrong-key deny; no saved record loss/auto-unlock. Thay passphrase/wrapping nếu format hỗ trợ không silently xóa khả năng restore backup cũ; docs ghi key/backup compatibility và rollback limits.
    - Evidence: genuine old encrypted schema + upgrade/fault injection/write/job/purge/lock/restart, actual UI/MCP reads; migrate existing legacy plaintext fixture nếu còn bằng explicit owner cutover, no source migration hoặc new leak.

- [ ] **S8-T4 — Privacy-safe operations và audit.** Requirements: R36, R07, R29.
    - Scope: owner health/audit/error diagnostics, retention và vận hành log/secret/artifact; không thêm telemetry cloud mặc định.
    - Acceptance: owner unlocked thấy actor/action/outcome/cost/errors, agent không broad audit/key/token; private audit/diagnostics persisted encrypted, no raw transcripts/secret bodies. Catalog là disclosure duy nhất ngoài nonsensitive envelope; repo ignores private/runtime data. Lock đóng private views, purge/retention bao phủ diagnostics.
    - Evidence: actual failed token/redemption/locked/provider/job calls, inspect sanitized logs/encrypted outputs, runbook thật; no cloud telemetry/auto-update ngoài consent, no same-owner OS isolation claim.

- [ ] **S8-T5 — Tune theo operational envelope và qualitative corpus.** Requirements: R23, R37, R38.
    - Scope: đo và sửa bottleneck/relevance trên dữ liệu user cho phép, không thay target thành benchmark khác.
    - Acceptance: đo encrypted persistence/protected index/unlocked queries và locked idle, p95 recall/UI/ingest/RAM/CPU/disk/provider cost theo S1 targets; Việt–Anh/paraphrase/symbol/time hữu ích. Không plaintext index/bỏ provenance hoặc giảm scope checks để đạt tốc độ; không nới target sau fail.
    - Evidence: chương trình/UI thật trên máy đích, số đo lặp có methodology và scenario notes; chọn embedding/reranker nếu thực tế cần, ghi decision/tradeoff, không nâng complexity vô cớ.

- [ ] **S8-T6 — End-to-end security và recovery acceptance replay.** Requirements: R02, R03, R04, R05, R06, R07, R08, R09, R29, R34, R39.
    - Scope: release-level adversarial scenarios trên surface tích hợp sau packaging; không reviewer lặp lại unit checks đã pass.
    - Acceptance: normal official Codex/OMP + owner UI/app, encrypted store/private index/source/provider/restore giữ CR01 boundary. Wrong-key/tamper/plaintext temp/index/backup, unpublished catalog, approval spoof, concurrent/replayed token, scope growth/guessed IDs/source/relation/cache, revoke/expiry/lock/restart/injection/declassification negatives thật. No key/token model disclosure hoặc restore resurrection; no OS-shell/process/peer-credential isolation acceptance.
    - Evidence: actual release with two scoped sessions/private/shared/catalog fixtures, commands/observations redacted, new gate evidence distinct old AppContainer history; limits bearer theft/same-owner runtime memory/previous disclosures explicit, findings corrected/reviewed.

- [ ] **S8-T7 — Owner daily-use acceptance và release handoff.** Requirements: R01, R30, R31, R35, R37, R40.
    - Scope: acceptance walkthrough và handoff trên release đã kiểm chứng; không tạo thêm feature không được yêu cầu.
    - Acceptance: owner install/unlock → encrypted capture/recall/decision/link/research/synthesis → catalog publication → agent request/narrow approval/token → read/revoke/lock → intentions/archive/purge → encrypted backup/recovery/restore và explicit export. “Vì sao nhớ?”/“Ai thấy?” đúng; user biết lost-key và same-owner/copies limits; actual approval không serious findings/requirements gaps.
    - Evidence: record owner feedback/approval thực, requirement-to-artifact map R01–R40, known non-blocking limitations, docs/changelog/version và operational runbook đúng release. Không giả owner approval; nếu chờ user thì task blocked, không release complete.

## Sprint Acceptance Gate

Mọi CR01 requirement có actual evidence/gate, owner qualitative acceptance thật; encrypted storage/key/lock/catalog/request/token/scope/egress security replay sạch. Installer/upgrade/key-compatible backup/recovery/export/daily-use paths chạy thật. End deep review differential toàn evidence mới, không old sandbox PASS thay crypto proof hoặc thay user acceptance.

## Notes / Blockers

Owner acceptance là external gate thật. Nếu provider, runtime, permission hoặc môi trường kiểm chứng không khả dụng, ghi blocker cụ thể và hoàn tất phần reachable, không đánh dấu production ready. Không dùng backup chưa restore thử như bằng chứng phục hồi.

## Gate Record

Pending. Chưa có release, production verification hoặc owner acceptance.
