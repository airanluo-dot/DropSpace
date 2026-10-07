# DropSpace 流畅度与资源开销优化记录（2026-10-07）

本轮完成三个可分开审查和回退的改动：分页数据库读取退出 UI 调用线程；音乐岛按呈现帧合并通知并由父面板拥有渲染生命周期；动画模式从已有系统偏好快照解析。没有修改产品功能、界面布局、动画参数、歌词匹配/AI 准入、数据格式、版本或发布流程。

初始审计/测量基线：`3192f14876f6c8351c21f79096969eb44974df0f`。开始时工作区干净。核对并 fetch 远程 main 后，发现最新 `6a1586a01e769c8b71492bcb18fb970047753434` 仅新增两个官网发布筛选脚本的改动；App/Core/Infrastructure、本轮修改和验证的应用文件全部相同。保留优化工作区并快进到该最新基线，没有发布或推送。应用测量无需因无关官网变化重跑。读取了 AGENTS、项目维护指南、README、ARCHITECTURE，以及本任务相关的产品、UX、设计和测试说明。定位以初始基线代码为准；当前改动可能使行号移动。

## 问题清单与取舍

“已确认”表示调用关系、执行线程或重复工作已有代码/实验依据，不表示已测得 Windows 卡顿、CPU 或常驻内存收益。

| 顺序 | 证据与根因 | 用户影响、收益与风险 | 本轮处理 |
|---|---|---|---|
| 1 | `MainViewModel.ReloadAsync:785` / `LoadMoreItemsAsync:834` → `ItemProjectionService.LoadPageAsync:18` → `SqliteItemRepository.QueryPageAsync:330–336`。Microsoft.Data.Sqlite 异步 API 同步执行，空闲 semaphore 和已完成 await 不会建立后台线程边界；SQL 和行物化留在调用线程。真实 SQLite 配对实验确认原入口同步返回。 | 搜索、导航、刷新和翻页会占用 UI。收益明确；独立查询 connection 和既有 UI 发布校验使改动风险较低。 | 已修分页入口。 |
| 2 | `MediaExperienceService:78,704–715` 每 33ms 发布 Position/Lyrics/Spectrum；`MediaCompactView:123–125` 对每项同步完整 Refresh；`:254–256` 每次再测词前缀宽度。 | 同一播放 tick 多次测量、改属性和更新谱柱。复用已有队列能直接减少工作；首帧几何和出场可见性有回归风险。 | 已合并到呈现帧；首帧尺寸保留同步准备。 |
| 3 | `OverlayWindow.ApplySnapshot:555–558` / `ApplyPageTransition` 选择叶子控件的 Visibility；HideImmediately 只收起父面板。Compact 仍订阅；Expanded 的 `CanRender:102` 只检查自身 Visibility。 | 播放发生在其他窗口/显示器时，隐藏岛仍刷新。缺少父面板生命周期归属。 | 两个实际岛视图都加入父面板活动门槛，隐藏/卸载/关闭取消排帧；出场动画仍按父面板实际可见状态刷新。 |
| 4 | `SystemVisualPreferenceService.Resolve:46–50` 调用 ReadPreferences；`OverlayWindow:1245,1267,1399` 等动画路径反复查询。 | 动画期间重复读取 UISettings、AccessibilitySettings 和 capabilities。调用计数收益明确，风险低。 | 从 Current 快照解析模式；保留 250ms 轮询和所有系统事件广播。 |
| 5 | `MediaViewModel.Session:80–95` 对时间线/时间戳变化发全部派生属性及四个命令通知；`Spectrum:143–144` 重发通常值未变的 HasSpectrumPresentation。 | 冗余通知已确认；合并视图刷新后的剩余成本未测。 | 保留，下一轮按实际影响筛选依赖。 |
| 6 | `WindowsMediaSessionService:788–791,234–306` 时间线事件重走会话发现、元数据/封面读取；`:401` 和 `:185` 可两次发布。 | 工作路径已确认，系统耗时待 Windows 测量。拆分会影响原生会话选择和发布 fence，回归风险较高。 | 未替换状态机。 |
| 7 | 启动 `App.xaml.cs:228` → `MainViewModel:643–645,1515` → repository Initialize/Count；pin `MainPage:1366` → VM:985 → use case:37 → repository:497；MarkUsed VM:1200 → repository:591；文件状态更新 VM:1449。 | 这些入口仍存在同步 SQLite/UI 执行路径；真实卡顿和收益未量化。事务写入、通知顺序和取消边界比分页复杂。 | 明确保留为后续项，未声称全仓数据库已后台化。 |
| 8 | `DownloadManager.Start:320` 创建 lifetime-linked CTS；`RunAsync:406` 退出没有释放，Stop 直到重试、删除历史或 shutdown 才 Dispose。 | 完成任务在历史中保留取消注册，长期累积路径已确认；实际资源量未测。直接在 finally Dispose 会与 ControlAsync 的 CancelAsync 竞争。 | 未自动删历史或冒险提前释放；后续需明确取消回调完成边界。 |

