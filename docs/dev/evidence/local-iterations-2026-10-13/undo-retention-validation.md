# Round 25: pin Undo and retention ownership

Status: final. The single new opt-in `retention` scenario passed, exit 0, after one necessary
normal build with default analyzers and warnings-as-errors. No restore or retry ran.

The existing Undo probe links actual UndoCoordinator and PinItemsUseCase, references actual
Infrastructure SQLite/payload cleanup, and retains default analyzers. Four temporary rows
cover protected old/new image payloads, eligible recent text and an unprotected expired
image. The existing forwarding proxy holds the return of a real unpin commit. Retention is
invoked directly and reaches its held coordinator gate before returning; its repository
counter must remain zero until the barrier releases. After both operations finish, protected
rows/payloads survive, the newest protected row does not count against unrelated history,
and only the unprotected expired payload enters and drains from the actual deletion outbox.
Undo restores both pins. A second unpin followed by explicit finalization permits normal
age/count retention and physical cleanup without waiting eight seconds.

Only this new scenario executed, using existing assets and one necessary normal build.
No restore, historical default scenario, full App build, Windows clipboard or real user data
is involved. ClipboardCaptureService routing and seven constructor fixture adaptations are
reviewed separately; this probe does not compile those App files. Automatic timer expiry and
replacement by another Undo operation remain static-only, with explicit finalization used
as the runtime boundary.

Actual results: four temporary rows and three owned three-byte payloads were created.
The first retention pass removed only the unprotected expired image, and the real outbox
drain deleted its payload. Both protected payloads stayed byte-for-byte intact; Undo restored
two pins. After a second unpin and explicit finalization, ordinary retention removed the old
image and count-excess text; the second real outbox drain deleted the old image payload.
Exactly the newest image row and its original payload remained, with no pending outbox entry.
The fixture used image metadata without decoding image content.

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet run --project scripts/undo-lifecycle-probe/UndoLifecycleProbe.csproj --no-restore -- retention
```

[undo-retention-run.log.txt](undo-retention-run.log.txt) is the actual successful console
output. This console probe has no TRX. [undo-retention-validation-inputs.json](undo-retention-validation-inputs.json)
records the exact command, measured counts and assembly hash; pre-execution hashes are
also preserved in [undo-retention-frozen-inputs.json](undo-retention-frozen-inputs.json).
All frozen compiled and static-only inputs remained unchanged after execution. The separate
ClipboardCaptureService and App fixture hashes do not imply those files were compiled or run.
