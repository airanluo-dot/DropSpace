# Round 7 — explicit ZIP directory-link selection

Status: **implemented and locally verified** after implementation and independent/root
review. One new combined scenario ran once: **passed 1, failed 0, skipped 0**. The test runner
reported 71 ms; the actual TRX records 50.5958 ms for the test body. Existing restore assets
were reused. The normal targeted build completed with the project's default analyzers and
warnings-as-errors; no additional build or test run was performed.

The test uses the real action registry and actual temporary Linux directory symlinks.
Explicit directory-link selection alone, and mixed selection with the ordinary directory
first, both reject as `ActionInvalidData`, leave no output, and preserve source and link-target
bytes. Selecting the ordinary directory containing a nested link succeeds under the existing
skip-link policy; its archive contains exactly `Source/` and `Source/file.txt` with the original
file content. The Windows fixture capability branch was not executed, and the test was not
skipped.

Executed command from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --nologo --filter FullyQualifiedName=DropSpace.Infrastructure.Tests.ZipActionServiceTests.RegistryRejectsSelectedDirectoryLinksWithoutPartialOutputAndStillSkipsNestedLinks --logger 'trx;LogFileName=zip-selection.trx' --results-directory /workspace/DropSpace/artifacts/local-zip-selection-tests
```

Evidence: [raw build/test output](zip-selection-test.log.txt), [verbatim TRX](zip-selection.trx.txt),
and [source hashes and execution receipt](zip-selection-validation-inputs.json).

Windows junction behavior and the Windows UI command interaction remain unverified. No
historical ZIP tests, including the 10,000-directory entry-budget scenario, or broad suite ran.
