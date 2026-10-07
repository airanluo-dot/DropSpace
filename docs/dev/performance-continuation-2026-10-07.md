# DropSpace 继续优化与四轮复核（2026-10-07）

本记录接续 [上一轮优化](performance-optimization-2026-10-07.md)。开发分支为 `work`，HEAD 为 `6a1586a01e769c8b71492bcb18fb970047753434`；开始本轮时已与远程 main 一致。保留全部未提交的分页后台边界、音乐岛排帧合并、父面板隐藏生命周期、首帧准备和动画偏好快照修改。本轮没有发布、推送或合并 main，也没有修改 App 版本、CUDA ZIP 或模型。

## 证据与优先级

下表的“确认”表示调用关系或实际管理代码实验确认了工作，不表示已经测得 Windows UI 卡顿。排序综合用户影响、可减工作和回归风险。

| 优先级 | 问题、代码依据与状态 | 处理范围及风险 |
|---|---|---|
| 1 | **确认**：`MainViewModel.InitializeAsync / TogglePinAsync / RefreshFileAvailabilityAsync` 调到 `SqliteItemRepository`；Microsoft.Data.Sqlite 的异步接口同步执行。实际 10,000 条测试库中 1,000 项置顶入口中位占用约 32 ms。 | 完整初始化、目标写操作及 Count/Get 尾读建立后台边界；写入口保留同一数据库 gate 的排队及事务/取消语义。查询总耗时未必减少。 |
| 2 | **确认**：`MediaViewModel.Session` 的 Position/LastUpdated 变化通知无关元数据及四组命令；`Spectrum` 每帧重发通常不变的呈现布尔值。 | 按原 getter 的依赖发送派生通知，保留 Session/Spectrum、换歌和能力变化。风险在依赖漏项，聚焦验证暂停、时间线起止和语言刷新。 |
| 3 | **确认**：`DownloadManager.Start / RunAsync` 的 lifetime-linked CTS 随历史保留至重试/删除/退出；真实 100 次 loopback 下载保留 100 个源及父注册。 | 每个 run 退出后释放，保留历史及 linked shutdown 取消；必须等待真实取消回调，不能在锁内 Dispose。 |
| 4 | **确认**：`OverlayWindow.ApplySnapshot` 的同步 Compact 准备发 geometry 事件，随后已读最新几何却再排 Apply。Service 的两次 Apply 在普通场景重复，但双屏嵌套隐藏对照证明第二次仍有补偿作用。 | 仅去同步首帧准备范围的新增几何排队；Service 保持原状，避免改变双屏目标序列。原生呈现需 Windows 验证。 |
| 5 | **待验证影响**：旧 Main card 移除后缩略图任务仍以 VM lifetime 为取消所有者；服务槽位限定同时活动任务，等待者可积累，完成后会退休。 | 不直接称永久泄漏；需要慢盘/快速导航实机计数后决定是否加入 card 退休所有权。本轮不扩大 MainViewModel 重构。 |
| 6 | **待验证**：原生媒体时间线事件重取会话/封面、glow 栅格及 native region/backdrop；下载持久化信号 burst 可能残留空唤醒。 | 缺少本轮代表性实机或空唤醒计数，先保留。暂不改 SMTC 状态机、动画质量、框架或调度机制。 |

## 第一轮：响应与高频路径

数据库改动位于 `SqliteDatabase.InitializeAsync` 与 `SqliteItemRepository` 的 Count/CountClipboard/Get、SetPinnedMany/RestorePinnedStates/MarkUsed/UpdateFileStatus。完整操作在后台执行，VM 仍在原 await 后发布 UI。写入口先排队原 WriteGate；取得 slot 后以不可取消的调度保证 owner 能完成 finally，业务 token 仍传入 SQL。pin 输入与 undo 字典仍在调度前快照。SQL、事务提交、durable tail、schema、索引与排序不变；已初始化快路不排后台任务，分页不重复包装。

真实 SQLite，同一 10,000 条文件引用库、同操作顺序/次数，Linux .NET 10.0.12；除新空库 10 次，各场景 30 次。前后为不同进程的非交替运行，未控制冷盘缓存，不作为 SQL 吞吐结论。

