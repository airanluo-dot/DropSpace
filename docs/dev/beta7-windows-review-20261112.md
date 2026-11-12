# Windows Beta 7 审查处理记录

2026 年 11 月 12 日录音逐项确认了原清单中的 30 项。以下记录按原编号对照 Beta 7。录音中随后逐项作出的决定优先；09:31 的连读按清单顺序对应 106。跳过的项目已从本轮待办移除。

## 当前分支处理的 24 项

| 编号 | 位置 | 处理结果 |
| --- | --- | --- |
| 006 | OfficialModuleCatalog、FeatureModuleUiSmoke | 本地隔离预览的诊断入口使用专用示例目录，消费者目录继续过滤该模块。 |
| 008 | JsonSettingsService、KeyboardShortcutPolicy | 无效快捷键或类型错误只重置受影响的快捷键设置，界面恢复保留有效的非界面偏好。 |
| 018 | 网站 release-requirements.js 及调用方 | Windows 稳定版要求显式接收当前语言的翻译函数，静态生成和浏览器使用同一契约。 |
| 024 | ci.yml、Reuse-BundledLyricsLanguageModel.ps1 | 语言模型缓存未命中时从固定版本、校验哈希的发布文件中提取模型。 |
| 030 | AiLyricsService、MediaExperienceService | 快速切歌时，旧请求不能取消新歌的翻译或清除新歌的结果。 |
| 037 | QqMusicSession | QQ 音乐显式重试只影响当前操作，成功和失败分别更新相应的会话状态。 |
| 056 | 两个 Windows 发布候选筛选脚本 | 扫描时跳过外部分叉的候选，对本仓库候选继续执行原有核验。 |
| 061 | MusicPage | 当前行因翻译、文字或字号变化而重新布局后，重新计算居中位置。 |
| 065 | App.xaml.cs、ShellIntakeActivationService | UI 线程统一交接启动期间的重定向请求，文件接收端通过 Windows App SDK 的启动参数接口读取路径。 |
| 070 | WidgetEditorView、WidgetModels | 从组件库拖放时只接受预览的准确位置；点击添加仍可自动选位。 |
| 073 | LyricsMatcher | 识别紧邻中文、年份和全半角字符的录音版本标记，避免混用现场版、混音版等歌词。 |
| 074 | docs/protocol/droplink-v1.md | 根据 Windows 发送端和接收端的实现，纠正 URL 片段与哈希说明，补充可复算的示例。 |
| 082 | OverlayWindow.xaml、DesignTokens.xaml | 高对比度主题显示系统色描边。 |
| 086 | MainPage、QuickActionDialogService、WindowsImageTransformService | 多选图片时，在进入单图参数对话框之前执行同一套可用性判断。 |
| 089 | ClipboardCaptureService | 缩略图清理失败时，本轮仍回收已过期的实际文件。 |
| 090 | App.xaml.cs | 局域网发现及跨设备剪贴板启动失败时，工作台继续初始化。 |
| 094 | ZIP、哈希及图片操作的可用性判断 | 选择记录时只读已有元数据，实际操作时再访问磁盘和处理缺失文件。 |
| 096 | DropSpace.iss、MaintenanceShutdownService | 安装器按目标路径识别旧进程，等待其真正退出后才继续替换文件。 |
| 098 | SqliteItemRepository | 在撤销期限内重加同一引用时复用原记录，同时保留同一删除批次中其他项目的撤销能力。 |
| 100 | ItemContentPolicy | PDF 或媒体预览不可用时保留已有文件说明。 |
| 106 | DropSpace.App.csproj | XAML、语言资源、图片的修改及增删改名会使 PRI 资源索引失效并重建。 |
| 109 | ARCHITECTURE.md、PRIVACY.md | 按 Windows 更新客户端的请求顺序说明官网版本索引、GitHub 后备来源与下载来源。 |
| 113 | OverlayWindowService | 每台显示器分别判断是否需要唤醒入口，另一块屏幕上的浮岛不会将它禁用。 |
| 115 | SettingsApplicationCoordinator、StartupRegistrationService | 仅在开机启动项与设置不同时更新注册表，访问失败时仍可正常打开工作台。 |

## 原入口已移除的 5 项

Beta 7 的开发规范提交 `1c2bbe91` 已删除以下入口，当前构建与发布流程均不调用它们。

| 编号 | 原审查指向的入口 |
| --- | --- |
| 029 | AI 发布审批与运行时发布的旧 Node 测试断言 |
| 039 | PersistentPlainLyricsRunnerTests 中旧 CUDA 测试样品的版本标签 |
| 046 | release.yml 中手动拼接两次运行身份的证据采集步骤 |
| 051 | test-ci-workflows.test.mjs 对旧构建、冒烟作业的断言 |
| 060 | plain-hy-production-evidence 的旧歌词筛选记录器 |

## 条件项 014

本轮未合入 PR #123。当前 Beta 7 的 required-validation 编译完整 Windows App 及 XAML；今后若推进该 PR，仍应在其实际待合并提交上编译 App 消费者。
