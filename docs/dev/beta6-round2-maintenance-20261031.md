# Beta6 第二轮架构维护与验证 — 2026-10-31

## 开发起点

本轮接续同一工作区中尚未提交、尚未发布的 Beta6 开发候选。
`git ls-remote` 再次确认远端 main 和 Beta5 标签仍为
`3ecea1207dc93df5383e4b061966da8d1843e3e4`，无 Beta6/Beta7 标签。
未把旧 HEAD 当成 Beta6，也未覆盖上一轮改动。起点的 21 个变更/新增文件已保存为
`artifacts/beta7-validation/beta6-working-baseline.tar.gz`、`.patch` 和 SHA256 清单。
最终校验确认其中 15 个源码、测试、probe 文件逐字节保留；版本与当前目标文档另行前进。

本轮与第一轮同属 `v1.1.0-beta.6`，升级基线为已发布 Beta5。第二轮曾被助手误标为 Beta7；
第三轮按用户要求纠正该命名，保留所有代码及实际执行证据。下文 `beta7-*` 文件名仅为当时
的原始本地证据路径，不代表另一个产品版本。本轮未提交、推送、打标签、生成 Windows 包或发布。

环境延续 Linux x64、.NET 10.0.401（`/workspace/.dotnet`）、Node 24.19.0 和 Python
3.12.14；沿用锁定依赖。遵循仓库维护技能和定向测试政策，没有运行完整套件、AI/模型、
大文件压力或原生诊断工作流。四个并行代理分别处理网络（含一个协议审阅子代理）、App
生命周期、输出/清理及 Worker、网站/独立复审；主代理处理预览、集成、版本及执行账本。

## 架构与实现结果

生产引用图与上轮一致且无环：Core 无项目/NuGet 引用，Infrastructure 仅引用 Core，
App 引用 Core/Infrastructure 和既有 MusicBackground，MusicBackground 无项目引用。
没有增加业务层或公共服务接口来搬移问题。本轮主要收紧三个边界：
可选缓存与源数据、申请资源前的输入准入、停止请求与最终资源退场。

| 问题/成本 | 本轮改动 |
| --- | --- |
| 预览缓存路径不安全时，缓存写入的 InvalidDataException 被误归为源 provider 失败，正常文本被替换成 Unknown | `PreviewProviderRegistry` 在可选缓存读取/写入边界隔离该异常；继续拒绝不安全 IO，但返回有效源预览 |
| JSON null 可绕过非 nullable 构造参数，损坏缓存的 Metadata 被 UI `.Count` 直接使用 | `FilePreviewCache` 验证所需元数据集合，关闭读取流后删除不合格缓存并从源重建，兼容 Windows 文件共享规则 |
| 导出目标名称被目录占用时，CreateNew 抛访问/IO 错误，未按同名文件规则尝试编号名称 | `ActionOutputPolicy` 仅在候选路径确实被文件/目录占用时重试；原目录及内容保持不变，其他权限异常继续上抛 |
| orphan 清理先收集整棵树全部文件路径，额外保留大列表才开始处理 | `OwnedPayloadReconciler` 保留每目录安全快照，逐文件 yield；所有权、宽限期、重解析点及取消检查不变 |
| Hotkey Dispose 销毁仍被已排队 Stop 调用等待/释放的 semaphore，出现退出异常或等待不结束 | `GlobalQuickPanelHotkeyService` 让纯托管 gate 随 owner 回收；新启动仍被关闭，原生线程停止和 join 逻辑不变；Music wrapper 同样受益 |
| 无效的 Nearby 分享期限在监听器启动之后才被拒绝 | `NearbyShareServer` 提前检查期限，拒绝请求不解析网络、不打开源、不分配监听器 |
| Handoff 接受最多未来一分钟时间戳，但重放缓存仅从接收时保留十分钟，存在消息仍有效而去重先过期的窗口 | `DropLinkReplayCache` 按完整可接受时间窗保留；Host 传入消息创建时间；原三参数 API、每 peer/全局容量限制和协议格式不变 |
| 网站拒绝缺少资源的新版数据，却已把内部 currentStable 改成该版本，切换语言后系统要求与旧下载链接不一致 | `script.js` 在全部必需资源通过后才发布 currentStable，保留原快照的一致性 |
| 分享接收器对每块重复派生相同文件密钥并解码相同 nonce 前缀 | 在原 try 范围内每文件初始化一次；逐块 nonce 索引、AAD、认证、整体 hash、abort/Blob 回退行为保持 |

