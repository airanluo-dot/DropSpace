# beta18 Round 1 — App/UI 全源审查与修复

## 完整覆盖与授权

审查唯一基准为不可变集成快照 `/workspace/scratch/beta18-round1`，严格覆盖 `scope/app-ui-scope.txt` 的 **58 / 58 文件、17,772 / 17,772 个物理文本行**。按 `wc -l` 的换行符计数为 **17,771**；`packages.lock.json` 最后一个 `}` 没有终止换行，是计数差异的唯一来源，该末行也已读取。所有 App 根部、窗口、ViewModel、Views/C#、XAML、双语资源、工程与清单配置均读取完整正文。首次工具输出截断的片段已补读，没有把搜索、摘要或 diff 当作全源覆盖。

范围清单 SHA-256：`f684faa6d9cf911335c1cb447b36b1546b9d7eb18ce6665bcacbf793220c7b35`。逐文件完整哈希、两种行数与阅读区间记于 [round1-app-ui-coverage.tsv](round1-app-ui-coverage.tsv)，全部文件也列于文末。

已读取 `/workspace/DropSpace/AGENTS.md`、canonical `.agents/skills/dropspace-maintainer/SKILL.md` 及 UI 参考。最初只审查；root 在本轮全源读取完成后明确授权以下 scoped fixes。本分区随后修改 App/UI 文件和报告，未执行任何测试、构建、应用启动、原生检查、远端操作或 commit。完整 App 已由本轮并行分区覆盖，没有将任何模块延期到下轮。

以下发现的原始行号均指不可变快照，修复状态指共享工作仓库 `/workspace/DropSpace`。

## 发现、证据与已实施修复

### R1-UI-01 — P2：剪贴板 reload 覆盖 live capture，cap 分支可遗留 loading

**原始位置：** `src/DropSpace.App/ViewModels/MainViewModel.cs:776`、`:1534`、`:1567`；关联 `Services/ItemProjectionService.cs:22`。

`ReloadAsync` 等待后台 query 前记录 `_reloadRevision`，结果返回后检查 revision 再 Clear/追加整个快照。`ApplyCapturedItem` 在无搜索 Clipboard 页面直接插入或更新 live 行，在未超过 cap 时不改变 revision。query 已形成旧快照、capture dispatcher callback 先执行、query continuation 后执行时，新行先出现又被旧快照删除；已存在 capture 的更新/顺序也可回退。记录仍在 storage，当前视图遗漏它直到另一次 reload。

达到 cap 时 `TrimLiveClipboardProjection` 增加 revision，但不安排替代 reload。当前 reload 被丢弃，其 finally 仅在 revision 相等时清理 `IsBusy`，可造成 loading 持续。`NavigateAsync` 切换 Clipboard/Space 未先清旧 Items，故只对每次 capture bump revision 会丢失初始 history，甚至留下旧 section 的行。

**已修复：** 每个无搜索 Clipboard 首屏 reload 拥有独立 capture journal，按 ID 去重并按最新 capture 顺序保留，最多 250 行。查询等待期间的 capture 同时写入此 journal 和当前 UI；合法快照返回后 merge 最新 capture 覆盖与历史查询结果，再替换 Items。合并超过 live cap 时，在渲染/thumbnail 创建前截到 cap，并以最终保留尾部重建 paging cursor、保持 HasMore。首屏 reload 等待期间 cap trimming 不再废弃该 reload，所以历史会正常应用且其 finally 正常结束 loading；无 pending reload 时仍使旧分页 revision 失效。LoadMore 在 IsBusy 时不开始；导航、搜索变更与 dispose 清掉旧 journal；旧请求 finally 只清自己拥有的 journal。**没有增加每次 capture 的数据库 query。**

**静态复核：** 对低于 cap、超过 cap、重复 ID、旧 reload 被新导航/搜索替代的控制流逐项读取；合法快照 merge 后始终保留本 section 的历史而不是上一 section 的 collection；最终 tail cursor 不会跳过本次 cap 淘汰的历史。未运行行为测试。

**待验证：** fake query 的受控顺序“先形成旧快照→capture→返回”，覆盖 `<250` / `>=250`、重复 capture、Space→Clipboard 首屏、分页、取消/退出；验证 capture 和 history 都在、spinner 结束、下一页不漏行。

### R1-UI-02 — P2：隐藏主窗音乐控件继续渲染，延迟 seek 未随离开页面取消

**原始位置：** `Views/MainPage.xaml:380`、`Views/MainPage.xaml.cs:122`、`MainWindow.xaml.cs:138`、`Views/Music/MusicPage.cs:255`、`Views/Island/MediaExpandedView.xaml.cs:83`、`:99`、`:235`；路径均在 `src/DropSpace.App/`。

