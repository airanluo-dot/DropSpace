# Round 21 — recovered ready updates survive metadata checks

Status: **implemented and locally verified** after final production/test review and root
authorization. Inputs were hashed before execution and confirmed unchanged afterward. One
exact Infrastructure composite ran once and passed: 1 passed, 0 failed, 0 skipped (exit 0).
Runner duration was 141 ms; the individual TRX duration was 120.4211 ms. One necessary normal
build/test reused existing restore assets, default analyzers and existing test warning settings.
No failure, retry, restore or unrelated test run occurred.

The composite uses actual `UpdateService`, `UpdateStateStore`, `UpdateManifestParser` and
`UpdateFileVerifier`, with tiny temporary files and real SHA-256 values. Measured behavior:

- A persisted payload recovers as ready after actual integrity verification. An offline startup
  check retains the same `Download` object while reporting the automatic check failure.
- Sole-caller cancellation is followed by awaiting the actual existing shared check task,
  rather than relying on the caller's prompt return. Ready state and the previous check time
  are restored; the controlled source has zero active calls afterward.
- A complete same-version manifest with matching payload retains the ready object even with
  automatic download/install enabled. Recovered publisher trust remains false. An empty
  release list also preserves the valid cached payload.
- A new version and a same-version changed payload SHA each produce `UpdateAvailable` without
  retaining the old `Download` when automatic downloading is disabled.
- Deleting the real cached file after recovery prevents reuse despite matching metadata;
  the candidate remains available for a future download.
- A subsequently recovered higher beta payload is not retained when checking the stable
  channel with no applicable release.

The metadata source is controlled and never contacts its fixture URLs. Strict downloader,
publisher-verifier and launcher fakes were each called zero times. This does not constitute
actual downloading, signature validation, installation or deployment. Temporary files are
removed only after the service's owned work is disposed.

Executed command from the repository root:

```sh
/workspace/.tools/dotnet-deb/usr/share/dotnet/dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj --no-restore --nologo --filter FullyQualifiedName=DropSpace.Infrastructure.Tests.UpdateReadyRecoveryTests.RecoveredPayloadSurvivesChecksWithoutReuseAcrossPayloadOrChannelChanges --logger 'trx;LogFileName=update-ready.trx' --results-directory /tmp/dropspace-round21-results
```

Evidence: [pre-execution hashes](update-ready-frozen-inputs.json),
[raw output](update-ready-test.log.txt), [actual TRX](update-ready.trx.txt), and
[final execution receipt](update-ready-validation-inputs.json).

Portable/packaged compatibility, integrity-verifier exception branches and subsequent
download/install error recovery were reviewed statically, without further dynamic cases.
No Windows update UI or full App/XAML compilation was performed. The separate round 22
`MediaExperienceService` App-only change (`cfeb744`) is outside this Infrastructure test's
coverage. No project version, remote release or user installation was changed.
