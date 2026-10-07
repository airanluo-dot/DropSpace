# Round 3 — App Services media subset full-source review

Reviewed the immutable source snapshot at commit `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9` under `/workspace/scratch/beta18-round3/src`. This fresh read is a delegated subpartition of the complete App Services review, performed after the Core partition finished; it is credited to Services rather than counted as additional unique Core coverage. Prior round reports and issues were not used as new review evidence.

## Coverage and source identity

All **33 files / 6,473 physical lines** in `app-services-media-scope.txt` were physically read from first to final line. This includes unchanged audio/COM code, DLC providers and manager, every Media service, every NetEase deployment/probing/verification file, and all diagnostic source, including the 717-line visual diagnostic. Larger files were read in nontruncated consecutive chunks; hashes are identity checks, not substitutes for source reading.

The frozen bytes match the source manifest for **33/33 files**. Current repository bytes also match frozen bytes for **33/33 files** at this completion checkpoint.

- Scope SHA-256: `c8266bf89302fe3c190a3695212eaa12a2e81e1f87bdaf59f7ee8a00bcfffefd`
- Manifest SHA-256: `5717765305d2e94d874c3bc0a0e95d2d1f5cf5626da3eaeaa0af59b2dc78b7ed`

## Result

**No newly confirmed defect requiring a production change in this subpartition.** Fresh review covered bounded native-operation ownership through cancellation and timeout; subscription retirement and restart readiness; coalesced media metadata/discovery/artwork reads; same-track renderer selection; track/generation/source fences for provider and AI lyric publication; cache-maintenance drains; process loopback activation memory, capture transition and recovery ownership; bounded logging; DLC operation/progress ownership; NetEase prepared-package validation, transactional receipts/backups/rollback and process-generation shutdown; Windows capability observation and playback restoration. Source-only candidates were not promoted to unsupported fixes.

The user's total glow failure was traced through `MediaExperienceService`: the overlay-owned `MediaViewModel.IsIslandGlowActive` event queues the capture transition and starts the media frame timer when music is playing. Capture demand accepts either visible spectrum or active island glow, so hidden spectrum alone does not prevent glow capture. Process-resolution or capture failure yields unavailable audio, while Core's eligible glow envelope still supplies its positive baseline. Media observation publishes playback/title and island experience on the dispatcher before optional lyric/artwork completion. These paths do not establish a shared Simple/Regular total-failure cause. Native halo presentation, WinUI placement/material settings and overlay activation remain with the Services/UI partition owners; no Windows runtime success is claimed.

A source-only publication-window question remains for cross-owner assessment: `WindowsMediaSessionService.CompleteArtworkAsync` checks the metadata revision before returning, while its single consumer subsequently uses an unconditional final `Publish`. A native metadata event can arrive between that check and publication. This does not prove that a newer published track is overwritten because the consumer is serialized, and it was not classified as a confirmed defect or used to justify a production edit.

## Evidence limits

Source reading, line accounting, file enumeration and SHA-256 identity comparison only. **Actual executed cases: 0**, within the user's hard maximum of 21. Diagnostic and verifier source was read but never invoked. No tests, fixtures, probes, build, app launch, Windows/native/audio/GPU execution, remote mutation or commit occurred. No production file was edited. Static review cannot establish live Windows/DPI/COM/audio behavior, deployed package contents, third-party player response, or model semantics.

## Complete read ledger

Every range means an actual full physical source read. “Match” means both frozen-manifest and current-worktree SHA-256 agreement.