主窗构造时创建 MusicPage，由其外层 MusicContent 的 Visibility 控制导航。祖先 Collapsed 不改变子控件自身 Visibility，也不卸载 tree。MusicPage.CanRefresh 以及 `_nowPlaying` MediaExpandedView.CanRender 只看自身 IsLoaded/Visibility，没有 host active 信号；隐藏/最小化主窗也只更新 media service 的 visibility owner。

服务 `MediaExperienceService.RenderFrame:704–715` 在播放且岛内 presentation 或 glow 活跃时每 33 ms 更新 Position/Lyrics/Spectrum。隐藏主窗的 now-playing 仍订阅每次 PropertyChanged，继续请求全局 CompositionTarget.Rendering 并 Render。MusicPage 在歌词索引/会话等变化时也继续 Refresh。这是静态 fanout 确认，未测量 CPU。岛内 ExpandedIslandMusicView.SetActive 已有正确的显式 gate。

同一缺口影响 120 ms seek commit 和 acknowledgement timer：只在控件自身 Visibility 变化或 Unloaded 时取消，OnSeekCommitTimer 没有 host active 检查。改动进度后立即切页、关闭到托盘或最小化时，旧 seek 仍可完成。

**已修复：** MainWindow 把 `!closing && IsMusicVisible && AppWindow.IsVisible && !Minimized` 合成状态同时传给 media visibility owner 和 MainPage→MusicPage→MediaExpandedView.SetActive。初始化与语言页 rebuild 同步该状态；page retirement/unload 主动停用。两个 CanRender/CanRefresh 均包含显式 active gate。停用取消 Rendering hook、seek preview/commit/ack timers 以及 pending interaction；seek 输入、pointer 开始、完成、timer 和执行端也检查 active。重新激活请求最新 refresh，MediaRenderQueue 保留 hidden invalidation。不依赖祖先 Visibility 通知。

**静态复核：** 阅读 initial construction、section PropertyChanged、AppWindow.Changed、语言 rebuild、retire/shutdown、own Visibility、pending seek 各入口；inactive 不再 queue render/commit，reactivate 可重新请求最新状态。未原生验证。

**待验证：** 主窗 Music→Space/Settings、tray hide、minimize 且岛内播放继续时不执行隐藏主窗 render；seek pending 时执行这些转换不提交旧 seek；重新显示数据正常。

### R1-UI-03 — P3：DLC 卸载确认对 AI 偏好给出相反承诺

**原始位置：** `Views/Settings/DlcPage.cs:147–152`；两个 locale `Resources.resw:744`、`:784`；路径均在 `src/DropSpace.App/`。

卸载已启用的当前模型，通用 DlcDeleteConfirm 先表示关闭 AI 翻译，随后 DlcDeleteEnabledModel 又明确 AI preference 保留。两种语言均矛盾。完整读取关联 AiModelDlcProvider.DeleteAsync:34–40 确认它停止/drain worker、删除模型 availability，保持用户 AI consent。

**已修复：** 双语 DlcDeleteConfirm 改为停止使用组件的推理、释放资源，并保留 AI preference/cache/files；已启用模型追加说明仍指出需安装或选择可用模型后继续翻译。不修改 AI 功能或 consent。旧 AiLyricsDeleteConfirm 仍存在但此路径不使用，未扩大改动。

**静态复核：** 两个资源 key 的正文与 DlcPage 拼接结果已读；未运行 resource/UI test。

### R1-UI-04 — P2：durable payload delete queue 在 UI 上同步初始化

**原始位置：** `src/DropSpace.App/App.xaml.cs:175` resolve MainViewModel；`src/DropSpace.Infrastructure/Storage/FilePayloadStore.cs:19–24` constructor。

基础设施 storage 全源分区确认 payload-store constructor 同步 drain durable deferred-delete journal；每段有限，全部删除义务刻意无总 cap，可能在低速或恢复大量条目时耗时。App 主 UI 上 MainViewModel DI 首次间接构造该 singleton，发生于交互窗口创建之前。root 要求保留每一项删除义务并实施最小 worker prewarm。

**已修复：** App 通过兼容性/配置/语言准备后、MainViewModel resolve 前，捕获当前服务 provider 和 app lifetime token，在 awaited Task.Run 中预解析 IPayloadStore。只含 AppStoragePaths 的构造因此在 worker 执行，后续 DI 复用同一 singleton。未 ConfigureAwait(false)，正常 continuation 保持 UI context；调度前 cancellation 捕获后返回，完成后的 token 再检查可阻止继续构窗。没有 cap、丢弃义务或重写 constructor 的恢复合同。