| 场景 | 调用线程占用中位数：前 → 后 (ms) | 总耗时中位数：前 → 后 (ms) |
|---|---:|---:|
| 已有库首次 owner 初始化 | 0.7756 → 0.0178 | 0.7765 → 0.7410 |
| Count | 1.0046 → 0.0141 | 1.0050 → 1.0290 |
| 单项置顶 | 0.1627 → 0.0164 | 0.1631 → 0.1292 |
| 1,000 项置顶 | 31.9510 → 0.1585 | 31.9532 → 16.5171 |
| MarkUsed | 0.0218 → 0.0117 | 0.0218 → 0.0785 |
| 文件状态更新 | 0.0339 → 0.0135 | 0.0340 → 0.1105 |
| 写后 Get | 0.0831 → 0.0124 | 0.0834 → 0.1287 |

改善的是调用线程可继续处理工作。表中 batch 总时长变化受缓存/调度条件影响，不声称 SQL 更快。小操作增加调度代价：30 次 MarkUsed 的进程 CPU 1.909 → 7.724 ms，状态更新 2.760 → 8.223 ms；Count/Get/微写平均新增约 422–960 B 分配，batch 约 1,696 B。原始进程级测量含工具开销，不能据此推算整个 App CPU 或内存。初始化快路及未改 startup recover 仍同步完成；Task 是否恰好已完成不是线程归属保证。见 [SQLite 原始及对照](evidence/performance-continuation-2026-10-07/r1-sqlite-comparison.json)。

媒体改动位于 `MediaViewModel.Session / Spectrum / Settings`，仅按 getter 的真实依赖变化发派生通知；每次主 Session/Spectrum 通知保留。相同的 300 个预分配 timeline 记录，属性通知 **4,500 → 300**、命令通知 **1,200 → 0**、管理分配 **314,400 → 86,400 B**；300 个 loopback spectrum frame，属性通知 **600 → 300**、管理分配 **57,600 → 50,400 B**。getter、歌词/轨道身份、时间边界、暂停保谱及本地化策略不变。移除周期性全量通知后，Language 变化显式刷新既有 PlayPauseLabel。实验执行真实 ViewModel/Core/MVVM toolkit 源码，仅 ImageSource 使用 Linux 类型 shim；没有测渲染或原生 CPU。见 [媒体对照](evidence/performance-continuation-2026-10-07/media-view-model-r1.json)。

聚焦验证：Infrastructure Release 编译通过，**42** 个用例通过（CUDA 包合约 29、数据库原边界 2、新边界 7、原 batch pin 4），覆盖真实 table lock 时调用者继续运行、准入顺序、排队/准入后取消、输入快照、回滚及 pin undo。媒体 **14 个不同用例**通过（既有歌词 8、新通知场景 5、前后计数 1）；实际 WinUI 引用 C# 子集编译零警告/错误。它不包含 XAML 生成或 native 控件运行。测试记录存于本记录的 [证据目录](evidence/performance-continuation-2026-10-07/)。

交叉复核未发现与前轮 UI 发布/渲染生命周期冲突。SQLite 原生 busy 等待不能强制中断；取消后 gate 仍由实际操作持有至退出。启动 Undo recovery、删除 finalization、batch 查询和其他 repository 入口尚未全面后台化，不以本轮有限边界宣称全仓 SQL 已隔离。

## 第二轮：长期运行与资源生命周期

`DownloadManager` 原先让 Work/history 同时持有 snapshot 与已结束 run 的取消源。现在 Start 捕获该 run 的局部 source/token；Control 与 Shutdown 在同一锁内记录首次 CancelAsync Task；Run 退出时撤下 Work.Stop，锁外等待首次显式/父取消回调实际结束，再 Dispose。保留 lifetime-linked 提前关闭取消、动作 gate、旧 run 完整结束后才能 Resume、历史记录、交付文件和恢复规则。删除的仅是被这个 run finally 替代的三个延后 Dispose 路径；它们的入口本就等待 Run 完成，没有删除受支持业务路径。

同进程中每次用真实 DownloadManager/HttpRangeDownloader/DownloadTaskRepository 下载 loopback 3 B，顺序 100 次并等待每个真实 Run 结束；前后探针为不同进程、相同条件。完成历史均为 **100 条**，历史 Work.Stop **100 → 0**，父 lifetime 活动取消注册 **100 → 0**。捕获的 source weakref 在强 GC 后 **100 → 1**；最后一个对象是否被探针/最后任务引用未区分，不声称全部回收或 App RSS 降低。总 probe wall time **349.4154 → 388.0049 ms**，不把生命周期改善包装成下载提速。

