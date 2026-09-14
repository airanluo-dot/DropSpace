# Network, sharing, and clipboard boundaries

Read this file only for cross-device clipboard, DropLink, Nearby Share, Internet Share, pairing/authentication, network service lifecycle, or related privacy/security work.

Use current source, tests, `ARCHITECTURE.md`, `PRIVACY.md`, and `DECISIONS.md` as the exact protocol truth. Do not treat old release notes or old Skill snapshots as a protocol specification.

## General trust model

- DropSpace remains local-first. Network features must be explicit product capabilities, not an implicit path for ordinary local data.
- Treat peer input, clipboard content, filenames, URLs, metadata, manifests, chunks, and browser/backend responses as untrusted.
- Bound payload size, item count, concurrency, queue depth, retries, lifetimes, and temporary storage.
- Never log raw clipboard payloads, secrets, authentication material, or full private user paths as routine diagnostics.
- Do not claim a backend, firewall rule, local peer, browser capability, or remote transfer works unless that layer was actually exercised or otherwise evidenced.

## Cross-device clipboard

- Reuse the event-driven clipboard watcher; do not introduce polling.
- Pause/disable state is a commit/send barrier, not a cosmetic setting. Recheck enabled/pause/peer state immediately before durable or remote effects where races are possible.
- Automatic propagation must remain bounded and loop-resistant.
- Stale remote content must not overwrite newer local state merely because delivery was delayed.
- Automatic and manual payload size limits may differ, but both must be explicit and bounded in current code/tests.
- Disabling the feature should unsubscribe/stop owned work and drain or cancel pending automatic work according to current lifecycle semantics.

## DropLink and peer trust

- Pairing must require explicit user confirmation of the current trust ceremony/SAS on both peers before durable trust is stored.
- Keep pending pairing secrets ephemeral until confirmation succeeds; rejection, cancellation, expiry, malformed input, and failure must clean up partial state.
- Authentication/replay protection must validate peer identity and input shape before consuming bounded replay state.
- Transfer staging must remain confined to DropSpace-owned storage until integrity and user-approval requirements are satisfied.
- Accepted transfers commit only after the current whole-item/file integrity contract succeeds.
- Cancellation, reconnect/handoff state, and session cleanup must be finite and owned; do not leave orphan listeners, staging files, timers, or replay entries.
- Protocol/cryptographic framing is compatibility-sensitive. Before changing it, inspect both producer and consumer code, tests/vectors, migration compatibility, and `DECISIONS.md`. Do not restate cryptographic byte layouts in this Skill unless they become a durable cross-repository contract with no better canonical home.

## Nearby/browser sharing

- Nearby sharing should stay constrained to its intended local/private-network trust boundary and use expiring, revocable, unguessable authorization according to current source/tests.
- Range/resume behavior, receiver limits, and revocation must remain bounded.
- Do not broaden LAN exposure or add automatic firewall/elevation behavior without explicit product intent and target-host validation.

## Internet Share

- Internet Share remains client-encrypted according to the current protocol. The server/reference worker must not gain plaintext access as a shortcut.
- A reference backend implementation in the repository is not proof that a production backend is deployed or reachable.
- Preserve explicit revocation and bounded server/session quotas.
- Browser receive paths must avoid unbounded in-memory aggregation. Prefer streaming-to-destination when the browser supports it and keep an explicit bounded fallback otherwise.
- Content names/metadata must remain data, not executable HTML. Preserve CSP/output-encoding protections in the browser receiver.
- Deployment-level abuse/rate-limit controls are operational evidence, not something source inspection can prove.

## Secrets and persisted security state

- Never print or commit private keys, tokens, credentials, or Actions secrets.
- Persisted sensitive handles/secrets must use the repository's current protected-storage boundary and user scope.
- Failed initialization must unwind partial ownership and remain retryable where current product semantics require it.
- Settings changes involving network services should apply/rollback through the existing coordinator/policy path rather than ad hoc partial mutation.

## Source safety

Network/share actions must not mutate an external source file as a side effect of sharing. Any generated/transformed output must be DropSpace-owned or explicitly user-selected, collision-safe, and cleaned up if incomplete.

## Validation

Automated tests can prove parser, framing, replay, bounds, cleanup, and state-machine contracts. They cannot alone prove:

- two real Windows devices can discover/pair/reconnect
- firewall/network topology behaves as expected
- a reference Internet Share backend is actually deployed
- the target browser can stream/save/decrypt the intended payload
- long-running network/resource behavior is healthy

Keep those claims conditional until the relevant target-host evidence exists.
