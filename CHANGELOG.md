# Changelog

All notable changes to Meta Brain are documented here.

## [Unreleased]

### Added

- Added an owner-controlled encrypted vault with passphrase/recovery-key provisioning, explicit v1 settings/data migration, locked startup, authenticated managed-resource reads/writes, and a lock that drains active private responses.

### Removed

- Removed the protected Codex/OMP launcher, AppContainer and per-session credential bindings, agent pipe/MCP/read/wire entry points, and legacy grant/session CLI commands. The retained local service is owner-only and offers status, managed-resource read/write, and vault lifecycle commands; agent sharing remains unavailable until the planned token cutover. This removal did not itself provide an encrypted vault, catalog publication, tokens, or agent approvals.