取消回调错误由发起的 Control/Shutdown await 交给调用者；退休只等待同一回调任务结算，避免把这份已公开的错误再次复制成 Run 故障而永久挡住重试。Shutdown/Dispose 用 finally 确保取消回调抛错后仍进入既有运行等待、标记清理、journal drain 和资源释放。**没有通过吞错误掩盖故障**：聚焦测试直接断言调用者仍收到原 callback AggregateException，并验证后续恢复和释放。本轮未引入一般错误聚合框架；取消与清理同时出现多个独立异常时仍遵守通常的 finally 错误覆盖规则，不宣称任意故障下都能完整清理。

Infrastructure Release 编译通过，**6 个不同用例**最终通过：完成历史、失败重试、排队暂停/恢复/取消、显式和父级阻塞回调（含重叠 Shutdown）、回调抛错后的错误传递/重试/drain/Dispose，以及既有恢复/分页历史保护。首次回调故障测试误认为 .NET Timer.Change 在 disposed 后抛异常；实际 .NET 10 契约返回 false。已纠正断言，仅补跑此用例，生产代码未为此修改。初次日志/TRX 和补跑通过结果都保留。Windows 文件锁 cleanup 用例本轮未执行；Linux 提前 return 不能算 Windows 验证。

复核 DownloadPanel 订阅/卸载、MusicPage generation/图标退休、MediaExperience 任务/channel 限额与退订，未发现值得立即修改的另一个长期资源问题。历史/checkpoint 随历史保留有明确产品所有者，未当作缓存泄漏自动删除。持久化信号 burst 的空唤醒尚未计数，留待验证。

## 第三轮：架构与改动交叉复核

最终生产修改仅在 `OverlayWindow.ApplySnapshot / OnMediaGeometryChanged`：Compact.RefreshForPresentation 期间用局部 scope 标记、try/finally 恢复；同一外层调用随后读取最新 IdealIslandWidth/Height，因此这次同步尺寸事件无需再排 Apply。保留已有 pending 回调，普通帧/viewport 几何变化仍走原队列；没有改变初始尺寸、出场生命周期、动画质量或状态 authority。

编译提取的完整真实 Service/Window 方法，使用真实 Core 协调器及 Fullscreen policy；仅窗口、timer、monitor、控件和 dispatcher 原生边界用可控 shim。最终 **13 项聚焦断言**通过、两版本 C# 编译零警告/错误。首帧准备产生的新几何排队 **1 → 0**，对应窗口方法调用 **2 → 1**；两者仍读到输入的 **420×76** 新尺寸，准备次数均 1。已有排队继续保持 1，读取 **390×72**；普通几何事件仍合并，异常及嵌套 scope 的 finally 正确恢复。这些数值是管理方法的计数/边界契约，不能作为 WinUI 排版、帧率或 CPU 结论。

架构复核保留了一个重要的兼容路径：`OnSnapshotChanged → UpdateFiles → OnExperienceChanged → ApplySnapshot` 后，外层第二次 Apply 在普通场景看似重复。双 monitor 对照中，前一个非活动窗口的 HideImmediately 推进 CompleteFileDismissal，嵌套 Hidden 事件被既有 `_applyingSnapshot` guard 阻挡；基线第二次 Apply 把后一个窗口从 Dismissing 校正为 Hidden。删除后实际 authority 已 Hidden，但窗口最后目标仍 Dismissing。因而 Service 修改已逐字节恢复基线，最终双屏序列仍为 **[Dismissing, Hidden]**。没有追加 pending 循环或替换状态机去扩大这次优化。

前两轮重新检查了 UI 状态发布、数据库 gate/业务 token、取消源与 callback 错误归属、父面板渲染生命周期及暂停/换曲依赖，未发现本次普通路径的新冲突。职责仍沿原层级：repository 拥有 SQL/gate/连接，VM 拥有 UI 发布，下载 run 拥有 source，协调器拥有可见状态，窗口只投影该状态。本轮没有删除受支持的主窗口音乐视图、兼容 download API 或发布 schema，也没有新建通用调度/状态抽象。

## 第四轮：追加文件拖入 SQL 边界

