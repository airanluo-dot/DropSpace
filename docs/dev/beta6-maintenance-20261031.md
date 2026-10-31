# Beta6 架构维护与验证记录 — 2026-10-31

## 基线与交付状态

- 基线：`3ecea1207dc93df5383e4b061966da8d1843e3e4`，`v1.1.0-beta.5`。
  只读 `git ls-remote` 确认远端 main 和 Beta5 标签均指向该提交，没有 Beta6 标签。
  初始工作区干净。
- 当前开发目标：`v1.1.0-beta.6`，直接升级基线为 Beta5；尚未发布或生成 Windows 安装包。
- 环境：Linux x64、Node 24.19.0、Python 3.12.14、补装的 .NET SDK 10.0.401。
  SDK 位于 `/workspace/.dotnet`。环境没有 Windows 桌面、WinUI/XAML 原生工具链或 Swift。
- 使用仓库 `dropspace-maintainer` 指南。四个并行代理分别审查存储、App 生命周期、
  下载恢复及架构；主代理负责设置、集成、版本与验证。所有代理的实际执行合并记录于此。

## 架构结论与实施

最终项目引用检查通过：Core 无项目/NuGet 依赖；Infrastructure 仅引用 Core；App
引用 Core、Infrastructure 和既有 MusicBackground；MusicBackground 无项目引用。
四个生产项目没有引用环。MusicBackground 是 Windows 视觉实现组件，三层业务职责不变。
ARCHITECTURE.md 补齐该组件，并消除过期的 Beta28/Beta27 当前版本描述。

本轮采用已有所有权边界修复问题：设置存储拥有迁移，文件引用服务拥有可用性缓存，
下载持久化线程拥有最终删除，DLC 管理器拥有通知异常边界。没有新增服务容器、
业务层、默认后台任务或强制用户重新配置的迁移。

| 问题与影响 | 修复及代码位置 |
| --- | --- |
| 首次 Load 前执行 Update/安全模式 UI Reset，会把未显式保存通道的旧 Preview 用户变成 Stable；两套迁移顺序容易继续分叉 | `JsonSettingsService.MigrateSettings` 共用迁移链，保留显式通道、语言、隐私和原有迁移顺序 |
| 设置读取因 record/集合克隆而反复触发落盘 | 对原始 JSON 与验证后的持久值做结构比较；实际规范化写回一次，未改变的配置免写，不依赖集合引用相等 |
| 缺失文件夹的刷新结果被保存为 File，目录恢复后仍显示类型改变 | `RecentItemsService.RefreshAsync` 在元数据不可用时保留原引用类型和扩展名 |
| 远程路径缓存把 File 的可用状态复用于同路径 Folder；淘汰与写入分离可能突破上限 | `LocalFileReferenceService` 核对预期类型，以一个短内存锁串行淘汰和发布，保持 1,024 项上限；文件 IO 不在锁内 |
| 在途进度持久化失败后重新入队，可能让已删除下载记录复活 | `DownloadPersistenceWorker` 在实际成功删除的串行边界再次清除待重试进度 |
| 下载日志仅部分可读时仍标记恢复完成，暂不可读记录本进程内不再恢复 | `DownloadManager` 保留恢复重试机会，复用已恢复任务对象，成功完整读取后仍幂等 |
| 一个 DLC 界面订阅者抛错，会污染安装/删除结果、阻断取消或从 ThreadPool 进度计时器逃逸 | `DlcManagerService` 统一复用既有 `NativeSubscriberNotification`，隔离各订阅者并保留诊断 |
| 两项已有设置测试比较集合引用而非持久值，在 Beta5 原版也失败 | `StorageAndRepositoryTests` 改为核对完整序列化值，保留原有数据库/payload 安全断言，未以实际输出替换预期字段 |

源码热点包括 OverlayWindow（2,726 行）、MainPage（2,576）、ClipboardCaptureService
（1,960）、DragSessionDetector（1,933）、SqliteItemRepository（1,879）和 MainViewModel
（1,877）。这些类跨越多个原生/交互生命周期，后续应沿既有投影、事务、资源退场职责
逐步拆分；本轮没有为了行数大规模搬移界面或改变事件时序。

界面布局、动画/材质、交互入口、默认值、数据库和设置 schema、十语言资源、
歌词/provider/AI 策略与独立模型/CUDA 资源均未修改。效率改进有源码和免写入断言支持，
没有进行真实性能基准，因此不报告 CPU、内存或启动速度的量化收益。

## 功能验证执行账本

没有运行完整回归套件、模型推理、大文件压力测试、CI 或发布工作流。
TRX 和基线日志位于本地忽略目录 `artifacts/beta6-validation/`。

| 执行 | 实际结果 | 说明 |
| --- | --- | --- |
| Infrastructure 首轮定向过滤 | 17 通过，0 失败/跳过 | 设置入口 4、并发设置 2、首次隐私 3、下载删除 1、下载恢复 4、文件引用 3；`beta6-focused-infrastructure.trx` |
| DLC 生产源码链接 probe，`-- all` | 3 场景通过，各执行 1 次 | 事务结果、真实 200ms 进度 Timer、取消和退场；确定性 provider，无实际模型下载/GUI |
| DLC Beta5 原始源码 probe，`-- transaction` | 1 次预期失败，exit 134 | 原始 `RefreshCoreAsync` 的 Changed 通知抛出异常；未执行会导致线程池未处理异常的基线进度场景 |
| 设置规范化补充后的首次检查 | 3 通过、2 失败 | `beta6-settings-final.trx`；发现验证复制列表导致 record 不等，误触发落盘 |
| 改为 JSON 持久值比较后的设置检查 | 5 通过，0 失败/跳过 | `beta6-settings-corrected.trx`；涵盖更新/重置前迁移、免重复落盘、规范化写回及旧通道/隐私持久化 |
| 已有设置往返/损坏恢复/无效 UI 定向检查 | 1 通过、2 失败 | `beta6-settings-recovery.trx`；两项失败来自集合引用断言 |
| 独立 Beta5 原始工作树运行上述两项失败用例 | 2 失败，与候选同因 | `beta5-existing-settings-baseline.trx` 及同名 `.txt`；确认原有测试问题，未修改基线 |
| 修正持久值断言后重跑这两项 | 2 通过，0 失败/跳过 | `beta6-settings-roundtrip-corrected.trx`；序列化往返和损坏恢复均成立 |

