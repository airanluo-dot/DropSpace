# 第一轮 Core / Infrastructure 静态审核

审核时间：2026-10-01 19:34–19:46 UTC。范围为 Core、Infrastructure 及两者测试。审核者只读实现，按主任务要求另写本报告。其他实现改动来自主审核任务，不归为本审核者修改。

## 完成范围与证据边界

- 169 个生产 C# 文件完整语义阅读：Core 90、Infrastructure 79
- 起始 119 个测试 C# 文件完整阅读：Core 57、Infrastructure 62
- 审核期间另读新增 PairingRoundTripNativeTests.cs 与 ClipboardWireBudgetTests.cs，当前测试阅读数为 121
- 4 个 csproj 完整阅读；4 个 packages.lock.json 按依赖、版本、哈希字段元数据审阅，不声称审计第三方实现或实时漏洞状态
- 当前精确覆盖清单在文末，合计 298 个源/配置文件；排除 bin/obj
- 根 AGENTS.md、规范项目技能及 app-ui 引用已读取；相关架构、产品、UX段落和历史报告用于解释行为。历史报告及搜索命中没有计入当前源码语义覆盖
- 当前未提交 Core / Infrastructure 改动已复核，包括未决删除恢复、单字号、模型目录、空 LRC 时间边界与缓存路径处理
- 本审核没有运行构建或测试套件，没有执行双机协议、Windows DPAPI、安装更新、在线歌词提供者或真实模型推理。主任务执行结果必须另行记录，不能归入本报告的执行证据
- 唯一执行的行为核查为内存 Python SQLite 比较：含 É/é 的路径在 NOCASE 下不相等，ASCII 大小写对照相等。它证明 SQL 比较差异，不代替 Windows 文件系统或真实仓库回归
- 这是第一轮中的本分区静态覆盖，不代表完整项目第一轮结束，更不表示三轮或原生验收通过

## 确认问题

### R1-CI-01：配对确认响应的 PeerId 角色相反

- 严重度：P1
- 原位置：Infrastructure/Network/DropLinkClient.cs:95；DropLinkHost.cs:263–273
- 客户端要求 response.PeerId 等于 offer.LocalHello.DeviceId，即接收端 ID；Host 原来返回 peer.Id，该值来自 pending.RemoteHello.DeviceId，即发起端 ID
- 两台不同设备完成 SAS 确认后，接收端已保存 Trusted/共享密钥，发起端却抛 UnauthorizedAccessException，产生单边配对
- 最小修复：Host 返回自己的 pending.LocalHello.DeviceId，使返回语义和客户端校验一致
- 回归：真实 Client↔Host TLS 成功握手，双端确认、信任及共享密钥一致；既有配对测试没有覆盖完整成功交换
- 状态：主任务已实现 Host 返回 pending.LocalHello.DeviceId，本审核已读差异确认。新增 PairingRoundTripNativeTests.cs 已完整阅读，Windows执行待主任务。主任务随后补入ClearAllPools清理、双端Trusted与密钥一致性断言，本审核也已复核

### R1-CI-02：手动剪贴板图像额度超出 HTTP 接收预算

- 严重度：P2
- 位置：Core/Transfer/TransferPolicies.cs:149；Infrastructure/Network/DropLinkClient.cs:373；DropLinkProtocolContract.cs:78；DropLinkHost.cs:129；DropLinkAuthenticationMiddleware.cs:81–91
- 调用证据：App/Services/CrossDeviceClipboardService.cs:103–110、196–222 的手动发送使用50 MiB图像额度，client把byte[]序列化为Base64 JSON，但Host/鉴权总body仅16 MiB + 64 KiB
- 13 MiB原始图像仅Base64就有18,175,320字节，超过16,842,752字节请求上限，必然413
- 最小修复：为剪贴板路由设置包含编码/JSON开销的独立额度，或分块；不要无差别放大其他路由额度
- 回归：完整序列化/接收的边界内、边界外与手动50 MiB策略一致性测试
- 状态：主任务已按clipboard路由引入Base64/JSON额度，Host总上限允许该额度，鉴权仍对其他路由使用旧额度。本审核复核了三个生产文件差异及新增ClipboardWireBudgetTests.cs；它验证13/50 MiB完整序列化大小与路由别名，未运行也未验证真实接收端吞吐

### R1-CI-03：上传失败且撤销失败后丢失撤销能力

- 严重度：P2
- 位置：Infrastructure/Sharing/ShareUploadCoordinator.cs:90–93、111–127；App/Services/SecureInternetShareService.cs:54–63
- 远端session创建后只在局部变量中；上传失败后的best-effort撤销若也因断网失败，该异常被吞掉；撤销句柄仅全部上传成功返回后才落盘
- 影响：上传中断网或进程退出可留下用户无法再次主动撤销的远端对象，只能等待过期。加密不恢复撤销控制
- 最小修复：创建session后、首次上传前持久化已预留容量的撤销记录；确认撤销成功才删除
- 回归：上传及撤销同时失败、进程重启恢复撤销；同时验证成功路径不会遗留重复记录
- 状态：失败路径源码确认，未执行故障注入；截至检查点未复核修复