待验证热点：`MainViewModel.Items.Clear/AppendProjectionItems` 后旧 card 的缩略图任务仍用 VM lifetime token，可能在快速导航或慢盘上占用缩略图槽位；`IslandGlowController:169–174` 在 UI 执行栅格渲染，几何变化会使栅格缓存失效。它们没有本轮 Windows 帧耗时测量，不宣称为主要瓶颈。`Surface.UpdateLayout` 与原生 region/backdrop 同帧安全有明确联系，未凭重复调用的外观删除。

## 三组改动及生命周期

1. **分页**：`ItemProjectionService` 先构造不可变 query，再在 Task.Run 内调用整个 repository 查询，避免先在 UI 创建已执行 SQL 的 Task。结果集合仍由原 VM 在 UI 更新；revision、disposed、request-current 校验保留。查询每次创建独立 connection；取消 token、游标、异常均透传。没有扩大线程池、改变写事务、索引、分页或搜索规则。
2. **音乐岛**：Compact 复用 MediaRenderQueue，多个属性通知只使最新状态变脏；真正显示时最多安排一个呈现回调。父面板收起、卸载、关闭会撤销回调，重现会消费最新数据。排帧 generation 防止退休回调吞掉新请求；首次 Compact 显示在读取 IdealIslandWidth/Height 前同步消费脏数据。Expanded 同样采用父面板活动门槛。布局、词测量和动画算法保留；主窗口的 MediaExpandedView 没有改动。已有 native music smoke 增加真实控制刷新计数，并等待 CompositionTarget 帧后检查呈现。
3. **系统偏好**：Resolve 只覆盖快照中的 Motion 字段，其余材质、高对比、透明度、远程会话字段不变。原系统事件广播仍保留，包括快照值不变的颜色事件。AnimationsEnabled 没有本轮新增事件，变化最迟由原 250ms fallback poll 发现；此前逐帧读取可能更早发现，这是该优化的时效限制。

各组的生产文件、回归文件可单独回退；音乐岛组需要将两个视图、MediaRenderQueue、OverlayWindow、MusicVisualSmoke 和 MusicUiPressureTests 一起回退。没有删除业务/兼容代码。MediaExpandedView 仍是主窗口 MusicPage 的实际依赖，不能因新岛视图存在而删去。

## 优化前后证据

### 分页与搜索

Linux x64、.NET SDK 10.0.401 / runtime 10.0.12。真实 SqliteItemRepository、同一份 10,000 条记录测试库、每页 200 条；同进程链接基线和当前 ItemProjectionService，基线只改类名避免重名。预热 5 次，每场景 30 组，交替先测旧/新入口；包含普通页、两字符搜索、trigram 搜索的首页和下一页。seed 是固定时序 document 标题及约 900 字符文本，未使用用户数据。

| 首页场景 | 调用线程占用中位数：前 → 后 (ms) | P95：前 → 后 (ms) | 查询总耗时中位数：前 → 后 (ms) |
|---|---:|---:|---:|
| 普通分页 | 1.4809 → 0.0158 | 2.6674 → 0.0200 | 1.4812 → 1.5272 |
| 搜索 `do` | 16.4033 → 0.0331 | 17.1475 → 0.0547 | 16.4051 → 16.1541 |
| 搜索 `architecture` | 21.2078 → 0.0204 | 26.4971 → 0.0815 | 21.2085 → 21.5064 |

六场景中，原入口 180/180 次同步完成，新入口 0/180 次同步完成。收益是调用线程能尽快继续处理其他工作；没有证明 SQL 或 CPU 更快。`architecture` 首页 30 次的进程 CPU 为 668.795 → 667.492ms，分配为 25,920,064 → 25,928,048 bytes；差异包含进程级测量噪声。所有场景平均每次新增约 174–355 bytes 分配。后台调度有开销，不能据此声称内存降低。

另做 25 组真实 SQLite 分页与 pin 写入交叠；身份、顺序、页大小和最终 pin 状态均通过。这不是 Windows UI 延迟或数据库锁等待曲线。

原始数据：[projection-paired.json](evidence/performance-2026-10-07/projection-paired.json)、[projection-concurrency.json](evidence/performance-2026-10-07/projection-concurrency.json)。

### 高频音乐与偏好工作

30,000 次 Resolve，用同一计数平台 shim 执行真实基线/当前服务源码，排除构造。AnimationsEnabled、AdvancedEffectsEnabled、HighContrast 各从 30,000 次读取变为 0；capability Get 从 60,000 变为 0，Snapshot 从 30,000 变为 0。轮询/事件刷新仍读取系统。这是管理调用数量，不是原生调用耗时、CPU 或帧率测量。见 [preference-counts.json](evidence/performance-2026-10-07/preference-counts.json)。