累计执行 38 次功能用例/场景：31 次通过，4 次开发期失败已修正，3 次基线失败用于
复现；没有跳过结果冒充通过。最终覆盖 **21 个不同的 Infrastructure 用例和 3 个
DLC 场景**，最新相关结果均通过。设置最终改动后只重跑受影响用例，未重复下载/
存储已通过的检查。

首轮过滤包含 `SettingsMigrationEntryPointTests`、`SettingsConcurrencyTests`、
`FirstRunPrivacySettingsTests`、`DownloadPersistenceTests`、`DownloadManagerRecoveryTests`
以及 RecentItems 的以下三项：

```text
FolderRefresh_PreservesMissingReferenceKindAndRecoversWhenRestored
FileReplacement_WithDirectoryIsUnavailableWithoutChangingTheOriginalReferenceKind
Refresh_CannotBypassStabilityAndDoesNotRepeatMissingNotifications
```

后续设置恢复检查为 `Settings_RoundTripTypedValuesAtomically`、
`Settings_InvalidDragWakeModeIsQuarantinedToSafeSmartDefault`、
`Settings_MalformedJsonFallsBackWithoutDeletingDatabaseOrPayloads`。
新 probe 使用命令 `dotnet run --project scripts/dlc-notification-probe -c Release -- all`。

## 构建与完整性检查（不计入功能用例）

- 首次 SDK 安装到 `/opt` 因权限失败，改用 `/workspace/.dotnet` 后成功；没有修改项目 SDK 版本。
- Infrastructure 测试项目 `restore --locked-mode -p:Configuration=Release` 成功，依赖锁文件未改。
  Release 测试编译包含 Core、Infrastructure、ResidentWorkerFixture 及 Infrastructure.Tests。
- probe 首次编译/执行成功。代理最后一次用基线源码覆盖了本地 probe 编译产物，主代理随后
  用当前源码重新 `dotnet build ... -c Release --no-restore` 成功，0 警告/错误，没有重复功能场景。
- `npm ci --prefix website/_source --ignore-scripts` 成功；资源检查通过：10 个界面语言、
  App 1,059 ID、网站 272 ID。资源语义质量和实际 Windows PRI 未因此获得验证。
- `npm run build --prefix website/_source` 成功：三份统一页面、英语默认和旧链接兼容性。
  输入是仓库自带 `data/releases.json` 的历史 `v0.2.1` fixture，未联网同步或改写公共更新数据。
- `node scripts/test-release-metadata.mjs`、`node scripts/passive-test-budget.mjs audit`
  通过；后者只检查 12 个 workflow 的触发/范围约束，未运行其功能测试。
- 新候选路由接受 `v1.1.0-beta.6`；MSBuild 读取到 release `1.1.0-beta.6`、文件/包版本
  `1.1.0.6`。依赖边界检查最初错误地遗漏既有 MusicBackground 引用；修正检查预期后通过，
  无生产代码依赖错误。
- 最终 `git diff --check` 通过；无需修改翻译确认、模型证据或历史发布记录来通过检查。

## 目标系统验证与剩余边界

| 目标验证 | 待执行内容 |
| --- | --- |
| Windows 工具链 | 对最终候选完整编译 App/XAML 与 MusicBackground；生成/检查 PRI、Portable、Installer、MSIX；Linux probe 只覆盖所链接的托管源码 |
| Beta5 → Beta6 升级 | 真实用户配置/数据库/payload 保留，Stable/Beta 通道、独立模型/CUDA 复用，安全模式与设置重启回读 |
| 文件与网络盘 | Windows 上运行 `RemoteMetadataCacheTests`（含新类型场景和上限场景）；真实 UNC 断连/恢复、文件夹移走后恢复、文件/文件夹替换及源文件不变 |
| 下载生命周期 | 真实下载中的取消/删除、持久化失败、部分日志恢复、关闭再启动和断网重连；验证历史不复活、记录不丢失、文件恢复一致 |
| DLC | 真实包安装/删除/取消、进度更新、导航卸载/重载、退出时资源释放；probe 不证明 WinUI 线程调度或 provider/native 清理 |
| 体验回归 | 主窗口/灵动岛、剪贴板、拖放、音乐/歌词、设置；Light/Dark/Liquid Glass、高对比度、DPI/多显示器、快捷键和无障碍 |
| 发布资格 | exact-candidate Windows 构建/包哈希、签名/安装与升级门禁及实际下载/更新/API 回读；`test-ai-release-approval.mjs` 的现有模型授权仍绑定 Beta5，不能自动沿用或改写成 Beta6 已获批准 |

已知原生堆稳定性调查仍未解决，不能以托管检查通过宣称其修复。macOS、真实两台设备
DropLink、部署后的分享 Worker/browser 和长时间运行未验证。架构只读审查另发现 macOS
`ProcessRunner` 超时后零退出码的处理值得复现，Worker 接收循环存在重复密钥派生的优化
空间；这两处没有在缺少相应目标验证的本轮中修改。当前工作不构成全项目无 bug 或原生
功能已全部验收的声明。
