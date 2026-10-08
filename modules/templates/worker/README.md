# Optional worker module template

This is the executable contract-v1 example used by `modules/sample`. Copy this directory when
starting an official optional feature; change the manifest ID/version, `ModuleIdentity.cs`, and
the supported actions together. Built-in music, lyrics, clipboard and Temporary Space stay in
the App and retain their existing owners. A feature belongs in a module only when optional
delivery and an independent process are useful; normal built-in work continues normally.

Run `pwsh -File modules/templates/worker/Build-Package.ps1` from the repository. The build is
self-contained Windows x64 .NET 10, links the actual
`src/DropSpace.Core/Dlc/ModuleContracts.cs` wire types, and does not ship the host Core DLL or
service container. The package builder compiles; it never launches a worker or runs tests.
For a second build, select a fresh output directory inside the module project using
`-OutputDirectory`; a previously pinned ZIP is never overwritten.

The single declaration is `module.template.json`; the builder emits `manifest.json` plus the
explicit `Files` payload and a `package.json` with actual lengths/hashes. The manifest itself
is omitted from its recursive file-hash list. ZIP entry order/timestamps are fixed; publish
inputs, SDK/runtime and resulting package SHA must also be recorded before adding a release
to the official catalog. A manifest's publisher string and a SHA alone do not authorize
installation. The catalog binds module identity, version, exact asset URL under the official
repository, length and SHA-256. Only that official repository source is accepted over HTTPS;
catalog/source trust and payload integrity are separate checks. Explicit catalog refresh can
discover newer official modules without a new App release; startup uses saved descriptors
offline. Cryptographic publisher signatures are not part of this first-version contract.
Do not upload or replace assets under an existing version.

## Protocol and lifecycle

The actual host is `src/DropSpace.Infrastructure/Dlc/ModuleWorkerClient.cs`. Both directions use
one UTF-8 JSON envelope per line: `Protocol`, `Session`, `Id`, `Method`, `Payload`, `ErrorCode`.
Responses have `Method: "result"`; their `Payload` is a `ModuleReply` with `Result` and optional
`Island`. Respect the 65,536-byte limit before allocating/deserializing a line. Standard output
is only for replies. Diagnostics belong on stderr and must not contain user data.

The host passes `--module-session` and `--data-version`, creates the module's data directory as
cwd, and sends `hello`. Validate its `moduleId`, `moduleVersion`, `hostInterface`, `protocol`,
`dataVersion` and negotiated capabilities; return matching lower-camel identity fields in
`Result`. Register UI only after this succeeds. The example action request uses
`{ "action": "show", "input": {} }`. Settings requests carry the full declared string-value
dictionary, e.g. `{ "show-island": "false" }`; validate and acknowledge first. The host persists
settings only after acknowledgment and supplies them again on activation. This sample writes
no files, has no subscriptions or background work, and maintains settings only in memory.

The host owns process/session/request lifetime and serializes requests. It enforces an 8-second
request deadline. Cancellation or protocol failure retires the session, so late replies cannot
affect a later activation. This v1 protocol cancels by retiring the worker session; it has no
independent per-request cancel message or unsolicited-event stream. Keep actions bounded and
asynchronous; a future long-running feature must design its work within this contract or
explicitly add a compatible/versioned protocol. Do not block the UI thread.

On `stop`, retire owned work/subscriptions/resources, acknowledge, then exit. The host withdraws
UI and island content first, rejects new requests, cancels active requests, waits for bounded
exit, and kills an unresponsive owned process. The Windows job limits this worker tree and
ends it with the owning job; process/resource containment is **not a filesystem, network or
permission sandbox**. Only trusted official code is admitted. Do not imply third-party safety.

## Declarative UI, languages and data

`ModuleUi` contains only host-rendered pages/actions/settings, declared text keys and a permitted
icon. Use declared `text`/`boolean` settings and bounded plain text. No arbitrary XAML, callbacks,
markup code or host-service references are accepted. The host owns navigation; withdrawing a
module returns its active page to a usable built-in surface. Module keys are scoped to their
manifest; they cannot replace App resources.

Supply all ten catalog languages from `localization/languages.json` with matching meaningful
translations. The source example has eight stable keys in each language. UI resolves against
the current interface language, then English. Keep lyric translation targets independent and
permanently Simplified Chinese/English. Release bodies remain English. Review changed source
and translation IDs, placeholder structure and completeness before packaging/publication;
automatic completeness does not prove translation meaning.

`node modules/check-module-resources.mjs --manifest <module.template.json>` checks the shared
ten-language definition, exact module key sets, declarations, placeholders and confirmed
fingerprints in the adjacent `localization-reviews.json`. After actually reviewing affected
translations, confirm only those IDs with `--review --language <code> --keys-file <explicit
JSON ID array> --reviewer <identity and scope>`. Source or translation changes invalidate their
recorded hashes; the package builder and main App localization gate both fail on stale records.

Island submissions contain `Compact`, `Expanded`, declared `Actions` and `ExpiresAt` only.
This sample contributes at most 30 seconds and omits island content when its optional
`island.content` capability is unavailable. The host keeps its window/layout/animation,
priorities, drag/drop, manual page choice and focus. A worker cannot replace built-in music
or Temporary Space or create its own island window.

Keep module data within its own scoped directory; never modify host databases, original user
files or another module's data. Settings/data survive disable/uninstall. Data deletion is a
separate explicit host operation. Contract v1 requires equal data-format versions for an
update: general migrations and cross-format rollback are **not implemented**. Declare readable
data ranges accurately, preserve the previous compatible code/data during a staged update,
and reject an incompatible update before removing a working version. Breaking interface/UI/
protocol meanings require explicit contract-version increases, independent of App release
numbers. Missing required capabilities fail; missing optional capabilities require a declared
and implemented fallback (`omit` in this example).

## Verification boundary

Packaging success establishes source compilation and hashes, not a real installation or
successful functionality. The release owner records one shared project-wide passive ledger,
maximum 20 actual functional scenarios including failures/retries/Actions. The example's real
install/hello/page/action/settings/disable/reenable/uninstall cycle belongs in that ledger;
do not start a separate module budget or broad matrix. Source/static/resource checks and
production builds do not replace host compatibility and regression evidence.