### R1-CI-04：NetEase别名可绕过版本冲突校验

- 严重度：P2
- 位置：Infrastructure/Lyrics/NetEaseLyricsProvider.cs:75–81、54–55；LyricsService.cs:123–131
- 构造：query为Song/Artist，候选name为Song (Live)、alias为Song，artist/duration相同。规范名被拒，但alias得分12并作为Match.Title保存；二次校验也看不到Live证据
- 影响：录音室歌曲可显示并缓存现场/混音版本歌词
- 最小修复：alias仅帮助名称匹配，规范名的版本证据独立保留和检查
- 回归：规范名含Live/Remix而alias不含版本的provider fixture；既有测试只有别名恢复与Core单独拒绝Live，未覆盖组合
- 状态：主任务已在别名打分前加入规范name版本冲突检查，将Core谓词显式复用，新增Live/Remix provider fixture。本审核已复核生产与测试差异，未运行用例

### R1-CI-05：TTML相对时间以数值大小推断

- 严重度：P2
- 位置：Core/Lyrics/LyricsParser.cs:315–329
- 构造：p begin=1s，span begin=2s/end=3s，相对模式应为3–4秒，当前输出2–3秒。代码仅当parsed小于parentStart时相加
- 最小修复：明确支持的绝对/相对时间方言或解析模式，不按两个数字大小猜测，同时保留Apple绝对时间兼容
- 回归：父1秒/子2秒、相等边界及嵌套层级；现有测试为父10秒/子1–3秒，漏掉边界
- 状态：现有支持边界缺陷，非本次空LRC补丁引入；未运行，截至检查点未复核修复

### R1-CI-06：空LRC结束边界修复遗漏负offset

- 严重度：P2
- 位置：Core/Lyrics/LyricsParser.cs:153–169 与 :33–36
- 复现输入：

```lrc
[offset:-3000]
[00:01]first
[00:02]
[00:10]second
```

- first先被确定为1–2秒，offset将起止同时钳为0；最后整理误当无终点，再延长到second的7秒，使已结束句重新显示
- 最小修复：保留明确终点与待推断终点的区别；明确终点偏移后已过期的句子不能再推断到未来
- 回归：终点偏移后小于0、等于0，以及起点小于0但终点仍为正数
- 状态：当前补丁修复不完整，不能称为补丁新引入的历史表现；未运行，截至检查点未复核修复

### R1-CI-07：非ASCII大小写路径绕过Space去重

- 严重度：P2
- 位置：Infrastructure/Data/SqliteItemRepository.cs:1264；Storage/LocalFileReferenceService.cs:259–267
- 规范路径不折叠Unicode大小写，而查询使用SQLite NOCASE，该规则仅折叠ASCII。在普通Windows大小写不敏感目录中，É/é路径可指向同一文件却生成两条Space记录
- 已执行内存SQLite对照：C:\Users\Éva\file.txt 与 c:\users\éva\FILE.txt 不相等，ASCII名字对照相等
- 最小修复：使用明确Windows路径比较语义的稳定比较键或自定义collation，并考虑既有记录；大小写敏感目录的产品边界需明确
- 回归：同一文件的非ASCII大小写路径变体，检查复用同一item ID；不能只用Linux上确实不同的文件作为证明
- 状态：SQL机制已实测，Windows仓库回归未执行；截至检查点未复核修复

### R1-CI-08：失败的缓存清理可重新接纳未删旧条目

- 严重度：P2
- 原位置：Infrastructure/Preview/FilePreviewCache.cs:148–178
- 原实现逐文件TryDelete吞掉锁文件IOException，随后将_clearPending清false；旧缓存重新变成可读取
- 最小修复：Clear采用严格删除，仅全部成功才解除invalidated状态；Trim可继续best-effort
- 状态：主任务已修改为严格删除，本审核已复核源码与新增FailedClearKeepsSurvivingEntriesInvalidUntilClearCanFinish测试。静态上原路径已修复，Windows锁文件测试待执行
- 关联修复：UndoCoordinator两个缓存清理catch已由主任务补入InvalidDataException，本审核已复核；避免重解析拒绝越过best-effort缓存清理边界

## 测试自身问题与验证边界

