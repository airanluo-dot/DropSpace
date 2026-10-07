# Round 3 — Core full-source review

Reviewed the immutable source snapshot for commit `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9` under `/workspace/scratch/beta18-round3/src`. This is a fresh complete Core read after PR 107/108/109, the island changes and sequential Round 1 / Round 2 fixes. Prior round reports were not used as review evidence or counted as new findings.

## Coverage and source identity

All **117 files / 14,804 physical lines** in `round3-core-scope.txt` were physically read from first through final line, including unchanged algorithms, models, policies, interfaces, project and lock files, all 5,062 OpenCC dictionary lines, the adjacent original license and README. Truncated displays were split and reread; no hash-only coverage substitutes were used. Physical lines include the final non-newline-terminated lock-file line, hence `wc -l` is one line lower.

The frozen bytes match the source manifest for **117/117 files**. The current repository Core bytes also match the frozen bytes for **117/117 files** at the completion checkpoint.

- Scope SHA-256: `6acf3f73214fcb2a22273a9b7371af1b20808291a233663c6f38a1722d140f78`
- Manifest SHA-256: `5717765305d2e94d874c3bc0a0e95d2d1f5cf5626da3eaeaa0af59b2dc78b7ed`
- OpenCC dictionary SHA-256: `9ff46a7d30e5765375eb13d33f2b03a34d298913caf2b120380679f33ae1642d`, matching its pinned README identity.

## Result

**No newly confirmed Core defect requiring a production change.** Reviewed state/cancellation and projection ownership, drag verification/completion fencing, island priority/presence/hide deadlines, lyric parsing/timing/recording matching, whole-track AI/provider admission and output identity, media playback/spectrum freshness, settings defaults/migrations/merging, placement/motion/native-region identity, clipboard bounds/propagation/loop guards, updates and widgets. Potential concerns were assessed against current source rather than promoted to speculative refactors.

Targeted caller source reads additionally checked `OverlayNativeRegionController`: a failed native region application resets the Core deduplication identity, so a retry is not suppressed. `IslandGlowController` and `OverlayWindow.UpdateGlowTarget` were read for the user's reported total glow failure; those caller reads are supplemental to the complete Core partition, and their partition owners retain the complete App coverage claim.

The complete Core glow policy has no Simple/Regular switch that suppresses eligibility: `LyricsGlowPolicy.IsEligible` permits Music mode for playing visible music; AiLyrics additionally requires an actually visible LocalAi translation. `LyricsGlowEnvelope.Advance` gives any eligible surface a positive `0.08` brightness baseline even with absent/silent capture, while `SimplifiedGlow` changes only the contour blend. Reduced motion keeps that baseline. Thus absence of loopback audio or the Core Simple/Regular envelope alone does not establish the reported total failure's cause. The end-to-end shipping activation/native-window/render investigation continues in the App reports; this Core result does not claim runtime glow success.

## Evidence limits

Source reading, file enumeration, line accounting and SHA-256 identity comparison only. **Actual executed cases: 0**, within the user's hard maximum of 21. No tests, fixtures, probes, build, application launch, native execution, remote mutation or commit occurred. No production file was edited. Static reasoning does not establish Windows/DPI/monitor/OLE/audio/GPU/native-runtime behavior or semantic model quality. Any concrete App issue discovered by the supplemental glow trace will be recorded separately rather than retroactively counted as a Core finding.

## Complete read ledger

Every range below means an actual complete physical source read. “Match” records both frozen-manifest and current-worktree SHA-256 agreement, not the reading method.