维护改动没有改变界面布局、动画/材质、现有交互入口、默认设置、数据/schema/网络格式、
模型/CUDA/provider/AI 策略或语言资源。效率结论仅限消除树级路径列表和重复密钥初始化；
未测量整体 CPU、内存峰值、吞吐或启动时间。测试观测到双块文件仅一次文件 HKDF 派生，
不是性能基准。

独立复审检查了上表所有改动，尤其确认：缓存容错没有放宽路径边界，清理时没有跨 move
保留 OS 目录枚举器，输出碰撞不掩盖真正访问故障，Hotkey 不再销毁 gate 并不保留原生
快捷键资源，密钥初始化失败仍进入原有 abort/finally。未发现阻塞回归。
ARCHITECTURE.md 补充了上述可选缓存、停止 drain 和网络准入不变量。

## 实际功能验证账本

与 Beta6 的历史执行分开记录，不将旧通过结果算作本轮新增执行。

| 执行 | 实际结果 | 证据/边界 |
| --- | --- | --- |
| 新预览缺陷用例在修复前执行一次 | 2 次预期失败 | `beta7-preview-baseline.trx`；不安全缓存把 Text 变 Unknown；Metadata=null 被当成有效缓存返回 |
| 预览与网络定向过滤 | 12 通过，0 失败/跳过 | `beta7-preview-network.trx`；预览 6、Nearby 3 行、Replay 3 |
| 输出与孤立 payload 清理过滤 | 4 通过，0 失败/跳过 | `beta7-output-cleanup.trx`；文件/目录名称碰撞各一项，已拥有/孤立清理与宽限期各一项 |
| Hotkey 初始基线 probe | 1 场景预期失败，exit 134 | Quick Panel 重复 Stop 遇到已销毁 gate |
| 固定单 fixture worker 顺序后的 Hotkey 基线 probe | 1 场景预期失败，exit 134 | 已经排队的 Stop 在 finally Release 复现 ObjectDisposedException；仅独立 probe 调整调度，不影响 App |
| 修复源码 Hotkey probe | 2 场景通过，各一次 | Quick Panel 和 Music；链接实际生产服务，无 user32/GUI/真实全局快捷键 |
| 网站运行时回归 | 1 通过，0 失败/跳过 | JSDOM 执行完整生产 runtime 及实际 requirements formatter，拒绝不完整发布后切语言，要求/标签/链接仍指向同一旧快照 |
| Worker 双块回归在优化前执行一次 | 1 次预期失败 | 完整解密成功，但文件密钥被派生两次，违反新的一次初始化断言 |
| Worker 优化后同一指定用例 | 1 通过，0 失败/跳过 | 真正 AES-GCM 的 5 MiB + 37 字节、两个块；核对所有下载路径、完整重建字节、一次 manifest + 一次 file 派生 |

本轮累计 **25 次用例/场景执行：20 通过、5 次预期基线失败**，没有未解决的非预期
失败。最终覆盖 16 个不同的 Infrastructure 用例、2 个 Hotkey 场景、2 个 Node 用例，
均通过。未匹配的套件用例未执行；没有把未执行/目标系统限制算作通过。
所有 .NET TRX 存于 `artifacts/beta7-validation/`；代理的 Node/probe 执行结果汇总于本表。
当前 Hotkey probe 编译产物对应修复源码。

Infrastructure 第一组过滤：

```text
FullyQualifiedName~PreviewCacheFaultTests
|FullyQualifiedName~PreviewRegistryConcurrencyTests
|FullyQualifiedName~FailedCacheReadAndWriteDoNotHideSuccessfulPreview
|FullyQualifiedName~ReparsePreviewRootCannotWriteOrDeleteExternalFiles
|FullyQualifiedName~NearbyShareAdmissionTests
|FullyQualifiedName~DropLinkReplayLifetimeTests
|FullyQualifiedName~DropLinkReplayCacheTests
```

第二组过滤：

