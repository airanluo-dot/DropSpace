# Focused pin and Undo ownership probe

```sh
dotnet run --project scripts/undo-lifecycle-probe/UndoLifecycleProbe.csproj --no-restore
```

One combined scenario links actual `UndoCoordinator`, `PinItemsUseCase`, and notification
publication code with the real Infrastructure SQLite repository. A forwarding repository
proxy holds only the return of a completed pin commit, permitting a deterministic newer
Remove attempt and postcommit caller cancellation. Assertions cover gate exclusion, exact
Pin then Remove publication order, current removal token ownership, real Undo restoring the
removed row, and the older item's pin remaining applied.

The probe uses a temporary directory and deletes it after draining the tasks and disposing
the Undo owner. It has no Windows/platform shim, network listener, production data access,
or system clipboard operation. Default analyzers and warnings-as-errors are retained.
`MainViewModel` call-site integration and WinUI interaction require separate review; this
probe does not compile or execute the full App or historical Undo tests.

The separate opt-in retention scenario uses the same actual services and SQLite repository:

```sh
dotnet run --project scripts/undo-lifecycle-probe/UndoLifecycleProbe.csproj --no-restore -- retention
```

Four temporary history rows distinguish protected old/new images, eligible recent text,
and an unprotected expired image. A postcommit pin barrier proves retention cannot enter
the repository before the unpin publishes Undo. Age/count retention then protects both
images without counting the newest protected row against other history; the expired image
and its real payload are cleaned through the actual durable outbox. Undo repins both rows.
Explicit finalization removes protection without waiting for the eight-second timer, so a
subsequent retention pass applies the normal age/count limits and physical cleanup.
Only fixture timestamps are set directly; no production clock or policy is replaced.
The original default scenario does not run in this mode. App clipboard integration and
automatic timer expiration remain outside this runtime check.