每 tick 三个通知（Position/Lyrics/Spectrum）的 300 tick 队列回归：可见时实际排帧/消费 300 次，隐藏时 0 次；旧 Compact 同步入口对应 900 次是**源码推导**，没有执行旧 WinUI 控件。见 [media-scheduling.json](evidence/performance-2026-10-07/media-scheduling.json)。native smoke 已能检查真实控件的通知 burst、隐藏、快速重现、首帧几何，尚未在本环境运行。

## 验证与编译

- Core 重点测试 17/17：OverlayMaterialCapabilityTests、OverlayMotionControllerTests、LyricsMarqueePolicyTests。Core Release 编译通过。
- Infrastructure QueryPagingTests 6/6，Infrastructure Release 编译通过：keyset、legacy offset、trigram、Unicode 边界。
- 临时 net10.0 项目直接链接真实 App 纯服务/队列和正式测试：ItemProjectionServiceTests 5/5；MusicUiPressureTests、SystemVisualPreferenceTests 和可控平台 shim 检查共 17 个不同用例通过。包括取消前不启动、运行中取消、错误传播、旧渲染回调退休、隐藏脏数据和模式映射。shim 不提供 Windows 原生执行证据。
- 展开页补齐隐藏边界后，6 个已有 MediaSeekInteractionTests 通过；载体是从真实 MediaExpandedView.cs 逐字截取的纯交互类和直接链接的原测试，并未执行 WinUI slider、输入或计时器。已提交给播放器的 seek 不会撤回；父面板隐藏后清理本视图的计时器、预览和待确认状态。
- `node --test scripts/test-expanded-island-layout.test.mjs`：4/4。没有运行无关全量测试。
- Compact、Expanded 和真实 MediaViewModel/MediaRenderQueue 使用真实 WinUI/Windows SDK 引用进行 C# compile-only 检查，通过且零警告；临时 XAML 名称 partial 不能代替 XAML 编译或控件运行。日志见 [compact-expanded-compile.txt](evidence/performance-2026-10-07/compact-expanded-compile.txt)。
- WinUI Debug build 先因命令全局 RID 与 Core/Infrastructure 锁文件不符失败；纠正后 restore 成功、MSBuild 子节点异常退出；单节点诊断明确失败于 Linux 执行 `XamlCompiler.exe` 的 Exec format error。锁文件未更改，恢复正常 locked restore 后重点测试通过。完整 WinUI 编译、App.Tests 原生执行和 Release 嵌入资源检查均未通过/未运行，不以 C# 子集检查替代。

共执行 51 个不同 C# 重点用例和 4 个布局脚本用例，全部通过。TRX XML、失败 build 日志和最终源码哈希保存在 [evidence/performance-2026-10-07](evidence/performance-2026-10-07/)。测试工具及基线项目位于本次执行环境 `/tmp/dropspace-projection-{probe,tests}`、`/tmp/dropspace-performance-checks`、`/tmp/dropspace-compact-compile`；这些临时项目不是生产依赖。

## 剩余验证与风险

已读现有 OverlayWindowService 的 1,000 次生命周期、原生 handle/GDI/USER/private-bytes checkpoint、几何压力和无持续帧订阅诊断，以及 Test-MusicVisualSmoke、LyricsRapidSkipDiagnostic。Linux 没有 Windows 桌面，因此本轮没有启动、真实拖入/取出、展开收起/隐藏/屏幕切换、真实播放/连续切歌或长时间运行的 UI 响应、卡顿、CPU、常驻内存与后台活动前后曲线。

下一次可在同一 Windows 机器、同数据根快照、同 DPI/主题/歌曲/网络条件下分别运行基线和当前构建：启动与空闲；文件拖入/取出；音乐播放时 Compact ↔ Expanded ↔ Hidden 和显示器切换；快速切歌/暂停恢复；连续使用后再取资源 checkpoint。已有 `scripts/Test-MusicVisualSmoke.ps1` 新检查针对本轮显示生命周期；完整 Windows App 编译及 Seek、native glow/混合 DPI/出场动画仍是验证缺口。不要将旧发布版本的实机记录视为本轮通过。

后台取证没有确认新的文件 watcher 重复订阅：文件状态主要按 card/打开动作按需检查；metadata 工作后台化且本地 8/远程 2 槽位，远程取消保留槽位直到真实退出。预览 64 条/64MiB，歌词已有 quota/generation；Clipboard 单消费者、5 分钟维护、退订和 drain、下载 progress 单次 dirty 通知及持久化 coalesce 均有明确所有权。媒体/歌词/AI 保留 song/source/cache generation fence、原生工作预算和退休后实际完成才释放的资源。没有发现足以宣称新旧结果竞争已解决或所有长期资源均不增长的本轮实机证据。上述待处理项继续保留。
