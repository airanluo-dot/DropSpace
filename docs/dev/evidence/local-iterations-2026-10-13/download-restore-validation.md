# Round 11 — late download recovery preserving live ownership

Status: **implemented and locally verified** after final production and test review and root
authorization. One new combined scenario ran once: **passed 1, failed 0, skipped 0**. The runner
reported 336 ms; the actual TRX records 313.9088 ms for the test body. Existing restore assets
were reused for one normal targeted build with default analyzers and warnings-as-errors.
No failed or repeated execution occurred.

The test reuses `DownloadManagerLifetimeTests` ownership reflection helpers and its fixture
with the HTTP listener disabled. Holding the sole shared transfer slot keeps a real admitted
run queued. Late recovery preserves the exact Work, Run, CTS and output reservation without
duplicating the visible row. A separate new-ID journal entry recovers as Paused with no active
run/source. Cancel settles the original run, disposes its CTS and removes its reservation
marker without producing an output. HTTP connection count stays zero; the only occupied
transfer slot belongs to the fixture. Its failure cleanup directly cancels/drains the original
owner before releasing that slot.

Executed command from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --nologo --filter FullyQualifiedName=DropSpace.Infrastructure.Tests.DownloadManagerLifetimeTests.LateRestorePreservesAdmittedRunOwnershipAndStillRecoversNewTasks --logger 'trx;LogFileName=download-restore.trx' --results-directory /workspace/DropSpace/artifacts/local-download-restore-tests
```

Evidence: [raw build/test output](download-restore-test.log.txt),
[verbatim TRX](download-restore.trx.txt), and
[source hashes and execution receipt](download-restore-validation-inputs.json).

The hidden failed-admission cleanup owner is covered by static review of the unconditional
existing-ID guard, not by an additional scenario. No real HTTP, listener, historical download
case, remote endpoint or broad suite ran. Windows download UI and restore cancellation/retry
semantics remain unexecuted.
