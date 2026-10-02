# Changelog

All notable changes to Meta Brain are documented here.

## [Unreleased]

### Added

- Added an owner-controlled encrypted vault with passphrase/recovery-key provisioning, explicit v1 settings/data migration, locked startup, authenticated managed-resource reads/writes, and a lock that drains active private responses.
- Added distinct owner-control and agent pipes with schema-2 settings migration, atomic encrypted scope-state migration, consume-before-disclose single-use bearer redemption, process-local epoch-bounded sessions, expiry/policy invalidation, and owner-controlled scope inspection through DPAPI-protected local handoffs.

### Removed

- Removed the protected Codex/OMP launcher, AppContainer and per-session credential bindings, and legacy session CLI commands. The service no longer claims OS-level agent sandboxing; CR01 exposes only a separate scope-limited agent session protocol. MCP/client/resource-retrieval integration remains future work.
