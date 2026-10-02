# Changelog

All notable changes to Meta Brain are documented here.

## [Unreleased]

### Added

- Added an owner-controlled encrypted vault with passphrase/recovery-key provisioning, explicit v1 settings/data migration, locked startup, authenticated managed-resource reads/writes, and a lock that drains active private responses.
- Added owner-only scoped token previews and issuance for zones, flat owner-managed collections, or exact resource sets. Grants freeze concrete resource revisions, operations, expiry, destination scope, and optional provider/model/cost context in encrypted `scope-grants.enc` state; collection membership is encrypted and can change without enlarging existing grants. Tokens are handed off through a DPAPI-protected owner-only file and are not printed or persisted plaintext.

### Removed

- Removed the protected Codex/OMP launcher, AppContainer and per-session credential bindings, agent pipe/MCP/read/wire entry points, and legacy grant/session CLI commands. The retained local service is owner-only and offers status, managed-resource read/write, vault lifecycle, collection, and grant-issuance commands. Agent sharing still has no redemption/session/read API; owner token issuance does not provide agent access or catalog publication.
