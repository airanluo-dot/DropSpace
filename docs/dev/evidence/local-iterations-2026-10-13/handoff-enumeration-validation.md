# Round 16 — shared handoff preparation limits

Status: **implemented and locally verified** after final production/test review and root
authorization. One new combined scenario ran once: **passed 1, failed 0, skipped 0**. The
runner reported 56 ms; actual TRX test duration is 29.5716 ms. Existing restore assets were
reused for one normal targeted build with default analyzers and warnings-as-errors. No
failed or repeated execution occurred.

The test uses only production `EnumerateSources` against temporary local filesystem trees.
Each selected root plus its empty child and regular file fits exactly three visited nodes;
combining both roots exceeds a shared four-node allowance and reports the typed traversal
reason/limit. An empty child still triggers the configured 64-character path limit. A real
40-level tree with 26-character segments produces a relative path above 1024 but below 2048:
the explicit 2048 configuration succeeds, while the default 1024 reports the typed path
reason/limit. An already-canceled token rejects admission. No test-only synthetic root-name
overload or large node fixture was added. The production factory's `MaxItems + 4096` formula,
long arithmetic and single client budget across roots were statically reviewed.

Executed command from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --nologo --filter FullyQualifiedName=DropSpace.Infrastructure.Tests.ReparseSafeDirectoryEnumeratorTests.SharesTraversalBudgetAcrossRootsAndHonorsConfiguredDirectoryPaths --logger 'trx;LogFileName=handoff-enumeration.trx' --results-directory /workspace/DropSpace/artifacts/local-handoff-enumeration-tests
```

Evidence: [raw build/test output](handoff-enumeration-test.log.txt),
[verbatim TRX](handoff-enumeration.trx.txt), and
[source hashes and execution receipt](handoff-enumeration-validation-inputs.json).

The localization agent separately completed nine explicit review-record commands and one
`--scope app` integrity check: 10 interface languages, 887 App IDs, all exit 0. Its actual
[raw localization log](handoff-enumeration-localization.log.txt) was copied by the root agent;
these checks were not repeated here and are not functional scenarios or proof of semantic
quality. Independent semantic/UI review preceded the review records.

Existing item/byte/reparse cases and a large 10,000-file fixture did not run. Linux filesystem
coverage does not establish Windows long-path behavior. MainPage's typed-error message and
language wiring were reviewed statically; no WinUI/XAML compilation or interaction, peer,
server, network transfer, or real user data was involved.
