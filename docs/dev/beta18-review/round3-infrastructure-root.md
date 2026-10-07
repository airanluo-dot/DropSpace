# Round 3 root Infrastructure supplement

Baseline: `f22bcc4bdd5c2d477a7880fe02c87f6035392ab9`. Fresh full physical read of all nine Updates files and four Sharing files, 13 files / 3,357 lines, from the frozen Round 3 snapshot. All 13 SHA-256 values match the round manifest. No additional confirmed defect; no tests or runtime execution.

Coverage included update single-flight waiter ownership, cancellation/disposal, manifest/version/hash validation, recovery/install state; nearby server network rebind, bounded late cleanup and ranged streams; secure-share staging, durable revoke capacity, crypto and upload recovery. Findings from previous rounds were rechecked without repeating their count.

| File | Read lines | SHA-256 |
| --- | --- | --- |
| `src/DropSpace.Infrastructure/Sharing/InternetShareRevokeStore.cs` | 1–393 (complete) | `b7ad1a8c30eefbcd94cc3961baeff553ec584de8f60a64a51bcfd52d4bc0b6ee` |
| `src/DropSpace.Infrastructure/Sharing/NearbyShareServer.cs` | 1–507 (complete) | `d0f918ec99a03acee9ec5d7059be24cd7db43d3099f6e670a0dbd7eeed40216c` |
| `src/DropSpace.Infrastructure/Sharing/ShareCryptoService.cs` | 1–265 (complete) | `9ea8593a5b334aef02b0a5bfa2e6dfc01a5eb51f9966d948aa746789df701180` |
| `src/DropSpace.Infrastructure/Sharing/ShareUploadCoordinator.cs` | 1–406 (complete) | `633e5e01bbb38f16a5e3195b31d6d288e1404592847f004c8c65740fe4ac28f9` |
| `src/DropSpace.Infrastructure/Updates/GitHubReleaseUpdateSource.cs` | 1–132 (complete) | `af76ce89f826efe4ea7cd430eab4345b66100d26e709f9f1125aa737c4c2cfe3` |
| `src/DropSpace.Infrastructure/Updates/HttpUpdateDownloader.cs` | 1–121 (complete) | `2b07d619c6a84c60fca14d52193d15352d4d1c3565870d6c552bf806a4fbc4e7` |
| `src/DropSpace.Infrastructure/Updates/OfficialWebsiteReleaseUpdateSource.cs` | 1–176 (complete) | `e8e135b8d5b5aa8b732dcad6e492b046340af646017f3e69b78cef881439ca6c` |
| `src/DropSpace.Infrastructure/Updates/ResilientUpdateSource.cs` | 1–123 (complete) | `8ae37cf32296faa1545d31005b994fb57778bfe65f0a306a9eb75eb0776b8354` |
| `src/DropSpace.Infrastructure/Updates/UpdateFileVerifier.cs` | 1–43 (complete) | `908c7b61762b9ab47d3b02fd7232cdbb596124249e9740ba665dfa6bd9c7cce6` |
| `src/DropSpace.Infrastructure/Updates/UpdateManifestParser.cs` | 1–174 (complete) | `e3d330d2d3baa2af330d9ca483a578e2918043b98d77e922cc17548ffae939a1` |
| `src/DropSpace.Infrastructure/Updates/UpdateMetadataReader.cs` | 1–32 (complete) | `8f0b86d3c3033fe9f7214d5b875dc6383c4ee6c3881327066cf0ab3d1b2f09a6` |
| `src/DropSpace.Infrastructure/Updates/UpdateService.cs` | 1–731 (complete) | `18f510e71f45e13837e33cbed06a5806f89b245a657b5f23ab6ad80d0ca3db4a` |
| `src/DropSpace.Infrastructure/Updates/UpdateStateStore.cs` | 1–254 (complete) | `16b64d23f80983d72aa2981375cd8f50e41f749d549aa58b419c4a985e537b20` |