三轮后的交叉取证确认 `MainViewModel.AddPathsBatchAsync:912 → SqliteItemRepository.AddFileCoreAsync` 在文件检查完成后仍逐项同步执行 SQL。真实已初始化 owner、预先准备好的 candidates/metadata，100 个新引用全部在原 caller 同步完成，累计占用 **63.6136 ms**，完整 SQL 循环 **63.9721 ms**；100 个已有引用复用同样同步，占用 **19.0981 ms**。这仅测 SQLite 阶段，没有文件检查/复制或 Windows UI 帧。

追加修改仅抽出共享 AddFile SQL Core：原参数检查、WriteGate caller 准入及 finally 保留；取得 slot 后后台执行整个 connection/duplicate/insert/transaction/reload 操作。Space 去重保留原 ID/metadata/revision，owned payload 先插入再关联，新记录 `Commit(None) → reload(None)` 的不可取消持久提交尾段原样保留。VM、生产 ClipboardFiles 的独立 batch transaction、存储格式和文件行为没有修改。

同条件另一进程对照：100 个新引用的**累计调用线程占用 63.6136 → 1.8132 ms**，单调用中位数 **0.6114 → 0.0212 ms**；100 个重复引用 **19.0981 → 0.9613 ms**。后测任务会在不同 continuation 上进行，这个累计值不等于一段 UI 卡顿。总 SQL loop 新引用 **63.9721 → 45.5765 ms**、重复引用 **19.1740 → 23.5423 ms**；单轮不同进程的非交替测量不证明 SQL 提速。重复引用 CPU **19.240 → 25.712 ms**、平均每调用多约 **467–486 B** 分配。Owned 导入生产路径本就由 StagedFileImportService 在后台拥有整个文件工作，共享入口多一次调度是代价：10 次 loop **8.4051 → 9.2672 ms**、CPU **8.434 → 9.809 ms**，不冒认其 UI 收益。单条 Clipboard API 是保留的兼容入口，生产使用另一个后台 batch 入口，不以接口测量冒认生产收益。详见 [文件引用对照](evidence/performance-continuation-2026-10-07/r4-addfiles-comparison.json)。

Infrastructure Release 编译通过，**11 个用例**通过（新 SQL intake 边界 3、已有 staged import 4、源文件保护/Clipboard 独立事件/owned cleanup/Unicode 去重各 1）。覆盖真实 table lock 时 caller 继续运行、FIFO 准入、中途取消、原行及 metadata 复用、owned 关联与 staging 生命周期。没有测试 Commit 和 reload 瞬间恰好到来的取消；该时机靠保持原不可取消尾段与结果检查，本轮不伪称动态验证已覆盖。

完成后停止增加生产范围。后台化不代表 SQL 计算或总时长更低，线程池原生 SQL 等待也不能强停；保持 owner 等待实际退出、gate/connection/transaction 释放，比提早报告取消更重要。

## CUDA #102 兼容基线

#102 仍为 open/draft，核验 head 为 `b9bfde8f9d2473b16aa155266af64de3ea4198b6`。确认未包含后，已把该 PR 的完整 13 文件 diff 应用到当前工作区，未合并该草稿到 main。逐文件 LF 源码哈希匹配 PR head；#100 官网修正及 #101 组件资源过滤没有被覆盖。新旧 schema 发布校验及旧 Beta16 下载资产保持可用。

独立组件为 `cuda-llama-cpp-v0.5.0-cuda13-win-x64-v1`，固定 ZIP 大小 **540873572 bytes**，SHA256 `79e8deb4f8c35e7c9c94da0efa0752826bafe510fcb1e54da23062f2d6f40a0b`。源码仍绑定固定来源/大小/哈希、接口 profile/protocol、小型 App 描述文件及安装复用身份。仅核验公开元数据和小型 manifest；没有下载 ZIP、模型或重跑 GPU 推理。

Python 合约 8 通过、1 因缺 PowerShell 跳过；独立组件加入审批源码指纹的回归 1 通过；新旧发布校验及资源过滤 Node 71 通过；C# CUDA 包合约 29 通过（复用第一轮编译，不重复运行）。PR head 的 CUDA 合约 Windows CI 成功，但普通 Windows CI 被旧 Beta16 输入保护拒绝，不能视为本轮完整 App CI 通过。最终实际源码指纹为 `a81cf9c895215b29c0c6f92056ca4b84a3f22cbc5dad737d7742cd22364e7b4d`，真实审批验证正确拒绝旧审批，未改审批或 staging 新描述文件。最终在线复核 main 仍为 `6a1586a…`、#102 head 仍为 `b9bfde8f…`。详见 [兼容证据](evidence/performance-continuation-2026-10-07/cuda-compatibility.json)。

