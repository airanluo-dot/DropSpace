# Round 8 — preview requests queued across cache clearing

Status: **implemented and locally verified** after final implementation, independent review,
and root authorization. One new combined scenario ran once: **passed 1, failed 0, skipped 0**.
The runner reported 94 ms; the actual TRX records 70.6615 ms for the test body. Existing restore
assets were reused. One normal targeted build completed with the project's default analyzers
and warnings-as-errors; no additional build or test run was performed.

The production change captures the preview cache generation when a request enters, before
it waits for provider concurrency. The test uses real `PreviewProviderRegistry`,
`TextPreviewProvider`, and `FilePreviewCache` against a temporary Linux directory. Two
explicitly gated provider probes occupy both worker slots, so a third request queues. Clear
removes the real seeded cache; after releasing the gate, the old requests still return their
preview with the original generation and leave the cache directory absent. A fresh request
uses the new generation and successfully writes readable cache content. Gates and pending
tasks are released/drained before deleting the temporary directory.

Executed command from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --nologo --filter FullyQualifiedName=DropSpace.Infrastructure.Tests.PreviewRegistryConcurrencyTests.QueuedLoadCannotRecreateClearedCacheButNewRequestsCanCache --logger 'trx;LogFileName=preview-generation.trx' --results-directory /workspace/DropSpace/artifacts/local-preview-generation-tests
```

Evidence: [raw build/test output](preview-generation-test.log.txt),
[verbatim TRX](preview-generation.trx.txt), and
[source hashes and execution receipt](preview-generation-validation-inputs.json).

Historical preview/cache-clear tests and the existing bounded cancellation/concurrency case
were not rerun. No broad suite ran. Windows preview rendering and UI interaction remain
unverified.