| File | Read lines | SHA-256 | Identity |
|---|---:|---|---|
| `src/DropSpace.App/Services/Audio/ProcessLoopbackInterop.cs` | 1–65 | `1020f4b73ca334962b6739c9443b47d0060f7684fffe529b222807073f7ca50f` | Match |
| `src/DropSpace.App/Services/Audio/WindowsProcessLoopbackService.cs` | 1–207 | `ba9e4c4b870eaa1b4f13501ed8bfc6ff0140b2f8c5d4333d71e076c14e3240dc` | Match |
| `src/DropSpace.App/Services/Diagnostics/LyricsLanguageSmoke.cs` | 1–66 | `edddd4b5e15185a475a9147d9ec6a0e51877129bac306405aa25cb701ebb7bfb` | Match |
| `src/DropSpace.App/Services/Diagnostics/MusicVisualSmoke.cs` | 1–717 | `6e85e1b29e9cb7d74d145cff743539d4b85c003b3848e3a1bf981fa021e1e9ae` | Match |
| `src/DropSpace.App/Services/Diagnostics/MusicVisualSmokeOptions.cs` | 1–56 | `a81a7255e95de8482695452bc07b634115b8ad04f011edeec99b69f69d38c7be` | Match |
| `src/DropSpace.App/Services/Dlc/AiModelDlcProvider.cs` | 1–41 | `3f2faa16daf4d1b88c74cf0fabd55a0e6b7399c179dfa20df639a31b4b264d06` | Match |
| `src/DropSpace.App/Services/Dlc/CudaRuntimeDlcProvider.cs` | 1–49 | `ac2c99e244e69ddffe1f5092aee03c69f9b86cb39aa21e583f1d7cd6fcbad2eb` | Match |
| `src/DropSpace.App/Services/Dlc/DlcManagerService.cs` | 1–312 | `46d2a8aba2b593fbab124007861e36b99b02ba09e267a8d483eb0c5177f5a3e5` | Match |
| `src/DropSpace.App/Services/Dlc/DlcProgressPresentation.cs` | 1–20 | `c2d5614d0d151f7aae65185df2af60af32528d96167eb4b3570ce078f8f77aa4` | Match |
| `src/DropSpace.App/Services/Dlc/NeteaseComponentsDlcProvider.cs` | 1–111 | `1d512abfa495beef41e27f19e3b3b39d7b51332261f601b25faff6907402754e` | Match |
| `src/DropSpace.App/Services/Media/AiLyricsService.cs` | 1–525 | `eb5d166548c0b1397b29ff77d223b190e9b01bb156f5a58ca5cf2027fc4c632e` | Match |
| `src/DropSpace.App/Services/Media/BoundedMediaOperation.cs` | 1–103 | `3172f2bd158c224cec6cb32d77c1277c0555b9711803998c2eefad4dfdabb458` | Match |
| `src/DropSpace.App/Services/Media/LyricsRapidSkipDiagnostic.cs` | 1–70 | `869b946716489c0ca5f9d0bf4516c53b322502f501f84b8162268371100b6f9d` | Match |
| `src/DropSpace.App/Services/Media/MediaApplicationIconService.cs` | 1–48 | `54b946faa152248cca5e17e9fe1d37fea432811e66c09773396aa21e12aa826b` | Match |
| `src/DropSpace.App/Services/Media/MediaArtworkService.cs` | 1–38 | `446850e05dc242c59e6dd227d52c114975ebff4487ec9d01b9d8dccbb296194a` | Match |
| `src/DropSpace.App/Services/Media/MediaEventSubscription.cs` | 1–49 | `a721740c62be19691ce081e8c03b40379e8c1db10f2aee6a8f7d86b6a6ace14e` | Match |
| `src/DropSpace.App/Services/Media/MediaExperienceService.cs` | 1–777 | `dc02a90cf354be40187372c7cd8c081da6ee385f3f30fc3bc81359903351c249` | Match |
| `src/DropSpace.App/Services/Media/MediaLyricsRefreshRequest.cs` | 1–19 | `9b17cf7d1ee9b4ff7dc83ce376766a000e56ee4570bb607bdeddb64ef382e333` | Match |
| `src/DropSpace.App/Services/Media/MediaProcessResolver.cs` | 1–61 | `03862b3f9d6f2b13029a4218b430f2c7ae13dcf5b8fef5ce3232cb16a54ea260` | Match |
| `src/DropSpace.App/Services/Media/MediaSessionOwner.cs` | 1–53 | `685d876f8bc5b0770bee0a2c8c14d3baccfb974a89919c6e0e1649fe2b2aab93` | Match |
| `src/DropSpace.App/Services/Media/MediaSoftRestartOperation.cs` | 1–67 | `1d31d215b06d8eda111539e4f12eecf3cfa0d719aca024623eaa320a459a4992` | Match |
| `src/DropSpace.App/Services/Media/MediaSubscriptionAdmission.cs` | 1–12 | `be3a7f766a3d8496596b82bfcb1f477c1d12559f41ff9e98004e378e2017ae33` | Match |
| `src/DropSpace.App/Services/Media/QqMusicLoginService.cs` | 1–198 | `1ca2b62a87820a38f437c2f5c69e35566c72d002b1977398df8eeecc03c078ba` | Match |
| `src/DropSpace.App/Services/Media/RetirableMediaWork.cs` | 1–127 | `c7e13f6c993744e7018b1fe9610b1d5fc1cddf0504a86840d3d1ccdb03a89022` | Match |
| `src/DropSpace.App/Services/Media/WindowsMediaSessionService.cs` | 1–882 | `fe6153cb0fb4ba879bf16f77edcf461a63286a4bbba782d58325e6bab6f38bc6` | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/BetterNcmProbe.cs` | 1–31 | `69caf929cb7cb6e86ca7598966bc7333a9a6306afd8fb7cb193ad716dacc6fec` | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.IO.cs` | 1–278 | `f176058fd249acd1bf3561becabe8d548d9b3857ff89f68fdbde445bbafd27f2` | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.Packages.cs` | 1–170 | `769005a4ecf64617db1493c3e1a9d62e7959d85e4f5fe6ca94130c64f2803fb5` | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/InfLinkDeploymentService.cs` | 1–224 | `9d691a669a0cea397098fdedfb5b6d6f52f18cc440570f6d3872f49969c5a449` | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseEnhancementService.cs` | 1–236 | `1ff2bdf74417f6f7454e6bcc4b84b5ebbf1f03592544a782ae0e27195c52c348` | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseInstallationProbe.cs` | 1–90 | `76338b660d5c706e479e6f33c0bb0e086f349ff90fd791ed8d54bd02c196ba53` | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseRuntimeInstaller.cs` | 1–79 | `a33cb72669adb6a6a06aa9e232d0d9ea931c0399ffc92e2d01cdf2b10ef81d45` | Match |
| `src/DropSpace.App/Services/NeteaseEnhancement/NeteaseSmtcVerifier.cs` | 1–692 | `98e459f2d069027882e6b0bc11a0d08b0c11e10fe54d2060ccb74f9256cf2ff8` | Match |