**静态复核与限制：** 检查 provider 构造与 payload registration、取消 token、后续 UI resolution、已有 launch exception cleanup。worker 内同步 drain 自身不支持中途取消，必须完成；await 明确拥有它，尚未做耗时/退出原生验证。该缺陷来自本轮 storage 全读；此处只完整读取关联 constructor/startup 片段补证，storage 本体覆盖由该分区记录。

## 分区间确认

基础设施分区单独拥有 recent-items SQL UI 阻塞发现与修复。补证的 caller 是 MainVM.GetRecentSourceItemsAsync:1156–1166 直接 QueryAsync，OverlayVM ctor:45–47 将其交给 SerializedProjectionRefreshCoordinator loader；RequestAsync 同步 RunAsync，无 offload。启动 App:293→OverlayWindowService:136→OverlayVM.InitializeAsync:145，以及 UI 上 SpaceProjectionChanged→OverlayVM:443 会调用。基础设施 agent 已在该 MainVM 方法增加 Task.Run query boundary，并在 UI continuation 创建 card VM；本分区未覆盖其改动。

为确认上述问题，额外完整读取 ItemProjectionService、AiModelDlcProvider、SerializedProjectionRefreshCoordinator，并阅读 MediaExperience frame 更新与 FilePayloadStore constructor/关联 SQL 的必要正文。它们不计入本分区 58 文件清单，所属模块由本轮其他全源分区覆盖。

## 关键路径观察

| 路径 | 全读后的实际观察 |
|---|---|
| 启动/激活/首启/更新/退出 | App 初始化顺序、single instance/maintenance/activation、startup failure cleanup、共享 shutdown task、任务 ownership 和 disposal 均完整读取；追加 R1-UI-04 修复。 |
| 拖入/拖出/窗口/monitor | 两个窗口所有 handler 全读；OverlayVM 的 generation/isCurrent 避免旧 drop 完成回调恢复取消状态。monitor/drag 原生服务由本轮 App Services 全读。 |
| 外观与 priority | settings controls/editor、Overlay VM pending settings、OverlayWindow.ApplyTheme、岛内 XAML palette/shape/text/active gate 全读。独立 island theme 设置和手动内容选择保持；Follow Windows 的 AppsUseLightTheme/WinUI 来源由服务分区确认。 |
| Media/lyrics/AI/GPU | 全部 media VM、三套播放器视图、歌词行/字号编辑、AI/Qq/Netease 卡片、DLC/下载 UI 全读；R1-UI-02/03 已修。推理/cache/GPU admission/native glow 由其他本轮分区覆盖。 |
| 编辑/持久化 | NativeSettingsEditor、settings controls/behavior、即时/debounce 保存、download limits、unload flush、MainPage sync 全读。migration/settings recovery 由 root/服务/基础设施分区负责。 |
| XAML/locale/工程/manifest | 全部正文完整读取；双语确认 R1-UI-03。没有以资源匹配替代 handler 全读。 |

## 验证边界

本轮执行了源码阅读、resulting diff 阅读，并通过本分区八个修改文件的 `git diff --check`（空白格式检查）；**未运行任何测试、build、应用、Windows UI 或性能测量**。是否编译、实际 WinUI effective visibility、跨 DPI 拖放、透明窗口 hit-test、动画、主题事件、GPU/SMTC 和启动/退出耗时均须后续 authorized verification。直接从 Application.Resources 取 brush 的 imperative 卡片在主窗手动主题切换时是否刷新是需原生确认的观察，未作为确认缺陷计数。生产修改仅上述 scoped App/UI/startup 代码；基础设施对 shared MainVM 方法的独立 patch 保留。

## 完整读取清单

以下路径相对于不可变快照根目录，覆盖均为完整 `1..N` 正文（包含未换行的 lock file 最后一行）。