## 审查、回退与验证边界

代码仍为 `work` 分支未提交的可审阅结果。每组只连同对应聚焦测试处理；六份独立 patch 的 `git apply --reverse --check` 均通过，没有实际回退工作区。它们以本轮开始时已有优化为基线，R3/R4 不混入旧改动。执行真实回退前仍应检查届时工作区，避免覆盖后续会话修改。

| 组 | 生产范围 | 独立审查补丁 |
|---|---|---|
| CUDA 基线 | #102 全部 13 文件 | [cuda-pr102.patch](evidence/performance-continuation-2026-10-07/cuda-pr102.patch) |
| 第一轮 SQL | SqliteDatabase、SqliteItemRepository 目标入口 | [r1-sqlite.patch](evidence/performance-continuation-2026-10-07/r1-sqlite.patch) |
| 第一轮媒体 | MediaViewModel | [r1-media.patch](evidence/performance-continuation-2026-10-07/r1-media.patch) |
| 第二轮 | DownloadManager | [r2-download.patch](evidence/performance-continuation-2026-10-07/r2-download.patch) |
| 第三轮 | OverlayWindow 同步几何 scope | [r3-overlay-geometry.patch](evidence/performance-continuation-2026-10-07/r3-overlay-geometry.patch) |
| 第四轮 | SqliteItemRepository 共享 AddFile Core | [r4-file-intake.patch](evidence/performance-continuation-2026-10-07/r4-file-intake.patch) |

按上一轮源码 LF 哈希核对，10 个既有交付文件均保留：9 个完全相同，OverlayWindow 的 R3 前快照与上一轮完全相同，最终只叠加独立的首帧几何 scope。最终源码、patch 哈希及只读检查见 [validated-sources-final.json](evidence/performance-continuation-2026-10-07/validated-sources-final.json)。媒体文件最后只做仓库要求的 CRLF 规范化，测量时 raw hash 与最终 LF hash 分开记录；没有因此重复编译或重跑同一产物。

新增生产修改完成后各轮必要 Core/Infrastructure Release 编译及相关测试通过；真实 WinUI 引用的媒体 C# 子集编译，以及真实完整窗口方法提取编译通过。完整 Windows App/XAML 编译仍未验证：前轮已记录 Linux 无法执行 XamlCompiler.exe，本轮未重复构建相同的不可运行工具链，也未以 C# 子集代替完整 App 编译。未改 csproj、依赖锁、版本、发布资产或审批记录；未拆 DLC，也未开展 NovaClip/B站模块工作。

## 实机待核对与停止边界

当前环境为 Linux，无法执行 Windows 桌面呈现。本轮管理代码计数、SQLite 入口占用和 loopback 资源计数不能替代 Windows 启动、拖入/取出、展开/收起/隐藏、显示器切换、真实播放/快速切歌、长时间 CPU/内存/原生句柄曲线。

具体剩余项：启动 Undo recovery、删除 finalization、batch QueryDropBatch 等 SQLite/文件入口仍有同步路径，空库/无 pending 的低成本测量不足以确定其实际用户影响；没有统一改写所有 mutation。旧 Main card 缩略图任务缺少 card 退休取消，等待者可在快速导航/慢盘积累但完成后会释放；同 card 重新定位文件与旧 availability/thumbnail 结果交错的代码可达，尚未本轮复现，需要 revision/path 所有权证据后再改。SMTC 时间线的元数据/封面重取、native glow/region、持久化空信号唤醒仍缺实机或计数依据。Service 顺序第二次投影明确保留其双屏嵌套兼容作用；替换该投影机制将是另一项需要更充分验证的架构工作。

应在同一 Windows 机器、同数据根快照、DPI/主题/媒体/网络条件下比较：启动与空闲；1,000 项置顶和取消；音乐 Compact ↔ Expanded ↔ Hidden、暂停恢复及快速切歌；下载完成/暂停/恢复/取消后资源 checkpoint；连续使用后再次响应。复用已有 MusicVisualSmoke、Overlay lifecycle / geometry pressure、LyricsRapidSkip 诊断，不将旧发布版实机记录当作当前代码通过。