1. InternetShareRevokeStoreTests.cs:30–32 将落盘字节Base64后查含空格/连字符的原文token，Base64字母表不含这些字符，即便明文落盘断言也通过。建议直接查原始字节中的UTF-8 token并验证DPAPI解封装。主任务已将断言改为原始文件UTF-8文本查找，既有LoadAllAsync解封装断言保留；本审核已复核差异，未运行Windows用例
2. LyricsProviderStrategyNativeSmokeTests.cs:55–65查询随机不存在曲目，仅断言非Found和包装器调用次数；五个端点全部失败也会通过。它仅证明策略分发，不能作为歌词源可用性验收
3. AiLyricsNativeRuntimeSmokeTests仅两句，未覆盖12行分批、180秒整曲预算、慢CPU与长歌。文档中的十行批耗时不能推出所有整曲已通过；本审核不将超时风险定为必现缺陷
4. OverlayMotionCatchUpTests.cs:9–17声称消费完整100ms但只断言尺寸增大、安全，旧33ms积分也可能通过。建议与12次1/120秒积分或明确位移比较
5. 同文件:34–43的NonFiniteElapsed实际使用有限TimeSpan最大ticks，走长间隔直接收敛路径，建议改名并补零/负间隔
6. OverlayMotionProfileTests.cs:45–62“不向外膨胀”仅断言scale<=1.03；若产品要求绝不超过原尺寸，应将断言收紧为<=1
7. 新PairingRoundTripNativeTests的SQLite池清理、双端存储与密钥一致性建议已由主任务补齐，本审核已复核。仍须Windows运行，不是双机实测

## 排除与纠正

- 撤销字号迁移的生产回归定性。已直接读取本地tag v0.3.0-beta.32，发布版没有OriginalFontSize/TranslationFontSize；双字号仅来自未发布0.3.1候选，用户明确要求改单字号。保留开发候选设置只是可选兼容建议
- SettingsChangePolicy嵌套整体合并疑点：当前NativeSettingsEditor为单例且取得_save后再执行change(Settings)，没有现有UI路径的覆盖证据，不列缺陷
- UpdateReleaseSelector未按发布方prerelease标记筛选的疑点，由UpdateManifestParser严格验证版本/标记一致性，不列错误频道接受缺陷
- Linux路径大小写不能直接套作Windows路径逃逸漏洞
- 本次未决删除恢复事务同时恢复未过期项并终结过期项，区分普通expiry sweep，静态审核未发现新增事务问题；相关测试已读，执行归主任务
- 未发现可仅据当前AI实现确定的新增P1；已核查显式模型下载同意、固定哈希、重定向约束、输出ID验证、失败保留原歌词与generation/cancellation发布保护。这不代表原生推理验收完成

## 精确完整覆盖清单

下列清单为完整语义阅读的C#及项目文件；依赖锁文件的覆盖方式为元数据审阅。审核期间新增配对回归单列在实际目录清单中。

### src/DropSpace.Core（92文件）

