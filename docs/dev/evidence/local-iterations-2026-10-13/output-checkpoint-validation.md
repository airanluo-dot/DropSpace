# Round 19 — output checkpoint and publication ownership

Status: **implemented and locally verified** after final production/test review and root
authorization. One exact Infrastructure test ran once and passed: 1 passed, 0 failed, 0
skipped (exit 0). Runner duration was 54 ms; the individual TRX duration was 30.7312 ms.
Existing restore assets were reused for one necessary normal build/test. Default analyzers
and the existing test-project warning configuration were retained. No failure or retry occurred.

The test uses the real reservation service and the same temporary three-byte staged file
through one serial composite:

1. A checkpoint is held by a completion signal, cancellation is requested, then the signal
   is released. Publication throws cancellation. Staged bytes and the original owned marker
   remain unchanged, with no output or alternative reservation.
2. The next checkpoint creates an external destination then throws an `IOException`.
   The exact exception instance propagates, the callback runs once, and no alternative
   marker is allocated. Staged bytes, original marker and external bytes all remain intact.
3. A subsequent normal retry resolves the existing destination through the real `PathExists`
   collision branch. The checkpoint records the eventual output path; the staged payload
   is moved there, external bytes remain untouched, and both old/new owned markers disappear.

The held task is canceled/released/drained before temporary files are deleted. There is no
delay or polling; timeout bounds only guard a broken fixture.

Executed command from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --nologo --filter FullyQualifiedName=DropSpace.Infrastructure.Tests.OutputReservationLifecycleTests.CheckpointCancellationAndFailurePreserveOwnershipWhileRealCollisionsStillCommit --logger 'trx;LogFileName=output-checkpoint.trx' --results-directory /tmp/dropspace-round19-results
```

Evidence: [raw output](output-checkpoint-test.log.txt), [actual TRX](output-checkpoint.trx.txt),
and [final source hashes and execution receipt](output-checkpoint-validation-inputs.json).

No historical case was run. The ordinary build also compiled referenced projects, but no
resident worker or network service was started. DownloadManager journal integration and the
instantaneous `File.Move` collision catch were reviewed statically; the latter was not forced
at runtime. Linux temporary-file results do not validate Windows file-sharing failures or
native UI interactions. No production data was touched.
