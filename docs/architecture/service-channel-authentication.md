# Historical implementation record — AppContainer service channel (source removed)

**Historical-only record:** S1-CLEAN removed the AppContainer launcher, identity/session bindings, agent pipe/MCP bridge, and old owner launch/session commands from current source. Every source location, command example, and behavior below records the superseded implementation; treat paths as literal historical locations, not live links or operator steps, and do not run these examples. The retained owner-only service exposes status and managed-resource read only; same-account pipe access is not owner intent, agent isolation, encrypted-vault, or token evidence. See the [current specification](../META-BRAIN.md), [CR01 requirements](../REQUIREMENTS.md), and [replacement Sprint 1](../sprint-plans/sprint-1.md).

At the historical baseline, the service used two local, versioned Windows named-pipe endpoints: owner control and agent data. The service rejected remote pipe clients and claimed the first pipe instance so a pre-created competing endpoint caused startup failure instead of an implicit fallback.

## Dependency boundary

- `MetaBrain.Core` owns the immutable authenticated-context type and permission policy. Its constructor is not public and the assembly has no UI or MCP dependencies.
- `MetaBrain.Application` consumes only a context created by the trusted transport and performs the shared operation authorization.
- `MetaBrain.Connections` owns named-pipe ACLs, Windows token inspection, session credential verification, and request/response framing. It is the only assembly allowed to create an authenticated context.

The project references are one-way: Application → Core; Connections → Application and Core. Connections is both the Windows Service host and the owner/agent named-pipe client library/command entrypoint. It does not own a second policy implementation.

## Authentication

The owner control pipe's DACL allows the configured owner SID and service identity, rejects AppContainer callers, and checks the impersonated client's SID plus `TokenIsAppContainer` on every connection. After authentication, the server sends a ready response before reading an owner request. The owner has no bearer credential; authority comes from the authenticated Windows token.

The agent data pipe's DACL allows the service process and only AppContainer SIDs listed in protected settings. On each connection the server impersonates the client and reads `TokenUser`, `TokenIsAppContainer`, and `TokenAppContainerSid`, then binds the observed user/container SIDs to one configured principal/session and verifies the public credential ID plus a fresh HMAC-SHA-256 challenge proof. In the synthetic fixture, each raw 256-bit credential file is protected by an explicit ACL: its associated AppContainer can read it; the owner and SYSTEM can manage it; peer agents cannot. The service settings store only a SHA-256 verifier, not the raw credential. Raw credential bytes are not put in command-line arguments, environment variables, request bodies, or the pipe.

Request bodies carry versioned operation payloads, but identity-looking fields (`is_owner`, agent name, session ID, cwd, path, localhost, and similar claims) are ignored; the application receives a server-created context, never a client-selected principal. Owner-only operations are denied on the agent data channel.

## Grant lifecycle

`grants.create`, `grants.list`, `grants.get`, and `grants.revoke` are available only on the authenticated owner-control channel. Grants bind a configured principal and session to one operation, exactly one resource or zone scope, and a UTC expiry. Agents can call `access.check`, but cannot mutate or inspect the policy; an agent with no matching live grant is denied. Owners bypass grants for local operations. Each committed policy change and expiry purge advances a persisted generation; revocation and expiry remove the grant so an already-authenticated session cannot reuse it.

The service stores grants in a versioned `grant-policy.json` next to protected service settings. The file is restricted to the service identity, SYSTEM, and Administrators (owner/admin remain outside the threat model). Mutations are serialized in-process, checked against the loaded generation, flushed to a same-directory temporary file, and atomically replaced. Missing state starts empty; malformed or inaccessible state fails closed. No SQLite policy mirror or provider/model call is introduced here.

Provider egress grants bind an exact provider/model and positive USD cost cap. The policy requires a trusted context and priced estimate; client-supplied agent claims are untrusted, and unknown or unpriced egress is denied. T5 adds no trusted provider adapter or provider/model call, so raw agent-wire egress remains fail-closed; the smoke exercises policy decisions through the owner control channel only.

## Owner CLI and OMP resource reads

The `owner` CLI is authorized only by the configured Windows owner token on the control pipe; there is no owner bearer credential and callers must not supply one in arguments or environment variables. `owner launch` creates and registers a per-session AppContainer, prints the generated principal/session identifiers, then waits for the protected client process tree to exit. Run later owner commands from a second owner console while that command is waiting, or after it exits; the registration remains service-bound until the owner revokes the session.

Before resuming the suspended client, the production launcher verifies `TokenIsAppContainer`, the registered `TokenAppContainerSid`, the configured owner's `TokenUser` SID, and the Low mandatory integrity label. Any mismatch fails closed and the owned process/job is terminated and drained.