- `src/DropSpace.Core/Abstractions/IAppStringLocalizer.cs`
- `src/DropSpace.Core/Abstractions/IFileReferenceService.cs`
- `src/DropSpace.Core/Abstractions/IItemRepository.cs`
- `src/DropSpace.Core/Abstractions/ILocalStorageMetrics.cs`
- `src/DropSpace.Core/Abstractions/IPayloadCleanupCoordinator.cs`
- `src/DropSpace.Core/Abstractions/IPayloadCleanupRepository.cs`
- `src/DropSpace.Core/Abstractions/IPayloadStore.cs`
- `src/DropSpace.Core/Abstractions/ISettingsService.cs`
- `src/DropSpace.Core/Abstractions/IStartupRegistrationService.cs`
- `src/DropSpace.Core/Abstractions/IUpdateServices.cs`
- `src/DropSpace.Core/Actions/ActionModels.cs`
- `src/DropSpace.Core/Actions/ImageSizePresetPolicy.cs`
- `src/DropSpace.Core/Actions/ItemSelectionResolver.cs`
- `src/DropSpace.Core/Actions/QuickActionPreferencePolicy.cs`
- `src/DropSpace.Core/Collections/ProjectionCollection.cs`
- `src/DropSpace.Core/Collections/SerializedProjectionRefreshCoordinator.cs`
- `src/DropSpace.Core/Compatibility/WindowsCompatibility.cs`
- `src/DropSpace.Core/Content/ItemContentModels.cs`
- `src/DropSpace.Core/Content/ItemContentPolicy.cs`
- `src/DropSpace.Core/Diagnostics/OperationCorrelation.cs`
- `src/DropSpace.Core/Displays/DisplayIdentity.cs`
- `src/DropSpace.Core/DragDrop/DragEvidence.cs`
- `src/DropSpace.Core/DragDrop/DragSessionPolicy.cs`
- `src/DropSpace.Core/DragDrop/DragSignalQueue.cs`
- `src/DropSpace.Core/DragDrop/OleFileDataKind.cs`
- `src/DropSpace.Core/DropSpace.Core.csproj`
- `src/DropSpace.Core/Island/IslandExperienceCoordinator.cs`
- `src/DropSpace.Core/Island/IslandGeometry.cs`
- `src/DropSpace.Core/Island/IslandPageTransition.cs`
- `src/DropSpace.Core/Island/IslandPresencePolicy.cs`
- `src/DropSpace.Core/Lyrics/AiLyricsModelCatalog.cs`
- `src/DropSpace.Core/Lyrics/LyricsDisplayPolicy.cs`
- `src/DropSpace.Core/Lyrics/LyricsGlowEnvelope.cs`
- `src/DropSpace.Core/Lyrics/LyricsGlowPolicy.cs`
- `src/DropSpace.Core/Lyrics/LyricsMatcher.cs`
- `src/DropSpace.Core/Lyrics/LyricsModels.cs`
- `src/DropSpace.Core/Lyrics/LyricsParser.cs`
- `src/DropSpace.Core/Lyrics/LyricsReloadPolicy.cs`
- `src/DropSpace.Core/Lyrics/LyricsTranslationOutput.cs`
- `src/DropSpace.Core/Lyrics/LyricsTranslationPolicy.cs`
- `src/DropSpace.Core/Lyrics/LyricsTranslationPrompt.cs`
- `src/DropSpace.Core/Media/MediaModels.cs`
- `src/DropSpace.Core/Media/MediaPlaybackClock.cs`
- `src/DropSpace.Core/Media/MediaProcessIdentityPolicy.cs`
- `src/DropSpace.Core/Media/NeteaseEnhancementModels.cs`
- `src/DropSpace.Core/Media/SpectrumAnalyzer.cs`
- `src/DropSpace.Core/Models/AppSettings.cs`
- `src/DropSpace.Core/Models/Capabilities.cs`
- `src/DropSpace.Core/Models/DomainModels.cs`
- `src/DropSpace.Core/Models/NativeIslandSettings.cs`
- `src/DropSpace.Core/Models/NativeIslandSettingsPolicy.cs`
- `src/DropSpace.Core/Models/OverlayPlacementEditSession.cs`
- `src/DropSpace.Core/Models/SettingsChangePolicy.cs`
- `src/DropSpace.Core/Models/SettingsMigration14.cs`
- `src/DropSpace.Core/Models/SettingsValidationPolicy.cs`
- `src/DropSpace.Core/Overlay/FullscreenWindowClassifier.cs`
- `src/DropSpace.Core/Overlay/OverlayContentPose.cs`
- `src/DropSpace.Core/Overlay/OverlayMotionController.cs`
- `src/DropSpace.Core/Overlay/OverlayMotionProfiles.cs`
- `src/DropSpace.Core/Overlay/OverlayPlacementPolicy.cs`
- `src/DropSpace.Core/Overlay/OverlayRegionSignature.cs`
- `src/DropSpace.Core/Overlay/OverlayStateMachine.cs`
- `src/DropSpace.Core/Policies/AppLanguagePolicy.cs`
- `src/DropSpace.Core/Policies/ConsecutiveClipboardCaptureCoordinator.cs`
- `src/DropSpace.Core/Policies/ContentClassifier.cs`
- `src/DropSpace.Core/Policies/FingerprintService.cs`
- `src/DropSpace.Core/Policies/LogRedactor.cs`
- `src/DropSpace.Core/Policies/PayloadPathPolicy.cs`
- `src/DropSpace.Core/Policies/RetentionPolicy.cs`
- `src/DropSpace.Core/Policies/SearchNormalizer.cs`
- `src/DropSpace.Core/Preview/PreviewModels.cs`
- `src/DropSpace.Core/Shell/ShellIntakeModels.cs`
- `src/DropSpace.Core/SystemActivities/SystemActivityModels.cs`
- `src/DropSpace.Core/Transfer/ClipboardImageBudgetPolicy.cs`
- `src/DropSpace.Core/Transfer/ClipboardLoopGuard.cs`
- `src/DropSpace.Core/Transfer/ClipboardPausedException.cs`
- `src/DropSpace.Core/Transfer/ClipboardPropagationQueue.cs`
- `src/DropSpace.Core/Transfer/HandoffMessagePolicy.cs`
- `src/DropSpace.Core/Transfer/TransferModels.cs`
- `src/DropSpace.Core/Transfer/TransferPolicies.cs`
- `src/DropSpace.Core/Undo/UndoModels.cs`
- `src/DropSpace.Core/Updates/DeploymentModeResolver.cs`
- `src/DropSpace.Core/Updates/ReleaseVersion.cs`
- `src/DropSpace.Core/Updates/UpdateChannelJsonConverter.cs`
- `src/DropSpace.Core/Updates/UpdateInstallerArguments.cs`
- `src/DropSpace.Core/Updates/UpdateModels.cs`
- `src/DropSpace.Core/Updates/UpdateReleaseSelector.cs`
- `src/DropSpace.Core/Widgets/WidgetCatalog.cs`
- `src/DropSpace.Core/Widgets/WidgetCountdown.cs`
- `src/DropSpace.Core/Widgets/WidgetDataSnapshot.cs`
- `src/DropSpace.Core/Widgets/WidgetModels.cs`
- `src/DropSpace.Core/packages.lock.json`

