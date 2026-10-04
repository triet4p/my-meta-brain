# Changelog

All notable changes to Meta Brain are documented here.

## [Unreleased]

### Added

- Added an owner-controlled encrypted vault with passphrase/recovery-key provisioning, explicit v1 settings/data migration, locked startup, authenticated managed-resource reads/writes, and a lock that drains active private responses.
- Added distinct owner-control and agent pipes with schema-2 settings migration, atomic encrypted scope-state migration, consume-before-disclose single-use bearer redemption, process-local epoch-bounded sessions, expiry/policy invalidation, and owner-controlled scope inspection through DPAPI-protected local handoffs.
- Added owner-only active-session listing and selective revocation plus agent-channel authorization decisions for exact frozen operations and resource/revision scopes. Lock, expiry, and policy-generation changes deny stale sessions; only the per-job `$0.25`, 10,000-input-token, and 2,000-output-token ceilings are enforced at this boundary. Agent-supplied egress remains denied without a trusted provider adapter/pricing context; no provider call or `$5/30-day` rolling-spend ledger is implemented.

### Removed

- Removed the protected Codex/OMP launcher, AppContainer and per-session credential bindings, and legacy session CLI commands. The service no longer claims OS-level agent sandboxing; CR01 exposes only a separate scope-limited agent session protocol. MCP/client/resource-retrieval integration remains future work.
