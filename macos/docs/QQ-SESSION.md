# QQ Music session and delivery constraints

Fixed source: Windows v0.3.2 Beta7,
`1cd4620dd58a67b0963b73487743137813b8864a`.

The user explicitly asked to **skip QQ Music login for now on2026-10-20**. Login/handoff/account UI work is deferred; this is not a completed login capability. Existing lower-layer work below is retained. The native
session owner and authenticated lyric request path are implemented; there is
currently no user-facing browser handoff, native acceptance dialog, account
verification/retry/sign-out card or real QQ account acceptance evidence.

## Implemented native ownership

`QQMusicSession` owns a serialized credential store, generation, access state and
30-second429 cooldown. `QQMusicCookiePolicy` preserves the six frozen names,
four domain scopes, root-only path,64 input-cookie limit,4096-character safe ASCII
value bound, exact expiry and last duplicate replacement. The UIN fallback/leading
`o` removal and signed32-bit GTK hash follow the frozen rules. Header domain order
was corrected against executed Windows code, including duplicate names on
different domains. Credential objects redact description and reflection.

The Keychain adapter stores a device-local, nonsynchronizing generic password
under an independent Mac QQ service/account. Development profiles get separate
namespaces. Save/sign-out report success after persistence; failed removal keeps
the prior state. Corrupt storage reports StorageError. Stale responses cannot
renew a signed-out/replaced generation. Server-issued renewal/deletion preserves
expiry rather than inventing a new lifetime. URLSession-coalesced cookie fields
are parsed with Foundation before field/domain validation.

`NativeLyricsService` supplies the same session to `QQMusicLyricsProvider`.
`LyricsHTTP` uses it only for the fixed HTTPS QQ endpoint, retains the shared
connection/bandwidth/deadline budgets, sends no QQ cookies to other lyric sources,
rejects credential redirects and fences queued/completed requests by generation.
UIN/GTK payload and cookie header are taken from the same request snapshot.
Authentication failures/cooldown update the session; unrelated business refusal
does not invent credential expiry.

One explicit native boundary is stricter than the old writer: incoming serialized
sessions are capped at64KiB **before** writing. The Windows reader has this bound
but its writer can exceed it. Native rejection preserves the prior session and
avoids creating unreadable credentials. This difference still needs inclusion in
the final reviewed difference list; it is not silently described as byte-for-byte
disk compatibility.

## Evidence

- `qq-session-policy-tests.log`:3 named policy/storage tests passed0.004s.
  `qq-session-integration-compile.log` reran the same three after service wiring,
  passed0.004s.
- `qq-authenticated-transport-tests.log`:1 actual URLSession/protocol-fixture
  provider test passed0.019s. Checks renewal between search and lyric requests,
  header isolation, redirect refusal,429 and signed-out connection waiters.
  It uses synthetic cookies and does **not** prove real HTTPS/account access.
- `generate-qq-session-fixtures.py` executes the exact fixed C# source's Validate,
  Identity and CookieHeader methods, with a deliberately nonexecutable DPAPI
  stub. Initial fixture program failed on C# collection syntax; corrected.
  The initial20-case comparison exposed two header-order differences; extended
  to23 cases and fixed native domain grouping.
- `qq-frozen-rules-transport-retry-tests.log`:3 selected tests passed0.015s,
  covering23 executed source cases ×7 destination headers, request UIN/GTK bytes,
  combined server-cookie fields and renewal/deletion.
- `qq-keychain-native-tests.log`:2 named tests passed0.059s. One checks corrupted
  storage/oversized rejection; one actually writes, reopens and removes a unique
  Keychain item containing an invented local-only ticket. No real account,
  browser cookie store or network credential transmission was used.
- `qq-session-native-integration-build.log`: native app build succeeded.
  No QQ login/interface/permission/first-install pass is implied.

Deferred / remaining: native handoff protocol and user consent UI; browser packaging,
signed distribution and real browser communication; credentials-change retirement
and native account-state UI; real signed-in search/lyrics, expiry, refresh,
sign-out, Keychain refusal/recovery and full cancellation/lifecycle acceptance.

## Safari constraint and proposed alternative

The previously selected Safari extension cannot be treated as a normally
installable ad-hoc component under this release's current constraints. Apple says
Safari ignores unsigned extensions by default; development requires allowing
unsigned extensions, and that setting resets when Safari quits. Outside-store
distribution uses a Developer ID signed/notarized containing application.
Sources: [Running a Safari web extension](https://developer.apple.com/documentation/safariservices/running-your-safari-web-extension),
[distribution](https://developer.apple.com/documentation/safariservices/distributing-your-safari-web-extension).

The user's ad-hoc policy remains valid for the **main Mac application**. Do not
make a paid Apple account a prerequisite for that application, claim that ad-hoc
identifies an Apple-verified developer, or require users to weaken browser/OS
protections to disguise the extension constraint.

The earlier proposal is now **deferred with the login feature**, not approved or implemented: a Firefox
helper signed through Mozilla's Add-on Developer Hub and self-distributed with
the official GitHub release. Only a user action on the QQ Music site would read
the six allowed session fields; an allowlisted native-messaging helper would
deliver them to a native confirmation and Keychain owner. No product web UI,
background cross-site cookie collection or unsigned-extension preference changes.
This requires a Mozilla account/submission agreement, signed artifact and actual
Firefox/native-host acceptance. No claim of Safari equivalence is made.
Mozilla supports [signed self-distribution](https://extensionworkshop.com/documentation/publish/signing-and-distribution-overview/)
and [installation from a signed file](https://extensionworkshop.com/documentation/publish/install-self-distributed/).