| File | Read lines | SHA-256 | Identity |
|---|---:|---|---|
| `src/DropSpace.Core/Abstractions/IAppStringLocalizer.cs` | 1–50 | `a6d7fc6ad930c8ec0f478135cf33da594838d6efec4f1190962a7680ed634ef1` | Match |
| `src/DropSpace.Core/Abstractions/IDlcPackageProvider.cs` | 1–51 | `f4a0b42695fe255cea65b11d691b36d0c382230c877b7636ba84b4f5291e289c` | Match |
| `src/DropSpace.Core/Abstractions/IFileReferenceService.cs` | 1–12 | `fc20dfd44303fc69fa91a813fa5affd398131eb45cac2fc1ebad7a537721c5b0` | Match |
| `src/DropSpace.Core/Abstractions/IItemRepository.cs` | 1–136 | `79594c59656d8c20fc552f45416924dfdfc21e26390d6dba8aa0646a8d4c4609` | Match |
| `src/DropSpace.Core/Abstractions/ILocalStorageMetrics.cs` | 1–8 | `eab37a26edbadaa3148d6755432b5550a7ac1778818b78394c32015d5572eb0c` | Match |
| `src/DropSpace.Core/Abstractions/IPayloadCleanupCoordinator.cs` | 1–8 | `d4864bb52558028e06b91772b524f5a87af07e03457a58dde35adf8b19f105cf` | Match |
| `src/DropSpace.Core/Abstractions/IPayloadCleanupRepository.cs` | 1–27 | `e4c1bfb38bb4861d3141be28200c71fa1dbc550e3cb534dddf18ccd6b36c984f` | Match |
| `src/DropSpace.Core/Abstractions/IPayloadStore.cs` | 1–30 | `401f2b14ec4b4d3c8224e67daddf1dd4710227a32ceef55f95abfa40f6eb7e27` | Match |
| `src/DropSpace.Core/Abstractions/ISettingsService.cs` | 1–25 | `3988ae2ea8a25782b4f8d92da1bd4aff52d64c94a7a9735103055d8290bef7b8` | Match |
| `src/DropSpace.Core/Abstractions/IStartupRegistrationService.cs` | 1–8 | `7c4c846fee29dc8ad484844ad6a9503c1fe051658d5bbed05e8a8a3d6e97bb73` | Match |
| `src/DropSpace.Core/Abstractions/IUpdateServices.cs` | 1–76 | `f6d9288dcb89fa5b80eff315294d6339e75189f6df30ebb826ea4d1cd4784349` | Match |
| `src/DropSpace.Core/Actions/ActionModels.cs` | 1–138 | `9da752bf9880f741389ba18b5d8ae1c563207be89c863ae747da99c22e368b11` | Match |
| `src/DropSpace.Core/Actions/ImageSizePresetPolicy.cs` | 1–16 | `dd8bb05894f6719559f23612c8731018ec4431c289eacf0e5cd2cc71f8304114` | Match |
| `src/DropSpace.Core/Actions/ItemSelectionResolver.cs` | 1–25 | `61ee2166ec37a400b2cfad54d15e0a16a276a6aa460ec26df4910fa66a8a153e` | Match |
| `src/DropSpace.Core/Actions/QuickActionPreferencePolicy.cs` | 1–174 | `86c36876dcd94d52fc4d5fefbd8799a4ddf20e5a3803e38d29a5e78a71b41a9a` | Match |
| `src/DropSpace.Core/Collections/ProjectionCollection.cs` | 1–77 | `521bd0baa84f3219f5e7fce70816bf1090a3cb2ca039f30e8435b679f74234ff` | Match |
| `src/DropSpace.Core/Collections/SerializedProjectionRefreshCoordinator.cs` | 1–223 | `d8403b97cca77e12a2e9632908a7fa03da92dde7bb184409a875db125e02864c` | Match |
| `src/DropSpace.Core/Compatibility/WindowsCompatibility.cs` | 1–95 | `1405e78369ee5bc3df01f87152111ead22fa431f304007cdbcfdde0976bc56a3` | Match |
| `src/DropSpace.Core/Content/ItemContentModels.cs` | 1–42 | `accb49bbfca97a52c2195df502d866b5ce342ed766d9f1cdeda3ef9788f60c34` | Match |
| `src/DropSpace.Core/Content/ItemContentPolicy.cs` | 1–84 | `3722d536228e8d5cf8e75f29950635aafe1e955d1b78e22809ec204f831a3839` | Match |
| `src/DropSpace.Core/Diagnostics/OperationCorrelation.cs` | 1–6 | `c7eb5098172721af7ac0f3bcc7af1b7812e7557f976fb70fda2083d08f5bd364` | Match |
| `src/DropSpace.Core/Displays/DisplayIdentity.cs` | 1–38 | `2e5c65fe63a5dd2d53fccf2eebcb4418aede62b0fadd0ede90bb800831a91289` | Match |
| `src/DropSpace.Core/Downloads/DownloadModels.cs` | 1–60 | `b97765892fac44333211cb15c26d700c394bd36a7505c62e55e88782340d3ffb` | Match |
| `src/DropSpace.Core/DragDrop/DragEvidence.cs` | 1–58 | `4a97a34b828d083c96dccb1bd420a22822f765e4469739e80db6b8f1c13f82c9` | Match |
| `src/DropSpace.Core/DragDrop/DragSessionPolicy.cs` | 1–314 | `5710b413773d6b9734a5c38251538ba9733008d09a970ab4ea69637a50f92d8e` | Match |
| `src/DropSpace.Core/DragDrop/DragSignalQueue.cs` | 1–64 | `27753fb0c5e1853bbc0d5c0522fa71b82af06c80bb97518ce4585d6554521631` | Match |
| `src/DropSpace.Core/DragDrop/OleFileDataKind.cs` | 1–45 | `33d09903bdad165339cd2771ca5f7b45d0953b7f1cbf036f14f4a41172e0edff` | Match |
| `src/DropSpace.Core/DropSpace.Core.csproj` | 1–10 | `98b2c232696f89e500b50e6f5de002afa121f8223f0021baa40070f526fdc01b` | Match |
| `src/DropSpace.Core/Island/IslandContentSelectionPolicy.cs` | 1–16 | `51fc726f5030342f3948062c98387940e28d9a3686a1b44555e72f5c768fb20f` | Match |
| `src/DropSpace.Core/Island/IslandExperienceCoordinator.cs` | 1–163 | `bc0672fee740052ce22d4411b9ed93cc8103204721c78319f4f66cd99d7ed0ff` | Match |
| `src/DropSpace.Core/Island/IslandGeometry.cs` | 1–28 | `86816c4d30f441845e1d490f6aca879c6f789ed9c3fb1c81a7ae021353cc7f23` | Match |
| `src/DropSpace.Core/Island/IslandPageTransition.cs` | 1–30 | `c954c2e10f7fd8b1a5227754a96549adfe19e2b50780ec118cc5d60a70cc3cbc` | Match |
| `src/DropSpace.Core/Island/IslandPresencePolicy.cs` | 1–32 | `6a23451ac470049f546254be3f023a0d40e876dea0bf6ab7b0e0a50e9be89df1` | Match |
| `src/DropSpace.Core/Lyrics/AiLyricsModelCatalog.cs` | 1–47 | `d4da0a37896a1bb2158739fcff7278a3eb351351464a04f98a8c78ff6acad1e2` | Match |
| `src/DropSpace.Core/Lyrics/AiLyricsSelectionModelCatalog.cs` | 1–24 | `5296fc3dde61e136f507b54abe59805cc0f5bc661704be47a19faa0fa6e7728f` | Match |
| `src/DropSpace.Core/Lyrics/ArtistCreditOrthography.cs` | 1–39 | `b934f7b4401447160c5c8b3318ae23f7fc602ee2c80f907e67a25789e3c46e6a` | Match |
| `src/DropSpace.Core/Lyrics/Data/OpenCC-LICENSE.txt` | 1–56 | `b534e465949558eec2597b04f5092b5e161236a68dfbfd04d547592ac3964308` | Match |
| `src/DropSpace.Core/Lyrics/Data/README.md` | 1–11 | `7f4e90e042d152cfc52aeb0021ed8bde2f3ed8a34cf2b2b517c296551038edc2` | Match |
| `src/DropSpace.Core/Lyrics/Data/TSCharacters.txt` | 1–5062 | `9ff46a7d30e5765375eb13d33f2b03a34d298913caf2b120380679f33ae1642d` | Match |
| `src/DropSpace.Core/Lyrics/LyricsBodyQualityPolicy.cs` | 1–30 | `7c2621570ad32cda2395e06d15785de5dc41c753462ff6f0d4944bc3787a0a35` | Match |
| `src/DropSpace.Core/Lyrics/LyricsCandidateSelection.cs` | 1–178 | `8a711c7f533d6cc5d2d4984b7615e5296ad8ceef04b533b71ebb86f47d1d318b` | Match |
| `src/DropSpace.Core/Lyrics/LyricsDisplayPolicy.cs` | 1–80 | `ddd8f7828bcdf2004a39df9daaf8f86585c3a4ec164764982b40c3ca19c6ebef` | Match |
| `src/DropSpace.Core/Lyrics/LyricsGlowAudioResponse.cs` | 1–42 | `4e8e1ddbf7c1bf5c058dc6a95c8b975ae0d6e0bc6e6d1bc6b0b82df21eb27793` | Match |
| `src/DropSpace.Core/Lyrics/LyricsGlowEnvelope.cs` | 1–63 | `2349b2c7a710a354ccb7180f1e17d0543dd5dd43c858be4a95af744b2ea32b57` | Match |
| `src/DropSpace.Core/Lyrics/LyricsGlowHandoff.cs` | 1–25 | `4a3c9806a639f28d29129d80be8e1b8e69cab75e038d5ae3f40410c5f5a5c1b4` | Match |
| `src/DropSpace.Core/Lyrics/LyricsGlowPolicy.cs` | 1–35 | `fde145eed2cfc45a4b0eb237c7e49ac7512c7e430932d73ee5155e3cab68ae56` | Match |
| `src/DropSpace.Core/Lyrics/LyricsInferenceCircuit.cs` | 1–29 | `4c4920ff3a22333f165d7d262af874646d627ae9a0e565e7063a0535c14b603f` | Match |
| `src/DropSpace.Core/Lyrics/LyricsLanguagePolicy.cs` | 1–263 | `6325cd8ee4836800d61b3bd4a53e96b9d352083b18469c8b63e6c6bd03fcd403` | Match |
| `src/DropSpace.Core/Lyrics/LyricsMarqueePolicy.cs` | 1–42 | `2e08aa3422da7b4fe3e0976a3c2b834bca845b740bd79f2e5cc40099d98b81a2` | Match |
| `src/DropSpace.Core/Lyrics/LyricsMatcher.cs` | 1–425 | `9a9193cf878315ad278d5187995df4680e330f25b3b0e79c95cfe3ca7f323808` | Match |
| `src/DropSpace.Core/Lyrics/LyricsModels.cs` | 1–154 | `f345092c3350a8777010dc09ca034822a4507031d631ceb3119334ae5d06f7aa` | Match |
| `src/DropSpace.Core/Lyrics/LyricsParser.cs` | 1–394 | `aba6d73e80d29faf2890a6be61a50d9078f76e5a3b4a6a289d92551fc4952fa4` | Match |
| `src/DropSpace.Core/Lyrics/LyricsPreviewPolicy.cs` | 1–48 | `7a6aa89a739109d0829e9bb03c4dfe50ada9d7facf4610218a8ce5fadc79cc10` | Match |
| `src/DropSpace.Core/Lyrics/LyricsReloadPolicy.cs` | 1–19 | `34901858edafb3838478e7b93e508d470ce51e2d63a8b3aea3b10e00b8304123` | Match |
| `src/DropSpace.Core/Lyrics/LyricsTranslationOutput.cs` | 1–145 | `3c844fb1e509e3fd46bf2424d81038803c67d2e614e2883afc56e4921e8d6d3d` | Match |
| `src/DropSpace.Core/Lyrics/LyricsTranslationPolicy.cs` | 1–86 | `65526a4ec97b66566d785fdc62b791ef2371189cc64c337e6e77469e36538601` | Match |
| `src/DropSpace.Core/Lyrics/LyricsTranslationPrompt.cs` | 1–128 | `247eef339dd4e9338d13e14c9e90f21a0b4a3a2e464bfdf747e8401c65bd4bfb` | Match |
| `src/DropSpace.Core/Lyrics/LyricsWholeTrackAdmission.cs` | 1–25 | `52c681606f57c6132a5e1c2831b8313007647481ffda8a1c3f80d1080410ed29` | Match |
| `src/DropSpace.Core/Lyrics/PlainHyLyricsProtocol.cs` | 1–81 | `eb74a244df42c90bf63b7f5609e5a7b4d6b2e81445a2a9a987d9a071d16a0a93` | Match |
| `src/DropSpace.Core/Media/AudioCaptureRecoveryPolicy.cs` | 1–9 | `3ee408e6437a48dd9268e044e1cc6a9e5d1333a2f9a8f774db8e6968df408b62` | Match |
| `src/DropSpace.Core/Media/MediaModels.cs` | 1–106 | `d1ccf827bef19e41a7ba038ec90d94cb5ee163b8430eef5761bcd0194be8f0d7` | Match |
| `src/DropSpace.Core/Media/MediaPlaybackClock.cs` | 1–91 | `678a6467c076b1c225f65f43badd5cb7506919461dd384ef41d907e421a31555` | Match |
| `src/DropSpace.Core/Media/MediaProcessIdentityPolicy.cs` | 1–19 | `d9b4d0321aab25ea3863071b96e82486c1994279c05db976ad09c77f574bcfbc` | Match |
| `src/DropSpace.Core/Media/NeteaseEnhancementModels.cs` | 1–39 | `ee856866cd855c771a1286669c099812f90bfd7b45a74bb55072d0957110e081` | Match |
| `src/DropSpace.Core/Media/SpectrumAnalyzer.cs` | 1–65 | `80aadd3395e2a06eed62f3c3777ef924ac89617eef54023131c7ac9870734b37` | Match |
| `src/DropSpace.Core/Media/SpectrumFreshnessPolicy.cs` | 1–22 | `85c7702ab6dacd6c3b95f0e03bb028aaa2a701722df4cab2111b8c99afe06d3c` | Match |
| `src/DropSpace.Core/Models/AppSettings.cs` | 1–336 | `4f70285f72f750b08bd486fd4136bb45c0744b9f3e0a1d7695b31d1fed3cf4fe` | Match |
| `src/DropSpace.Core/Models/Capabilities.cs` | 1–31 | `9fab4f64af236eef3425ec9ec77fa4d9fc794eaf8254a1c8644beadf1a96de85` | Match |
| `src/DropSpace.Core/Models/DomainModels.cs` | 1–211 | `d0feb608165dd0060a3db3b54aa3d9c15d89c3637419b2368d0ab6ef2a1e5a9f` | Match |
| `src/DropSpace.Core/Models/IslandAppearanceMigration.cs` | 1–20 | `32b3e7406d150b0e18a550dd964fadee8eb5ae83a2999130ecb759a4b9bb16bf` | Match |
| `src/DropSpace.Core/Models/NativeIslandSettings.cs` | 1–86 | `ee9b24239181060cdf66b78557408199991e989e4cdee9fcc65fe479f40676a0` | Match |
| `src/DropSpace.Core/Models/NativeIslandSettingsPolicy.cs` | 1–70 | `f9cd119e65eb01d4537e95ea29eff69ae635e66c78a1932e7cea3c9d0e2386ae` | Match |
| `src/DropSpace.Core/Models/OverlayPlacementEditSession.cs` | 1–111 | `ac5ea8d802b9c40fb9339e6d309d696cdcdeb8d8be19c3b24e6e84c6a9d40b08` | Match |
| `src/DropSpace.Core/Models/SettingsChangePolicy.cs` | 1–105 | `9b31445804f5b624c0d1658bab396c1eab9a9878e55c705525badb47bfeb9572` | Match |
| `src/DropSpace.Core/Models/SettingsMigration14.cs` | 1–13 | `0390d928512c6271e346c79f206e5d5bf3aa40c7c790a03d905c6fc62bb932cd` | Match |
| `src/DropSpace.Core/Models/SettingsMigration15.cs` | 1–29 | `f3c6e56d90507e23a81fa1ee552669fc41b72aa34dbc84411e67a7c487c73069` | Match |
| `src/DropSpace.Core/Models/SettingsValidationPolicy.cs` | 1–89 | `925525eb1b236e988c86d9f2b8a6353e3c704cdc5d2a4dd0844c48ce8a6787d3` | Match |
| `src/DropSpace.Core/Overlay/FullscreenOverlayPolicy.cs` | 1–23 | `889913284cd35713004090bf6417664aea284690ba7899d1c88b338fe2e3bbca` | Match |
| `src/DropSpace.Core/Overlay/FullscreenWindowClassifier.cs` | 1–41 | `f5b340dc58e24cfbb7fa8f356b603d6423676aecf37dae4b99a078e1fd231fe5` | Match |
| `src/DropSpace.Core/Overlay/OverlayContentPose.cs` | 1–13 | `543dd031b4483af3f797bb46fdf40cbb2547176acbc93f1917abbbc74ecad4f4` | Match |
| `src/DropSpace.Core/Overlay/OverlayFrameGeometry.cs` | 1–59 | `5d27d8bbb099e66df4dd8a8a99bf8cc7e03a390d0130dbde15c7a0c9a053620b` | Match |
| `src/DropSpace.Core/Overlay/OverlayFramePacer.cs` | 1–92 | `be9837db78a0e6f4fd1ac1508ecab164f9fa55bccaa5a4d863bfce2b9cdcbad7` | Match |
| `src/DropSpace.Core/Overlay/OverlayMotionController.cs` | 1–349 | `480366de46346f67fbcf87a38578cdeaf957403b145e2c394ee77b9f2fb9d2e2` | Match |
| `src/DropSpace.Core/Overlay/OverlayMotionProfiles.cs` | 1–236 | `5f2185c5138c958834d8d341d0556644c13aa90ebe2437ab428628634105b4b5` | Match |
| `src/DropSpace.Core/Overlay/OverlayPlacementPolicy.cs` | 1–154 | `55c41d3c3b72d03f6530f394207445a679fa07b9e2ee132a7a1339b56a1418eb` | Match |
| `src/DropSpace.Core/Overlay/OverlayRegionSignature.cs` | 1–110 | `bafaa9a63356214813bbdf341807a748078c5b92ae58b2be63742d9a13a1dc6f` | Match |
| `src/DropSpace.Core/Overlay/OverlayStateMachine.cs` | 1–249 | `9358858006674bc7791d651f2df655f2e7ec853432db7fb32b771c54375248fd` | Match |
| `src/DropSpace.Core/Policies/AppLanguagePolicy.cs` | 1–40 | `34388c915128a9f818385256101c7a507908f89816d98296da25ad3b57a2dedd` | Match |
| `src/DropSpace.Core/Policies/ConsecutiveClipboardCaptureCoordinator.cs` | 1–78 | `24264eda53784dc48b3c8d4719e0d069f0eecf56367d3f0f88d1c21d79e2d6a6` | Match |
| `src/DropSpace.Core/Policies/ContentClassifier.cs` | 1–200 | `36a2bfd5e785b1b52201f6cf4767c22b1a8ec9d9d994afd5922bb7b94b0df569` | Match |
| `src/DropSpace.Core/Policies/FingerprintService.cs` | 1–17 | `bd57894efe90595359bf659e9f3169aa5eea26af3b9bb7db1242c59f8843f80d` | Match |
| `src/DropSpace.Core/Policies/LogRedactor.cs` | 1–37 | `87b32276d8372b6b25d43b20e4feb788569b3b2290e4566f631f7ed9b86ed0b5` | Match |
| `src/DropSpace.Core/Policies/PayloadPathPolicy.cs` | 1–34 | `979ea4dbef94e27ddcd722447fd8b055b63947c4ba231f539f5374c3a21b3b67` | Match |
| `src/DropSpace.Core/Policies/RetentionPolicy.cs` | 1–26 | `06c9a3fa8a9a8433e46e2b73ea1d7ae1892205db028c8e16e482a3ce1d7b2df8` | Match |
| `src/DropSpace.Core/Policies/SearchNormalizer.cs` | 1–52 | `2347eb989d41ac0085f48beab3fc23730d4249bb909351adb99adda736286ce6` | Match |
| `src/DropSpace.Core/Preview/PreviewModels.cs` | 1–165 | `14e5bc5aac72d96a78144649efd2b76ab70d82794748d0911575bb407175722f` | Match |
| `src/DropSpace.Core/Shell/ShellIntakeModels.cs` | 1–139 | `f53adb9d2c6df4312a5f83ce398e5eef2fbce411baa3411c748740ba846cbcda` | Match |
| `src/DropSpace.Core/SystemActivities/SystemActivityModels.cs` | 1–11 | `c6627953bd031c1418f5257dcc2dbf734cfe2726e73a512874a10e23bd5c9b18` | Match |
| `src/DropSpace.Core/Transfer/ClipboardImageBudgetPolicy.cs` | 1–126 | `fd73608379e37a6a28e56d65501651aac9be55f28ff581da0e6ef5e3a4d2ae43` | Match |
| `src/DropSpace.Core/Transfer/ClipboardLoopGuard.cs` | 1–94 | `0977dca070be67f48ba728cd61de3081730a39c1bc2f15daef28f42192159685` | Match |
| `src/DropSpace.Core/Transfer/ClipboardPausedException.cs` | 1–6 | `1bdf9565c649d99df608d449b81b071fdb89373de4967d1c79715d80eb131707` | Match |
| `src/DropSpace.Core/Transfer/ClipboardPropagationQueue.cs` | 1–75 | `50842a0579d2f32659ed876a0dfe90fd3175cf416d0af767f25ebbeec3d5c7b3` | Match |
| `src/DropSpace.Core/Transfer/HandoffMessagePolicy.cs` | 1–122 | `c348cdf5573b0838363c63f897899745fc831352d4134af379855faeaf003a2f` | Match |
| `src/DropSpace.Core/Transfer/TransferModels.cs` | 1–286 | `4b8dd5d5e8d085d7531b64e7b7061a392facfa12cb4a2744772485f86b99fb6d` | Match |
| `src/DropSpace.Core/Transfer/TransferPolicies.cs` | 1–257 | `a6080397879fda3fb74d8059da3259d0bf307fb4c5b040120aacfb2569cdb4a4` | Match |
| `src/DropSpace.Core/Undo/UndoModels.cs` | 1–16 | `daf0e0e8b230b140b4c170ddcb8a605808da79423f01a211c9f8d427ebe9b450` | Match |
| `src/DropSpace.Core/Updates/DeploymentModeResolver.cs` | 1–23 | `d422f7f20f64d40ab503689e1e25f33c66d4eafb153a5197d19d5b6a4634f2bf` | Match |
| `src/DropSpace.Core/Updates/ReleaseVersion.cs` | 1–101 | `0b40d84ade296f759192cbdc775ef8b5487e37e47569960161ee76d7dd29f1db` | Match |
| `src/DropSpace.Core/Updates/UpdateChannelJsonConverter.cs` | 1–27 | `3e30f0bae063be27e74c06e1589acc967e1c9662ad6e51eef7e644fc52ae7c42` | Match |
| `src/DropSpace.Core/Updates/UpdateInstallerArguments.cs` | 1–21 | `db0d1d0bb05ca859f0a251578259f693c15f2b6b46c7a7d4706f9fb4dd92b55b` | Match |
| `src/DropSpace.Core/Updates/UpdateModels.cs` | 1–87 | `490cda29570ad7fc343bdf982fe9ec8614ab8a3b9ceed7a16d2eea2eeeae512e` | Match |
| `src/DropSpace.Core/Updates/UpdateReleaseSelector.cs` | 1–32 | `a6bd000f9f01dceacb104df51eb02ff2be02df1363a25db446c7b50e3b4cf09e` | Match |
| `src/DropSpace.Core/Widgets/WidgetCatalog.cs` | 1–25 | `f5eecc451496a4ead15b610acad7cefe666f80d9cbcfd98a41feffc7494aa83e` | Match |
| `src/DropSpace.Core/Widgets/WidgetCountdown.cs` | 1–23 | `310303ddde7b4e44aa402aa8b51530cab692762e47a01a1fee8a0c57ed43053a` | Match |
| `src/DropSpace.Core/Widgets/WidgetDataSnapshot.cs` | 1–4 | `37535c6f4e71aec42f4fa9b2e2dd8dc5ff8f0efb6d72216028e6dce70e2e6c7d` | Match |
| `src/DropSpace.Core/Widgets/WidgetModels.cs` | 1–196 | `29e2265f7014da2547aa6c52456b9062b20378a0ad417a797231e688ee065c3e` | Match |
| `src/DropSpace.Core/packages.lock.json` | 1–6 | `03eeadc5ef377c17f787ab65f41fb4c8a9c936bb7f7f4171111fdeec8a81cb46` | Match |
