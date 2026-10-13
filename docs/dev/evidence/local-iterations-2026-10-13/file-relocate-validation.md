# Conditional Locate and restored-item lifetime — validation

This follow-up round executed **1 new functional scenario, once, passing**, with no failures
or retries. It reused the prior successful locked restore. Normal Core, Infrastructure,
resident worker fixture and Infrastructure test compilation succeeded with the usual
compiler/analyzer settings. The exact filter executed only:

`DropSpace.Infrastructure.Tests.RepositoryRelinkAuditTests.LateRelinkCannotReplaceNewReferenceOrRevivedItem`

Result: **1 passed, 0 failed, 0 skipped**, test summary **133 ms**. The scenario uses actual
SQLite and temporary files: a current conditional replacement succeeds; a delayed previous
reference cannot overwrite it; pending deletion blocks replacement; after delete/Undo,
old replacement and availability observations are rejected and the new revision works;
clipboard clear/Undo also rejects the old observation. Original file bytes remain intact.

Raw output is `file-relocate-cas-test.log.txt`. `file-relocate-cas.trx.txt` is copied verbatim
from the actual TRX. Source hashes and the actual TRX counters are in
`file-relocate-validation-inputs.json`.

Executed with .NET SDK 10.0.401, using its previously recorded local executable path:

```sh
dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --nologo --filter FullyQualifiedName=DropSpace.Infrastructure.Tests.RepositoryRelinkAuditTests.LateRelinkCannotReplaceNewReferenceOrRevivedItem --logger 'trx;LogFileName=file-relocate-cas.trx' --results-directory /workspace/DropSpace/artifacts/local-file-race-tests
```

The earlier availability-CAS test and ItemCard probe were **not repeated**. SHA-256 checks
confirm `ItemCardViewModel`, `QuickActionButtonViewModel` and all four ItemCard probe files
are unchanged from their prior passing inputs, so those results are reused. This does not
claim an identical Core binary: its repository interface changed and compiled in this run.
No new UI interaction was executed.

The final `MainViewModel` Locate caller and preservation of non-null same-generation optional
assets were statically reviewed, and the final source hash recorded.
This Infrastructure build does not compile `MainViewModel`, WinUI XAML or the complete App.
Real Windows picker/Locate/delete/Undo and cross-card interaction paths remain unexecuted.
No media checks, full suites, production user data or remote workflows were involved.