### src/DropSpace.Infrastructure（81文件）

- `src/DropSpace.Infrastructure/Actions/ActionOutputPolicy.cs`
- `src/DropSpace.Infrastructure/Actions/HashActionService.cs`
- `src/DropSpace.Infrastructure/Actions/ItemActionRegistry.cs`
- `src/DropSpace.Infrastructure/Actions/QrCodeActionService.cs`
- `src/DropSpace.Infrastructure/Actions/ZipActionService.cs`
- `src/DropSpace.Infrastructure/Content/ItemContentResolver.cs`
- `src/DropSpace.Infrastructure/Data/DatabaseExceptions.cs`
- `src/DropSpace.Infrastructure/Data/SqliteDatabase.cs`
- `src/DropSpace.Infrastructure/Data/SqliteItemRepository.cs`
- `src/DropSpace.Infrastructure/DropSpace.Infrastructure.csproj`
- `src/DropSpace.Infrastructure/Logging/RedactingFileLoggerProvider.cs`
- `src/DropSpace.Infrastructure/Lyrics/AiLyricsCache.cs`
- `src/DropSpace.Infrastructure/Lyrics/AiLyricsRuntimePackage.cs`
- `src/DropSpace.Infrastructure/Lyrics/AiModelPackageService.cs`
- `src/DropSpace.Infrastructure/Lyrics/AmllLyricsProvider.cs`
- `src/DropSpace.Infrastructure/Lyrics/KugouLyricsProvider.cs`
- `src/DropSpace.Infrastructure/Lyrics/LlamaCompletionRunner.cs`
- `src/DropSpace.Infrastructure/Lyrics/LocalInferenceProcess.cs`
- `src/DropSpace.Infrastructure/Lyrics/LocalLrcLyricsProvider.cs`
- `src/DropSpace.Infrastructure/Lyrics/LrclibLyricsProvider.cs`
- `src/DropSpace.Infrastructure/Lyrics/LyricsCache.cs`
- `src/DropSpace.Infrastructure/Lyrics/LyricsHttpClient.cs`
- `src/DropSpace.Infrastructure/Lyrics/LyricsProviderRegistry.cs`
- `src/DropSpace.Infrastructure/Lyrics/LyricsService.cs`
- `src/DropSpace.Infrastructure/Lyrics/LyricsTranslationCoordinator.cs`
- `src/DropSpace.Infrastructure/Lyrics/NetEaseLyricsProvider.cs`
- `src/DropSpace.Infrastructure/Lyrics/QqMusicLyricsProvider.cs`
- `src/DropSpace.Infrastructure/Lyrics/WindowsInferenceProcess.cs`
- `src/DropSpace.Infrastructure/Network/DeviceIdentityStore.cs`
- `src/DropSpace.Infrastructure/Network/DeviceSecretStore.cs`
- `src/DropSpace.Infrastructure/Network/DropLinkAuthenticationMiddleware.cs`
- `src/DropSpace.Infrastructure/Network/DropLinkChunkLedger.cs`
- `src/DropSpace.Infrastructure/Network/DropLinkClient.cs`
- `src/DropSpace.Infrastructure/Network/DropLinkDtos.cs`
- `src/DropSpace.Infrastructure/Network/DropLinkHost.cs`
- `src/DropSpace.Infrastructure/Network/DropLinkNonceCache.cs`
- `src/DropSpace.Infrastructure/Network/DropLinkPairingService.cs`
- `src/DropSpace.Infrastructure/Network/DropLinkProtocolContract.cs`
- `src/DropSpace.Infrastructure/Network/DropLinkReplayCache.cs`
- `src/DropSpace.Infrastructure/Network/DropLinkSingleFlight.cs`
- `src/DropSpace.Infrastructure/Network/FirewallCapabilityService.cs`
- `src/DropSpace.Infrastructure/Network/LocalNetworkInterfaceResolver.cs`
- `src/DropSpace.Infrastructure/Network/ReparseSafeDirectoryEnumerator.cs`
- `src/DropSpace.Infrastructure/Network/TransferRepository.cs`
- `src/DropSpace.Infrastructure/Network/WindowsDnsSdDiscoveryService.cs`
- `src/DropSpace.Infrastructure/Preview/FilePreviewCache.cs`
- `src/DropSpace.Infrastructure/Preview/FilePreviewProviderBase.cs`
- `src/DropSpace.Infrastructure/Preview/ImagePreviewProvider.cs`
- `src/DropSpace.Infrastructure/Preview/MediaPreviewProvider.cs`
- `src/DropSpace.Infrastructure/Preview/PdfPreviewProvider.cs`
- `src/DropSpace.Infrastructure/Preview/PreviewProviderRegistry.cs`
- `src/DropSpace.Infrastructure/Preview/TextPreviewProvider.cs`
- `src/DropSpace.Infrastructure/Preview/UnknownPreviewProvider.cs`
- `src/DropSpace.Infrastructure/Preview/UrlPreviewProvider.cs`
- `src/DropSpace.Infrastructure/Properties/AssemblyInfo.cs`
- `src/DropSpace.Infrastructure/Settings/JsonSettingsService.cs`
- `src/DropSpace.Infrastructure/Settings/SettingsIoPolicy.cs`
- `src/DropSpace.Infrastructure/Sharing/InternetShareRevokeStore.cs`
- `src/DropSpace.Infrastructure/Sharing/NearbyShareServer.cs`
- `src/DropSpace.Infrastructure/Sharing/ShareCryptoService.cs`
- `src/DropSpace.Infrastructure/Sharing/ShareUploadCoordinator.cs`
- `src/DropSpace.Infrastructure/Storage/AppStoragePaths.cs`
- `src/DropSpace.Infrastructure/Storage/FilePayloadStore.cs`
- `src/DropSpace.Infrastructure/Storage/LocalFileReferenceService.cs`
- `src/DropSpace.Infrastructure/Storage/LocalStorageMetrics.cs`
- `src/DropSpace.Infrastructure/Storage/OwnedPayloadReconciler.cs`
- `src/DropSpace.Infrastructure/Storage/PayloadCleanupCoordinator.cs`
- `src/DropSpace.Infrastructure/Storage/ReparseSafeFileOpen.cs`
- `src/DropSpace.Infrastructure/Storage/ReparseSafePathPolicy.cs`
- `src/DropSpace.Infrastructure/Storage/StagedFileImportService.cs`
- `src/DropSpace.Infrastructure/Storage/StagingLeaseStore.cs`
- `src/DropSpace.Infrastructure/Updates/GitHubReleaseUpdateSource.cs`
- `src/DropSpace.Infrastructure/Updates/HttpUpdateDownloader.cs`
- `src/DropSpace.Infrastructure/Updates/OfficialWebsiteReleaseUpdateSource.cs`
- `src/DropSpace.Infrastructure/Updates/ResilientUpdateSource.cs`
- `src/DropSpace.Infrastructure/Updates/UpdateFileVerifier.cs`
- `src/DropSpace.Infrastructure/Updates/UpdateManifestParser.cs`
- `src/DropSpace.Infrastructure/Updates/UpdateMetadataReader.cs`
- `src/DropSpace.Infrastructure/Updates/UpdateService.cs`
- `src/DropSpace.Infrastructure/Updates/UpdateStateStore.cs`
- `src/DropSpace.Infrastructure/packages.lock.json`

