# Round 20 — nearby share management lifecycle

Status: **server management implemented and locally verified** after final production/test
review and root authorization. One exact Infrastructure composite ran once and passed:
1 passed, 0 failed, 0 skipped (exit 0). Runner duration was 267 ms; the individual TRX duration
was 246.1806 ms. One necessary normal build/test reused existing restore assets. Default
analyzers and the existing test warning configuration were retained. No failure or retry occurred.

The test exercises actual `NearbyShareServer` and Kestrel with a three-byte in-memory source:

- Initial snapshot and descriptor reads leave the address resolver uncalled, `BaseUri` null,
  and no expiry timer armed.
- Creation publishes accurate metadata and a fresh explicit descriptor. Serialized metadata
  contains neither its URL nor its bearer token.
- An actual file GET succeeds through the unchanged private-receiver admission rule. After
  revoke, the list is empty, fresh descriptor lookup fails, a change notification is observed,
  and a later GET of that same previously usable URL returns HTTP 404.
- Two live shares expiring at one and two minutes reuse one one-shot timer. A manual
  `TimeProvider` advances the real server expiry callback. Notifications arrive before any
  snapshot read, so lazy pruning cannot substitute for timer behavior. The first expiry
  preserves the later share and rearms its deadline; the second empties the list and disarms
  the timer. No wall-clock delay or polling is used.
- A final share is invalidated by invoking the existing network-change handler through
  reflection. Its metadata, fresh descriptor and advertised endpoint disappear, a notification
  is raised, and the timer stops. Normal server disposal completes and the owned timer
  reports disposed; socket closure is not independently probed afterward.

Requests remained on this machine's private IPv4 address with proxy use disabled. Loopback
was not substituted, and the production private-receiver rule was not weakened. Four tiny
shares and two HTTP requests were used; no external peer or public endpoint was contacted.

Executed command from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --nologo --filter FullyQualifiedName=DropSpace.Infrastructure.Tests.NearbyShareManagementTests.MetadataRevokeAndExpiryFollowTheRealShareRegistryWithoutReadSideStartup --logger 'trx;LogFileName=nearby-management.trx' --results-directory /tmp/dropspace-round20-results
```

Evidence: [raw output](nearby-management-test.log.txt), [actual TRX](nearby-management.trx.txt),
and [frozen server/test source hashes and execution receipt](nearby-management-validation-inputs.json).
Historical nearby range/network cases and other test suites were not run.

The server/test inputs were frozen for the single successful run before the App UI completed
its independent review. Those hashes remain unchanged. After UI/resource review passed,
their final hashes were appended separately with a capture timestamp; this was a read-only
evidence addition, not another build or functional execution.

The localization agent's existing nine explicit reviews and single app integrity gate passed
for ten languages and 920 IDs. Twelve IDs are new; the existing zh-TW `RevokeShareButton`
translation was also corrected. Copied evidence includes the
[raw log](nearby-management-localization.log.txt),
[exact commands](nearby-management-localization-commands.json),
[new IDs](nearby-management-localization-keys.json), and
[zh-TW reviewed IDs](nearby-management-localization-zh-TW-keys.json).
These are static integrity results and were not rerun.

This proves the managed server path on Linux, not an actual OS network-adapter transition,
cross-device reachability, Windows firewall behavior, or system timer thread scheduling.
Already-admitted streaming requests, queued stale timer callbacks, and disposal reentrancy
were reviewed statically without additional dynamic fixtures. App service forwarding and the
final native management UI were reviewed statically. Actual Windows dialogs, layout, focus,
clipboard actions, QR rendering, keyboard/accessibility and full App/XAML compilation remain
unverified; neither the server test nor localization integrity certifies those interactions.
