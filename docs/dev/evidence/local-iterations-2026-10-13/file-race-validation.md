# File reference and card publication races — validation

This independent iteration executed **2 unique functional scenarios, once each, both
passing**. There were no failed runs or retries. Earlier media iteration counts are not
included here. No broad suite or remote workflow ran.

## Actual executions

1. One locked restore of `tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj`
   succeeded, followed by its normal build (including Core, Infrastructure and the resident
   worker fixture) and an exact-name filter selecting **only**
   `DropSpace.Infrastructure.Tests.RepositoryRelinkAuditTests.LateAvailabilityForReplacedFileCannotChangeNewReference`.
   Result: **1 passed, 0 failed, 0 skipped**, test run summary **125 ms**. Default compiler
   and analyzer settings were retained.
2. The focused `ItemCardLifecycleProbe` restored/built and executed **once**, passing its
   single combined delayed-publication scenario. It links the production card and quick
   action view models, the actual Core project and CommunityToolkit.Mvvm **8.4.2**.
   Bitmap and storage objects are identity-only platform shims.

Raw outputs are saved as `file-race-restore.log.txt`, `file-race-cas-test.log.txt` and
`file-race-ui-probe.log.txt`. `file-race-cas.trx.txt` is a verbatim copy of the actual TRX.
`file-race-validation-inputs.json` records the results and SHA-256 of compiled/linked
production source, test/probe files and separately identified review-only caller files.

## What the scenarios establish

The SQLite scenario uses a real temporary database and real temporary files. After file A
is replaced by B, A's completed availability check cannot change B's entire stored record,
including reason and last-checked metadata. Wrong path and kind also fail without mutation;
a current observation succeeds and increments revision; repeating its old revision fails.
The test cleanup removes only its generated temporary data.

The card scenario holds a result behind a `TaskCompletionSource`: relinking immediately
clears old thumbnail/drag assets and rejects their delayed publication. Pinning while a
same-content result is pending preserves the result; stale revisions cannot undo the pin.
An equivalent overlay card without loaded assets preserves current assets, while an older
reference cannot merge back. Retirement rejects old callbacks and old records; a newer
authoritative revision restores the card without reviving the old generation.

## Commands and boundaries

Commands used SDK **10.0.401** at `/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet`:

```sh
dotnet restore tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --locked-mode --nologo
dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --nologo --filter FullyQualifiedName=DropSpace.Infrastructure.Tests.RepositoryRelinkAuditTests.LateAvailabilityForReplacedFileCannotChangeNewReference --logger 'trx;LogFileName=file-race-cas.trx' --results-directory /workspace/DropSpace/artifacts/local-file-race-tests
dotnet run --project scripts/item-card-lifecycle-probe/ItemCardLifecycleProbe.csproj --no-launch-profile
```

The UI probe validates the production card's managed publication rules. It does not compile
or run the complete `MainViewModel` or `OverlayViewModel`, nor Windows thumbnail decoding,
Windows drag/drop, WinUI rendering, real file relink/open/delete interactions or Undo UI.
Those callers were integrated and reviewed separately; a full Windows application build
and interaction validation remain unexecuted in this Linux environment. These results
must not be described as full application or real Windows UI verification.