### tests/DropSpace.Core.Tests（59文件）

- `tests/DropSpace.Core.Tests/AiLyricsSettingsTests.cs`
- `tests/DropSpace.Core.Tests/Beta31UpgradeRegressionTests.cs`
- `tests/DropSpace.Core.Tests/BetaMigrationTests.cs`
- `tests/DropSpace.Core.Tests/ClipboardCaptureCoordinatorShutdownTests.cs`
- `tests/DropSpace.Core.Tests/ClipboardImageBudgetPolicyTests.cs`
- `tests/DropSpace.Core.Tests/ClipboardPropagationQueueTests.cs`
- `tests/DropSpace.Core.Tests/ConsecutiveClipboardCaptureCoordinatorTests.cs`
- `tests/DropSpace.Core.Tests/ContentAndDragPolicyTests.cs`
- `tests/DropSpace.Core.Tests/DisplayIdentityTests.cs`
- `tests/DropSpace.Core.Tests/DragSessionPolicyTests.cs`
- `tests/DropSpace.Core.Tests/DragSignalQueueTests.cs`
- `tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj`
- `tests/DropSpace.Core.Tests/FullscreenWindowClassifierTests.cs`
- `tests/DropSpace.Core.Tests/HandoffMessagePolicyTests.cs`
- `tests/DropSpace.Core.Tests/IslandPageTransitionTests.cs`
- `tests/DropSpace.Core.Tests/IslandPresencePolicyTests.cs`
- `tests/DropSpace.Core.Tests/LogRedactorAuthorizationRegressionTests.cs`
- `tests/DropSpace.Core.Tests/LogUserInfoRegressionTests.cs`
- `tests/DropSpace.Core.Tests/LyricsArtistMatchingTests.cs`
- `tests/DropSpace.Core.Tests/LyricsDisplayPolicyTests.cs`
- `tests/DropSpace.Core.Tests/LyricsGlowEnvelopeTests.cs`
- `tests/DropSpace.Core.Tests/LyricsGlowPolicyTests.cs`
- `tests/DropSpace.Core.Tests/LyricsParserTests.cs`
- `tests/DropSpace.Core.Tests/LyricsRelativeEndRegressionTests.cs`
- `tests/DropSpace.Core.Tests/LyricsReloadPolicyTests.cs`
- `tests/DropSpace.Core.Tests/LyricsRepeatedLineRegressionTests.cs`
- `tests/DropSpace.Core.Tests/LyricsTranslationOutputTests.cs`
- `tests/DropSpace.Core.Tests/LyricsTranslationPolicyTests.cs`
- `tests/DropSpace.Core.Tests/LyricsTranslationPromptTests.cs`
- `tests/DropSpace.Core.Tests/MalformedTransferManifestTests.cs`
- `tests/DropSpace.Core.Tests/MediaPlaybackClockTests.cs`
- `tests/DropSpace.Core.Tests/MediaProcessIdentityPolicyTests.cs`
- `tests/DropSpace.Core.Tests/NeteaseEnhancementPolicyTests.cs`
- `tests/DropSpace.Core.Tests/OverlayContentPoseTests.cs`
- `tests/DropSpace.Core.Tests/OverlayMaterialCapabilityTests.cs`
- `tests/DropSpace.Core.Tests/OverlayMotionCatchUpTests.cs`
- `tests/DropSpace.Core.Tests/OverlayMotionControllerTests.cs`
- `tests/DropSpace.Core.Tests/OverlayMotionProfileTests.cs`
- `tests/DropSpace.Core.Tests/OverlayPlacementEditSessionTests.cs`
- `tests/DropSpace.Core.Tests/OverlayPlacementPolicyTests.cs`
- `tests/DropSpace.Core.Tests/OverlayRegionSignatureTests.cs`
- `tests/DropSpace.Core.Tests/OverlayStateDedupeTests.cs`
- `tests/DropSpace.Core.Tests/OverlayStateMachineTests.cs`
- `tests/DropSpace.Core.Tests/OverlayTransitionTests.cs`
- `tests/DropSpace.Core.Tests/PolicyTests.cs`
- `tests/DropSpace.Core.Tests/QuickActionPreferencePolicyTests.cs`
- `tests/DropSpace.Core.Tests/RepeatedCaptureRetryAuditTests.cs`
- `tests/DropSpace.Core.Tests/SerializedProjectionRefreshCoordinatorTests.cs`
- `tests/DropSpace.Core.Tests/SettingsAndImageActionPolicyTests.cs`
- `tests/DropSpace.Core.Tests/SettingsUpdateCheckMergeRegressionTests.cs`
- `tests/DropSpace.Core.Tests/SettingsValidationPolicyTests.cs`
- `tests/DropSpace.Core.Tests/ShellIntakeTests.cs`
- `tests/DropSpace.Core.Tests/TransferPolicyTests.cs`
- `tests/DropSpace.Core.Tests/UpdateVersionTests.cs`
- `tests/DropSpace.Core.Tests/WidgetCatalogTests.cs`
- `tests/DropSpace.Core.Tests/WidgetExpansionTests.cs`
- `tests/DropSpace.Core.Tests/WidgetMovementTests.cs`
- `tests/DropSpace.Core.Tests/WindowsCompatibilityPolicyTests.cs`
- `tests/DropSpace.Core.Tests/packages.lock.json`

