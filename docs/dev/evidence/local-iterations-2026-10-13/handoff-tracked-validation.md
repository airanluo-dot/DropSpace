# Round 18 — observable file handoff lifecycle

Status: **implemented and locally verified within the source-linked boundary** after final
production/probe review. The new `tracked` composite executed once and passed (exit 0).
The round 17 default scenario and all other functional tests were not executed.

Two necessary build/run commands were issued with existing restore assets. The first failed
before any scenario execution: the probe's lambda parameter `scoped` conflicted with C#
syntax (`CS1001`). Only that parameter and its reference were renamed to `scopedProgress`.
After reporting the cause and receiving root authorization, the second normal build/run
succeeded with default analyzers and warnings-as-errors. No production code or assertion
was changed to pass; no restore or additional check was run.

The actual send lifetime, actual tracker, and Infrastructure transfer types were used with
controlled send tasks. Measured coverage:

- A disabled preadmission notifies retirement exactly once without starting actual work;
  the tracker becomes settled `Failed/Disabled`, and its record can be cleared.
- Two active records reject a third. Preparing, approval, sending, finalizing and completed
  states publish through actual tracker events. Byte progress and revisions advance.
  Detaching an observer leaves ownership intact, and a replacement read sees the live state.
- Cancellation wins immediately before a finalizing report: the actual helper/token fence
  prevents a controlled post-report commit marker. When finalizing wins first, cancellation
  is refused and that marker can run. This is a local fence assertion, not an HTTP commit test.
- A canceled caller can finish before actual retirement; the record remains `Cancelling`.
  Retirement after a network-stage report yields `CancelledOutcomeUnknown`. Conversely,
  actual retirement before delayed caller outcome does not prematurely settle a record.
- Direct stale tracker progress cannot replace cancellation or completion state.
- One held active record survives eleven short completed transfers; exactly ten terminal
  records remain. Clearing terminal history preserves the active owner. Its later pre-offer
  cancellation is definite `Cancelled`, and final clearing empties the snapshot.

There were fourteen actual sends plus one disabled rejected helper attempt, with exactly one
retirement callback for each of those fifteen helper attempts. The separate tracker capacity
rejection never invoked the send adapter. All controlled tasks drain in cleanup. Event-driven
barriers use no delay or polling; five-second timeouts only bound a broken probe.

The same command was issued for both attempts from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet run --project scripts/handoff-send-lifecycle-probe/HandoffSendLifecycleProbe.csproj --no-restore --no-launch-profile -- tracked
```

Evidence: [first build failure](handoff-tracked-build-attempt1.log.txt),
[successful build/run](handoff-tracked-probe-attempt2.log.txt),
[exact probe project](handoff-tracked-probe.csproj.txt), and
[final source/assembly hashes and execution receipt](handoff-tracked-validation-inputs.json).
This console probe generates no TRX.

Client network phase placement and offer-cancellation cleanup, service/use-case routing,
and native panel language/lifetime integration were reviewed statically. No client/host,
network connection, actual file transfer, peer commit, WinUI window, keyboard/accessibility
interaction or full App/XAML build was executed. The controlled commit marker does not prove
remote rollback, and snapshot rereading does not prove Windows view reconstruction.

The localization agent separately completed nine explicit changed-ID reviews and one app
integrity check: ten languages, 908 IDs. The existing [raw log](handoff-tracked-localization.log.txt)
and [twenty changed IDs](handoff-tracked-localization-keys.json) are retained. These checks are
static integrity checks, not functional scenarios, and were not repeated here.
