# Changelog

All notable changes to Meta Brain are documented here.

## [Unreleased]

### Removed

- Removed the protected Codex/OMP launcher, AppContainer and per-session credential bindings, agent pipe/MCP/read/wire entry points, and legacy grant/session CLI commands. The retained service exposes only owner status and managed-resource reads; agent sharing remains unavailable until the planned token cutover. No vault encryption, catalog or token feature is included in this removal.
