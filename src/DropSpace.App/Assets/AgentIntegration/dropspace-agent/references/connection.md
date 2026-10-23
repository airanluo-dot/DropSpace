# One-time local pairing

The application exports this skill to `<app storage>/Agent/Skill/dropspace-agent`. The usual storage directory is `%LOCALAPPDATA%\DropSpace`; an invitation can specify another endpoint descriptor for an isolated instance. Use the paths supplied by that invitation. Do not install a copy of the repository-maintainer skill.

The client entry point is `scripts/Invoke-DropSpace.ps1`. It accepts:

| Parameter | Meaning |
| --- | --- |
| `-Action` | `Pair`, `Catalog`, `Execute`, `Status` or `McpConfiguration` |
| `-BindingId` | Required non-secret GUID from the invitation; use a separate binding for each agent connection |
| `-EndpointFile` | Defaults to `%LOCALAPPDATA%\DropSpace\Agent\endpoint.json` |
| `-CredentialDirectory` | Optional; defaults to `ClientCredentials` beside that endpoint file |

Start the helper as a child process with `-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <absolute-helper-path> -Action Pair -BindingId <id> -EndpointFile <descriptor-path>`. Send exactly one UTF-8 JSON object to its stdin, then close stdin:

```json
{"pairingKey":"<one-time-key-from-the-invitation>"}
```

Use the shell tool or process API's stdin facility. The key must not be placed in an argument, a temporary script/file, a saved command, or an MCP environment entry. The helper's stdout reports only pairing success and the binding ID. It stores the bearer token using Windows DPAPI for the current user in a binding-specific file, with a directory ACL for that user and SYSTEM. It never returns the token to the model. Pair once, then reuse the connection across app restarts; the client rereads the descriptor on each request because the app's local port can change.

Save the non-secret binding ID, endpoint path and client path in your agent's existing connection configuration. The generic skill files remain credential-free. Multiple bindings use different credential filenames and do not overwrite one another. Credentials are tied to the Windows account that paired them; copying an encrypted file to another account does not establish authorization there.

`-Action Status` verifies the binding against the authenticated command catalog. An absent descriptor or failed local connection means DropSpace must be running with Agent connections enabled. An invalid or revoked binding requires a fresh invitation. Deleting a local credential file only forgets the local copy; revoke the connection in the app to terminate its authorization.

## Optional MCP stdio

Call `-Action McpConfiguration` with the same binding and endpoint parameters. Its JSON output contains an absolute `command` and `args` ready to place in your agent's existing MCP server configuration. Keep the surrounding configuration format specific to that agent; do not replace unrelated servers. No token is included. If Node.js 18 or newer is unavailable, this command returns `node_required`; continue using the PowerShell REST client. It does not download or run an installer.

The bundled `dropspace-mcp.mjs` exposes only the running app's tools. It supports the current `2026-07-28` per-request protocol and the `2025-11-25` initialize handshake used by older clients. Pairing happens outside MCP so the one-time secret is never a tool argument. Closing MCP stdin ends the bridge and its pending helper processes, without closing DropSpace. Cancellation stops waiting for a helper; it cannot undo a mutation already committed by the app.