The owner control workflow is `owner grant` → `owner inspect` → `owner read` → `owner revoke`. Grant and inspect require the exact principal, session, operation, and resource or zone scope. Owner `read` calls the service and should use `--output-file` so managed content is not written to the terminal; its normal output reports only the byte count. Revoking a grant blocks the next service request. Session revoke removes its grants and credential, then attempts AppContainer-profile removal; an active runtime can block profile cleanup. Revocation does not signal a running client, but the next service request is denied.

```text
MetaBrain.Connections.exe owner launch --control-pipe <pipe> --runtime omp --executable <bun.exe> --omp-cache-root <bun-cache> -- <client-args>
MetaBrain.Connections.exe owner grant --control-pipe <pipe> --principal-id <principal> --session-id <session> --resource-id <opaque-id> --expires-in-minutes 60
MetaBrain.Connections.exe owner inspect --control-pipe <pipe> --principal-id <principal> --session-id <session> --resource-id <opaque-id>
MetaBrain.Connections.exe owner read --control-pipe <pipe> --resource-id <opaque-id> --output-file <owner-output-file>
MetaBrain.Connections.exe owner revoke --control-pipe <pipe> --session-id <session>
```

For OMP, the protected workspace receives a session-local `.mcp.json` entry that starts the staged `MetaBrain.Connections.exe agent-mcp` stdio bridge. The installed OMP `read` command uses `mcp://metabrain://resource/<opaque-id>`; the bridge maps that URI to `metabrain://resource/<opaque-id>` and issues `resource.read` through the authenticated per-session agent pipe. The service applies the same grant policy as every other client, so a cross-scope, revoked, or unknown resource returns a generic unavailable error without resource content. The bridge receives the credential file path and credential ID, never a raw credential in its command line or URI.

The protected Codex runtime writes a session-local `CODEX_HOME/config.toml` that starts the staged `MetaBrain.Connections.exe agent-mcp` bridge and supplies only the pipe name, protected credential-file path, and credential ID; raw credentials are never placed in Codex config or process arguments. Official Codex 0.159.2 documents `mcpServer/resource/read` on its app-server, which can request a local MCP resource without a model turn ([app-server protocol](https://learn.chatgpt.com/docs/app-server)). The authorized AppContainer probe ran `codex --version` but `codex mcp list` failed while canonicalizing `CODEX_HOME` with `Access is denied`; no Codex service resource read/revoke is claimed. No `gpt-6-luna` request was made: no fixture-specific API credential or trusted per-job provider egress/cost controller is available. Official [model documentation](https://developers.openai.com/api/docs/models/gpt-6-luna) lists MCP support and rates of $0.10 per million input tokens and $0.50 per million output tokens. A price card alone cannot enforce the owner-approved limit of two jobs, $0.25/job, $5/30 days, 10k input tokens, and 2k output tokens.

The focused outside-AppContainer lifecycle smoke now drives official Codex 0.159.2 through one persistent app-server stdio session: initialize, configured MCP readiness, and threadless `mcpServer/resource/read` requests before and after owner revoke. Both resource reads were denied and returned no content, so this proves protocol/configuration/lifecycle compatibility only—not protected-read authorization or the revoke-after-access pair. Codex's legacy MCP client negotiates `2025-06-18`; the bridge accepts that version while retaining `2025-11-25`, and returns an empty `tools/list` because it exports resources, not tools. The AppContainer proof remains blocked at `CODEX_HOME` canonicalization.

## Scope and limits

S1-T4's authenticated context is the seam for the S1-T5 grants, S1-T6 managed-resource reads, and S1-T7 owner CLI/OMP bridge. The Windows smoke uses a new marked fixture, synthetic AppContainer sessions, and real local named-pipe requests. It verifies owner/agent separation, spoofing and owner-only denial, grant/revoke, the actual OMP `read mcp://` service path, cross-scope and owner-profile denial, and credential redaction. It runs the service in console mode, not under a production Windows Service Control Manager.
Production Windows Service Control Manager installation, identity, and credential provisioning remain unverified. The installed official Codex 0.159.2 is unchanged; its synthetic AppContainer `mcp list` diagnostic fails while resolving `CODEX_HOME`, so Codex owner-launch behavior remains unverified. The fixture-only 0.159.2 build with the approved `CODEX_HOME` canonicalize-to-absolute fallback now completes the protected app-server grant-read, cross-zone/unknown denial, owner-revoke, and same-client post-revoke denial over one persistent AppContainer session.