| 文件 | 物理行数 |
|---|---:|
| `src/DropSpace.App/App.xaml` | 19 |
| `src/DropSpace.App/App.xaml.cs` | 967 |
| `src/DropSpace.App/Converters/BoolToVisibilityConverter.cs` | 23 |
| `src/DropSpace.App/Converters/NullToVisibilityConverter.cs` | 23 |
| `src/DropSpace.App/DropSpace.App.csproj` | 166 |
| `src/DropSpace.App/DropSpace.rc` | 33 |
| `src/DropSpace.App/MainWindow.xaml` | 22 |
| `src/DropSpace.App/MainWindow.xaml.cs` | 439 |
| `src/DropSpace.App/OverlayWindow.xaml` | 317 |
| `src/DropSpace.App/OverlayWindow.xaml.cs` | 2377 |
| `src/DropSpace.App/Package.appxmanifest` | 54 |
| `src/DropSpace.App/Properties/AssemblyInfo.cs` | 3 |
| `src/DropSpace.App/Properties/launchSettings.json` | 7 |
| `src/DropSpace.App/Strings/en-US/Resources.resw` | 878 |
| `src/DropSpace.App/Strings/zh-CN/Resources.resw` | 878 |
| `src/DropSpace.App/ViewModels/ClipboardIslandViewModel.cs` | 94 |
| `src/DropSpace.App/ViewModels/ItemCardViewModel.cs` | 208 |
| `src/DropSpace.App/ViewModels/MainViewModel.cs` | 1844 |
| `src/DropSpace.App/ViewModels/MediaViewModel.cs` | 290 |
| `src/DropSpace.App/ViewModels/NativeSettingsEditor.cs` | 149 |
| `src/DropSpace.App/ViewModels/NeteaseEnhancementViewModel.cs` | 72 |
| `src/DropSpace.App/ViewModels/OverlayViewModel.cs` | 511 |
| `src/DropSpace.App/ViewModels/QuickActionButtonViewModel.cs` | 34 |
| `src/DropSpace.App/ViewModels/SystemActivityViewModel.cs` | 31 |
| `src/DropSpace.App/ViewModels/WidgetViewModel.cs` | 95 |
| `src/DropSpace.App/Views/ContentDialogLifetime.cs` | 94 |
| `src/DropSpace.App/Views/DesignTokens.xaml` | 68 |
| `src/DropSpace.App/Views/Island/ClipboardIslandView.xaml` | 32 |
| `src/DropSpace.App/Views/Island/ClipboardIslandView.xaml.cs` | 18 |
| `src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml` | 63 |
| `src/DropSpace.App/Views/Island/ExpandedIslandMusicView.xaml.cs` | 332 |
| `src/DropSpace.App/Views/Island/MediaCompactView.xaml` | 50 |
| `src/DropSpace.App/Views/Island/MediaCompactView.xaml.cs` | 348 |
| `src/DropSpace.App/Views/Island/MediaExpandedView.xaml` | 50 |
| `src/DropSpace.App/Views/Island/MediaExpandedView.xaml.cs` | 342 |
| `src/DropSpace.App/Views/Island/MediaRenderQueue.cs` | 48 |
| `src/DropSpace.App/Views/Island/WidgetsExpandedView.xaml` | 9 |
| `src/DropSpace.App/Views/Island/WidgetsExpandedView.xaml.cs` | 154 |
| `src/DropSpace.App/Views/MainPage.Settings.cs` | 124 |
| `src/DropSpace.App/Views/MainPage.xaml` | 822 |
| `src/DropSpace.App/Views/MainPage.xaml.cs` | 2424 |
| `src/DropSpace.App/Views/Music/AiLyricsSettingsCard.cs` | 507 |
| `src/DropSpace.App/Views/Music/LyricsFontSizeControl.cs` | 209 |
| `src/DropSpace.App/Views/Music/LyricsFontSizeEditSession.cs` | 125 |
| `src/DropSpace.App/Views/Music/LyricsGlowModeControl.cs` | 35 |
| `src/DropSpace.App/Views/Music/LyricsRowCollection.cs` | 65 |
| `src/DropSpace.App/Views/Music/MusicPage.cs` | 489 |
| `src/DropSpace.App/Views/Music/NeteaseEnhancementCard.cs` | 147 |
| `src/DropSpace.App/Views/Music/QqMusicLoginCard.cs` | 69 |
| `src/DropSpace.App/Views/Settings/DlcPage.cs` | 277 |
| `src/DropSpace.App/Views/Settings/DownloadActionPanel.cs` | 45 |
| `src/DropSpace.App/Views/Settings/DownloadPanel.cs` | 323 |
| `src/DropSpace.App/Views/Settings/SettingsEditBehavior.cs` | 46 |
| `src/DropSpace.App/Views/Settings/SettingsForm.cs` | 128 |
| `src/DropSpace.App/Views/Settings/SettingsValueSlider.cs` | 91 |
| `src/DropSpace.App/Views/Settings/WidgetEditorView.cs` | 337 |
| `src/DropSpace.App/app.manifest` | 21 |
| `src/DropSpace.App/packages.lock.json` | 346 |
| **合计：58 文件** | **17,772** |