### tests/DropSpace.Infrastructure.Tests（66文件）

- `tests/DropSpace.Infrastructure.Tests/ActionOutputPolicyTests.cs`
- `tests/DropSpace.Infrastructure.Tests/AiLyricsCacheTests.cs`
- `tests/DropSpace.Infrastructure.Tests/AiLyricsNativeRuntimeSmokeTests.cs`
- `tests/DropSpace.Infrastructure.Tests/AiLyricsRuntimePackageTests.cs`
- `tests/DropSpace.Infrastructure.Tests/AiModelPackageServiceTests.cs`
- `tests/DropSpace.Infrastructure.Tests/AppStoragePathsTests.cs`
- `tests/DropSpace.Infrastructure.Tests/AuditHardeningTests.cs`
- `tests/DropSpace.Infrastructure.Tests/ClipboardWireBudgetTests.cs`
- `tests/DropSpace.Infrastructure.Tests/DatabaseWriteBoundaryTests.cs`
- `tests/DropSpace.Infrastructure.Tests/DropLinkAuthenticationMiddlewareTests.cs`
- `tests/DropSpace.Infrastructure.Tests/DropLinkChunkLedgerTests.cs`
- `tests/DropSpace.Infrastructure.Tests/DropLinkClientCancellationNativeSmokeTests.cs`
- `tests/DropSpace.Infrastructure.Tests/DropLinkNonceCacheTests.cs`
- `tests/DropSpace.Infrastructure.Tests/DropLinkPairingAdmissionTests.cs`
- `tests/DropSpace.Infrastructure.Tests/DropLinkPairingProtocolTests.cs`
- `tests/DropSpace.Infrastructure.Tests/DropLinkReplayCacheTests.cs`
- `tests/DropSpace.Infrastructure.Tests/DropLinkSingleFlightTests.cs`
- `tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj`
- `tests/DropSpace.Infrastructure.Tests/HashActionServiceTests.cs`
- `tests/DropSpace.Infrastructure.Tests/HostCompositionRegressionTests.cs`
- `tests/DropSpace.Infrastructure.Tests/InternetShareRevokeStoreTests.cs`
- `tests/DropSpace.Infrastructure.Tests/ItemActionRegistryTests.cs`
- `tests/DropSpace.Infrastructure.Tests/ItemContentResolverTests.cs`
- `tests/DropSpace.Infrastructure.Tests/LlamaCompletionRunnerTests.cs`
- `tests/DropSpace.Infrastructure.Tests/LocalStorageMetricsTests.cs`
- `tests/DropSpace.Infrastructure.Tests/LoggingDiagnosticsTests.cs`
- `tests/DropSpace.Infrastructure.Tests/LyricsCacheIdentityRegressionTests.cs`
- `tests/DropSpace.Infrastructure.Tests/LyricsFallbackTests.cs`
- `tests/DropSpace.Infrastructure.Tests/LyricsProviderStrategyNativeSmokeTests.cs`
- `tests/DropSpace.Infrastructure.Tests/LyricsProviderTransportTests.cs`
- `tests/DropSpace.Infrastructure.Tests/LyricsTranslationCoordinatorTests.cs`
- `tests/DropSpace.Infrastructure.Tests/NearbyShareRangeTests.cs`
- `tests/DropSpace.Infrastructure.Tests/NetworkRoundTwoRegressionTests.cs`
- `tests/DropSpace.Infrastructure.Tests/OfficialWebsiteReleaseUpdateSourceTests.cs`
- `tests/DropSpace.Infrastructure.Tests/OwnedPayloadReconcilerTests.cs`
- `tests/DropSpace.Infrastructure.Tests/PairingRoundTripNativeTests.cs`
- `tests/DropSpace.Infrastructure.Tests/PairingShutdownRegressionTests.cs`
- `tests/DropSpace.Infrastructure.Tests/PayloadCleanupOutboxTests.cs`
- `tests/DropSpace.Infrastructure.Tests/PeerTrustLifecycleTests.cs`
- `tests/DropSpace.Infrastructure.Tests/Preview16NetworkPolicyTests.cs`
- `tests/DropSpace.Infrastructure.Tests/Preview24SettingsMigrationTests.cs`
- `tests/DropSpace.Infrastructure.Tests/PreviewCacheTests.cs`
- `tests/DropSpace.Infrastructure.Tests/PreviewEdgeCaseAuditTests.cs`
- `tests/DropSpace.Infrastructure.Tests/PreviewRegistryConcurrencyTests.cs`
- `tests/DropSpace.Infrastructure.Tests/QueryPagingTests.cs`
- `tests/DropSpace.Infrastructure.Tests/RemoteMetadataCacheTests.cs`
- `tests/DropSpace.Infrastructure.Tests/RemoteMetadataGateTests.cs`
- `tests/DropSpace.Infrastructure.Tests/ReparseSafeDirectoryEnumeratorTests.cs`
- `tests/DropSpace.Infrastructure.Tests/ReparseSafeFileOpenTests.cs`
- `tests/DropSpace.Infrastructure.Tests/RepositoryRelinkAuditTests.cs`
- `tests/DropSpace.Infrastructure.Tests/SettingsConcurrencyTests.cs`
- `tests/DropSpace.Infrastructure.Tests/ShareCapacityRegressionTests.cs`
- `tests/DropSpace.Infrastructure.Tests/ShareCleanupRegressionTests.cs`
- `tests/DropSpace.Infrastructure.Tests/ShareCryptoTests.cs`
- `tests/DropSpace.Infrastructure.Tests/StagedFileImportTests.cs`
- `tests/DropSpace.Infrastructure.Tests/StagingLeaseStoreTests.cs`
- `tests/DropSpace.Infrastructure.Tests/StorageAndRepositoryTests.cs`
- `tests/DropSpace.Infrastructure.Tests/UndoRepositoryTests.cs`
- `tests/DropSpace.Infrastructure.Tests/UpdateCoordinatorTests.cs`
- `tests/DropSpace.Infrastructure.Tests/UpdateDownloadTests.cs`
- `tests/DropSpace.Infrastructure.Tests/UpdateManifestTests.cs`
- `tests/DropSpace.Infrastructure.Tests/UpdateMetadataNullRegressionTests.cs`
- `tests/DropSpace.Infrastructure.Tests/WindowsDnsSdDiscoveryTests.cs`
- `tests/DropSpace.Infrastructure.Tests/WindowsInferenceProcessTests.cs`
- `tests/DropSpace.Infrastructure.Tests/ZipActionServiceTests.cs`
- `tests/DropSpace.Infrastructure.Tests/packages.lock.json`