```text
FullyQualifiedName~DirectoryCollisionUsesNextNameAndPreservesItsContents
|FullyQualifiedName~OutputNamesAreReservedWithoutOverwritingExistingFiles
|FullyQualifiedName~OwnedPayloadReconcilerTests
```

两组使用 `dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj
-c Release --no-restore --filter ...`，含实际 Core/Infrastructure/fixture/测试程序集编译。
新增 Hotkey probe 为 `dotnet run --project scripts/hotkey-stop-lifetime-probe -c Release`；
`HotkeySourcePath` 可选择保存的基线源码，仅用于独立重现。网站命令为
`node --test website/_source/scripts/runtime-release-retirement.test.mjs`。
Worker 仅按精确测试名称筛选 `share-worker/test/worker.test.mjs` 的真实加密下载用例，
没有调用 `npm test` 或运行完整 Worker/网站测试集合。

## 构建、完整性与状态检查

- 上轮 15 个源码/测试/probe 文件与保存的 Beta6 SHA256 清单逐项一致；旧发布记录与
  Beta6 执行账本没有覆盖。沿用它们的历史结果，不将其重新记为本轮测试。
- 生产项目引用边界和无 Core NuGet 依赖检查通过，未改变包版本或锁文件。
- `npm run build --prefix website/_source` 通过：十语言网站 272 ID、三份统一页面、
  英语默认、旧链接兼容。构建仍使用仓库历史 `v0.2.1` release fixture，不是公共发布状态。
- App/installer/共享语言资源本轮均未改变；上一轮 App 1,059 ID/10 语言完整性结果可复用。
  此项没有替代 Windows PRI 或翻译语义验收。
- `node scripts/test-release-metadata.mjs`、`node scripts/passive-test-budget.mjs audit`
  和 `node --check share-worker/src/index.js` 通过；未触发工作流或扩大自动功能测试范围。
- 第二轮执行时因上述误标，MSBuild 曾读取 release `1.1.0-beta.7`、文件/包版本 `1.1.0.7`；
  此项历史结果已经第三轮的 Beta6 版本一致性检查取代，不代表当前开发目标。
- 最终 diff/new-file 空白与项目 XML 检查通过。没有新增依赖、发布签名或模型授权证明。

## 目标系统待验

| 目标 | 待验证内容 |
| --- | --- |
| Windows 构建/分发 | 完整 App/XAML、MusicBackground、PRI 和真实 Installer/Portable/MSIX；当前托管源码链接 probe 不证明 WinUI 可编译或渲染 |
| Windows 快捷键 | 真实注册/键位切换，设置事务与退出重叠、多次 Stop/Dispose 下原生消息线程正确结束 |
| 预览/磁盘 | 真实缓存损坏、权限/锁文件/重解析路径，预览正常显示，外部目录及原文件不变；POSIX 链接结果不是 Windows ACL 验收 |
| 导出/清理 | 真实 Hash/QR/ZIP 输出目录名称冲突，Windows 访问拒绝区分，进程崩溃后的 payload 清理和取消重试 |
| 局域网/两台设备 | 私网/防火墙/地址变化下分享启动关闭；配对设备有时钟偏差时 Handoff 一次接收、重复拒绝、超时后正确过期 |
| 浏览器/Worker | 部署后的真实浏览器语言切换/发布刷新，Blob 和 File System Access 下载、取消/失败流中止、空文件/多文件、移动端和大文件兼容；VM 只证明受测分块加密及状态逻辑 |
| 保持体验 | 主窗/灵动岛、拖放、剪贴板、音乐/歌词、DLC、Light/Dark/Liquid Glass、高对比度、DPI/多屏/无障碍与上一版本对比 |
| 升级与发布 | 从实际已发布 Beta5 升级且保留数据/偏好；exact-candidate Windows 包、哈希、签名、更新/API/真实下载门禁和模型授权重新核对；不能沿用 Beta5 历史授权宣布当前 Beta6 已获发布资格 |

已知原生堆稳定性调查仍未解决。macOS、长运行、真实跨设备和部署后服务未验证；上一轮
列明的 macOS helper 超时退出码候选问题仍未在本轮修改或获得 macOS 验收。本轮是有针对性
的第二轮维护，不声明全仓库无缺陷或所有原生体验已经验收。
