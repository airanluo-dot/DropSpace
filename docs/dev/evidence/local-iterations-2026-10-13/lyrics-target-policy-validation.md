# Round 23: lyrics interface language and translation target policy

Status: final. One existing Core test method was corrected and executed once; production policy and settings are unchanged. This is independent of round 22's App event handling change.

The first exact `--no-restore` command exited 1 before compilation or test execution. Core.Tests had an old failed `project.assets.json` with zero package libraries, pointing to `/workspace/.cache/nuget` and carrying NU1301 / proxy 403 diagnostics. Its raw output is retained in [lyrics-target-policy-build-attempt1.log.txt](lyrics-target-policy-build-attempt1.log.txt). No TRX was produced by that attempt.

Read-only inspection confirmed all 13 exact locked package versions, metadata and package hashes were present in `/home/agent/.nuget/packages`. One authorized locked restore used an explicitly empty local feed and that existing cache; `NuGetAudit=false` applied only to this invocation to avoid audit network access. Restore exited 0; repository lock files and Infrastructure.Tests assets remain byte-for-byte unchanged. No repository dependency or analyzer setting changed.

The second identical test command completed one normal Core.Tests build with default analyzers and executed only `DropSpace.Core.Tests.LyricsReloadPolicyTests.InterfaceLanguagePreservesLyricsWhileTranslationSettingsRetireThem`: **passed 1, failed 0, skipped 0**, exit 0. Runner duration: 40 ms; test TRX duration: 19.5017 ms. This means two test-command attempts, one successful compilation, one functional execution, and zero failed functional executions.

Assertions cover interface-language changes retaining lyrics, explicit English-to-Simplified-Chinese translation-target changes requiring reload, and the four existing AI model / GPU / translation-enable / provider changes requiring reload. Other Core methods were not run. Round 21 was not repeated. Round 22 App signal merging, WinUI/XAML, actual playback and translation/network behavior remain outside this check.

Raw restore and successful execution outputs are [lyrics-target-policy-restore.log.txt](lyrics-target-policy-restore.log.txt) and [lyrics-target-policy-test-attempt2.log.txt](lyrics-target-policy-test-attempt2.log.txt); the actual test result is [lyrics-target-policy.trx.txt](lyrics-target-policy.trx.txt). [lyrics-target-policy-validation-inputs.json](lyrics-target-policy-validation-inputs.json) records complete commands, exits, actual counters, assembly hash and pre-execution source hashes. All frozen sources and captured restore inputs were checked unchanged afterward.
