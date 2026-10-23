# Local Agent client distribution

DropSpace embeds the end-user skill under `src/DropSpace.App/Assets/AgentIntegration/dropspace-agent/` and exports it to `AppStoragePaths.Root/Agent/Skill/dropspace-agent`. This is product documentation and client code, distinct from the repository's canonical `dropspace-maintainer` skill. No personal skill copy is created by the build.

Embed these six files, preserving their relative paths:

- `SKILL.md`
- `agents/openai.yaml`
- `scripts/Invoke-DropSpace.ps1`
- `scripts/dropspace-mcp.mjs`
- `references/connection.md`
- `references/commands.md`

The existing `--shell-add` CLI only accepts Explorer/SendTo intake paths. It is retained for that flow. The agent client uses the authenticated local HTTP command dispatcher rather than extending intake flags into a second command catalog. `Catalog` and MCP `tools/list` obtain fresh schemas from that dispatcher; the bridge has no hard-coded app feature list.

## Pairing and identity

The UI's copied invitation gives the skill path, binding GUID, discovery file and single-use pairing key. `AgentSkillPackageService` owns a fixed English template formatted with `CultureInfo.InvariantCulture`; its constructor and prompt generation have no app localizer dependency. App language does not change the prompt. The settings UI keeps its normal localized wording, with no special language label. The template adds no seventh product skill resource; invitation values are populated only in the clipboard text:

> Read `<skill path>/SKILL.md` and install this skill in your supported local skill directory. If skills are unsupported, use its PowerShell client. Pair local DropSpace once using `scripts/Invoke-DropSpace.ps1 -Action Pair -BindingId <binding ID> -EndpointFile "<endpoint file>"`. Send `{"pairingKey":"<clipboard-only key>"}` as UTF-8 JSON on stdin. Keep the key out of command-line arguments, files, logs and reusable instructions. Afterwards use this binding’s saved credential and discover the command catalog. This request authorizes skill installation and pairing only, not other App actions.

The helper decrypts the bearer only in memory and never prints it. Each binding has its own DPAPI-protected file under `Agent/ClientCredentials`. `-EndpointFile` also changes the default credential directory, keeping diagnostic storage separate. A caller may specify an explicit `-CredentialDirectory`. Removing a binding in the app is the authorization boundary; deleting a local credential file alone is not revocation. Invalid authorization is reported without falling back to another binding.

The client validates loopback discovery, disables redirects and proxies, bounds stdin/request and response data, and never installs dependencies. Windows PowerShell 5.1 is the baseline. Node.js 18+ is optional for stdio; there are no npm dependencies. The bridge starts only the bundled helper using the system Windows PowerShell path and does not terminate the app when its own transport closes.

## MCP compatibility

The implementation was checked against the official current `2026-07-28` specification. It uses per-request version/capability metadata, `server/discover` and complete-result envelopes, while retaining the `2025-11-25` initialization path for existing clients. Modern metadata is evaluated independently for each request. Sources: [versioning](https://modelcontextprotocol.io/docs/2026-07-28/learn/versioning), [base protocol](https://modelcontextprotocol.io/specification/2026-07-28/basic/index), [discovery](https://modelcontextprotocol.io/specification/2026-07-28/server/discover), [legacy lifecycle](https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle).

Messages are newline-delimited JSON-RPC on UTF-8 stdin/stdout. Only protocol output goes to stdout. The bridge implements `tools/list`, `tools/call` and `ping`, advertises no resources/prompts/subscriptions, and handles cancellation plus EOF by stopping its own active helpers. A canceled response is suppressed; already-committed app work cannot be rolled back by terminating the helper. Sources: [stdio transport](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio), [tools](https://modelcontextprotocol.io/specification/2026-07-28/server/tools).

The HTTP API is DropSpace's local JSON API, not an MCP HTTP transport or OAuth service. The optional stdio bridge maps its discovered commands to MCP tools. Tool execution failures are `isError` tool results; protocol errors use JSON-RPC errors. `mutates` maps conservatively to read-only/destructive hints, with open-world hints because catalog commands may invoke downloads or other external operations. The app remains responsible for validating each command against its declared schema and authorization.

## Focused validation

Use isolated endpoint and credential directories when testing. Cover one-time pairing without secret output, per-binding DPAPI persistence, Unicode command arguments, app-port rediscovery, revoked credentials, rejected non-loopback/redirect destinations, legacy initialize/tools, modern discovery/metadata/tools, and cancellation/EOF. A mocked transport proves the client boundary; actual app-domain behavior requires the app's own runtime checks. Do not replace either with a broad unrelated test suite.

`node tests/DropSpace.AgentClient.Tests/client.test.mjs` passed 23 focused checks on Windows with real Windows PowerShell 5.1, DPAPI and Node.js v24.19.0. The fixture binds an isolated loopback HTTP server and uses temporary endpoint/credential files under `artifacts/1.1-beta1-agent/`; it never reads the installed app's credentials. Its receipt is `artifacts/1.1-beta1-agent/client-check-MZVwN6/receipt.json`. This verifies client transport behavior, not the live app's command implementations or third-party agent configuration UIs. The skill-creator `quick_validate.py` and Node syntax check also passed. The initial shell's Python alias lacked a runtime; validation used the existing bundled Python and existing local PyYAML files without installing dependencies.

`dotnet run --project tests/DropSpace.AgentClient.Tests/SkillPackageCheck.csproj --configuration Release` passed 12 export checks using the linked production service and actual embedded asset bytes in an isolated .NET 10 fixture. Checks cover the six-resource inventory, each exported SHA-256, no rewrite of identical files, replacement of a 64 MiB pre-existing file, preservation of a pre-existing `.new` file, owned-stage cleanup and canceled admission. Receipt: `artifacts/1.1-beta1-agent/skill-export-5832011465ab40d3b4156a5014d8edbb/receipt.json`. MSBuild evaluation confirmed that the App declares exactly the same six logical names; Windows backslashes after the resource prefix are normalized on export. This test does not launch WinUI or replace the App's final build check.

After making the invitation a fixed English template, the same fixture ran with `-- --prompt-only`: four targeted checks passed for identical output under en-US/zh-CN/ar-SA cultures, retained installation-only wording, correct JSON/ID/path interpolation, and the unchanged six-resource inventory. Receipt: `artifacts/1.1-beta1-agent/skill-export-38e5a15621a547ea8053cbd46c255a65/receipt.json`. The original twelve export checks do not depend on localized prompt text; their fixture constructor now matches the service's sole storage-path dependency. No live invitation or WinUI behavior was used in these four checks.
