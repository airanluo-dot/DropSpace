# Beta6 第四轮维护与验证 — 2026-10-31

## 起点与结果边界

继续同一 **`v1.1.0-beta.6`**，直接基于前三轮最新工作区迭代，版本未递增，升级基线仍为
已发布 Beta5。未提交、推送、打标签、产出 Windows 包或发布。起点 63 个变更/新增文件
保存为 `artifacts/beta6-round4-validation/round3-baseline.{tar.gz,patch,json}`。

本轮有意修改了前轮的 `DownloadPersistenceQueue.cs` 和 `StagingLeaseStore.cs`，分别
修复遗漏的退出竞态与发现的跨模块回归；其余 53 个前三轮已修改的源码、测试、工具、
网站/Worker 文件经 SHA256 检查逐字节保留。历史执行记录不被覆盖，新结果只记入本轮。

环境继续为 Linux x64、.NET 10.0.401、Node 24.19.0、Python 3.12.14。四个并行代理负责
App、Network/Sharing、Storage 以及独立审阅/下载队列，另有只读协议子复审。主代理
负责冲突协调、交界条件复审、隔离基线验证、集成与账本。没有运行完整回归、模型、
原生压力、CI 或部署工作流。

## 架构与修复

| 问题 | 实施及保持的约束 |
| --- | --- |
| 第三轮持久 lease 校验调用 GetLeasePath，而它会 EnsureCreated 所有 App 目录；Updates 目录被文件占用时，分享 finally 会在清除明文前抛错，覆盖成功结果或原上传错误 | `StagingLeaseStore.GetLeasePath` 改为纯路径计算。Acquire/Recovery 仍在自身入口初始化；Renew 先读取既有持久记录。与分享层协作验证成功会话、原错误、撤销及明文清理均保持 |
| 下载持久化先检查 accepting 后入队，可能在 Drain/Dispose 已令 worker 退出后留下没有消费者的 completion | 以短 `_admissionGate` 原子完成准入、入队、通知及关闭准入；多个 Dispose 返回同一完成任务；仓库 IO 和取消不持锁 |
| 合并进度时生产者可决定不发通知，而 worker 正好看到空队列开始休眠，更新滞留 | worker 在相同短锁内决定 GetProgressWait，等待动作在锁外；继续合并进度，不改为每次报告均唤醒 |
| Internet Share 检查调用方 MaxItems，却只使用固定 2 GiB 上限而忽略较小 MaxTotalBytes | 在源打开、暂存、密钥/后端创建前使用调用方与协议上限的较小者。默认及协议上限均不变 |
| 选中前一文件刚好用尽字节预算时，后续只含零字节文件的目录也被整体拒绝 | 移除目录枚举前的总字节已满判定，保留每个子文件的剩余预算检查、项目数/遍历/路径/重解析限制；正字节仍拒绝 |
| Island retrieval 查询失败后即使下次成功仍保留 SearchUnavailable | 只标记并清除刷新拥有的失败；复制/验证等操作反馈不因后台刷新被清除 |
| 卡片 220 字符摘要每次绑定读取会先复制整个正文、替换两次换行，且截断可能拆开 surrogate pair | 先 trim/slice 保留片段，再在有界片段替换 CR/LF；原空白规则保持，截断避开补充 Unicode 字符中间 |

本轮发现的分享清理问题是第三轮组合代码的真实回归，不是仅理论上的异常路径。
在由快照恢复的隔离工作树中执行原有两项分享测试，两项均失败；之后通过纯路径修复
消除故障。测试从“Updates 故障模拟 lease 删除失败”改为真实语义：无关目录故障不应
导致 lease 删除失败。另加真正 lease 记录权限故障测试，保留“明文先删、记录可重试”
的验收，未通过删除错误断言来隐藏问题。

没有新增生产层、公开服务 API、数据库/schema、包依赖、后台活动或界面资源。视觉布局、
材质动画、交互入口、默认值、模型/CUDA/provider/AI 机制不变。独立复审检查了 App
状态来源与 Unicode、网络预算、持久化发布/退出锁和租约调用初始化边界；主代理补充
复审合并进度的 idle-wait 交错。未发现此次已落地修改的阻塞回归。

## 实际执行账本

| 执行 | 实际结果 | 范围/证据 |
| --- | --- | --- |
| 第三轮隔离快照上的两项原分享清理测试 | 2 次预期基线失败 | `round4-share-cleanup-baseline.trx/.txt`；原始成功结果/上传异常被无关目录创建故障影响 |
| 分享、传输字节边界及暂存集成过滤 | 13 通过，0 失败/跳过 | `round4-share-storage.trx`；自定义预算 2、零字节边界 2、既有枚举预算 1、分享恢复 3、真实 lease 记录清理故障 1、第三轮 custody 4 |
| 下载持久化过滤 | 5 通过，0 失败/跳过 | `round4-download-persistence.trx`；共享退出、drain、合并进度、16 个发布者有界并发及第一轮删除重试回归 |
| App probe 修复前 | 2 通过、3 次预期基线失败 | 查询错误无法清除、surrogate 被拆开、摘要分配 1,800,536 bytes；操作反馈与空白兼容性已通过 |
| App probe 修复后 | 5/5 通过 | `retrieval-feedback-after.json`；包括实际生产 card/retrieval 源码；before JSON 保留于同目录 |

累计 **30 次用例/场景执行：25 通过、5 次预期基线失败**。最终 **18 个不同的
Infrastructure 用例、5 个 App 场景均通过**，没有跳过结果计作通过。队列竞态由源码
交错确认，新增有界并发测试不宣称固定复现原来的精确抢占窗口；合并进度用例在 drain
之前等待真实进度写入，未借额外 critical 操作唤醒。

首次 App fixture 编译曾因 TextPayload 构造参数错误失败，尚未执行场景便已修正；随后
基线/修复版本各执行五场景，未额外重跑。新 allocation 检查使用 300,000 字符合成正文，
只约束摘要格式化的分配，不能当作 App 内存峰值或吞吐 benchmark。

分享/存储过滤：

```text
FullyQualifiedName~InternetShareByteBudgetTests
|FullyQualifiedName~TransferSourceByteBoundaryTests
|FullyQualifiedName~EnforcesItemAndByteLimitsBeforeReturningAnIncompleteManifest
|FullyQualifiedName~ShareCleanupRegressionTests
|FullyQualifiedName~StagingLeaseCleanupBoundaryTests
|FullyQualifiedName~StagingLeaseCustodyTests
```

队列过滤：

```text
FullyQualifiedName~DownloadPersistenceAdmissionTests
|FullyQualifiedName~DownloadPersistenceTests
```

两组实际使用 `dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj
-c Release --no-restore --filter ...`，编译 Core/Infrastructure/fixture/测试程序集，只执行
所选用例。App 命令为 `dotnet run --project scripts/retrieval-feedback-probe -c Release`，
直接链接实际 ViewModel/Card/QuickAction 源码，保留 180ms debounce；未执行 WinUI/剪贴板。
可通过 README 中的源码路径参数选择保存基线，不复制算法。

所有本轮 TRX/JSON/基线日志位于 `artifacts/beta6-round4-validation/`。完整原始快照被
应用到自建临时 worktree 后运行基线，未用 Beta5 HEAD 冒充第三轮；验证后移除该临时树。

## 完整性与目标系统验证

- RELEASE_VERSION 保持 Beta6，发布说明继续合并本轮；README/ROADMAP 链接当前第四轮。
  版本一致性、MSBuild `1.1.0-beta.6` / `1.1.0.6`、源码/新增文件空白、项目 XML、生产
  引用图及已有文件哈希校验通过。
- workflow 范围审计通过；没有扩大自动套件。网站/Worker/语言资源未改，复用前轮
  相应静态构建/完整性结果，不重复计入本轮功能执行。
- 当前测试中的权限故障实际走 Linux 目录权限分支，明文先删除、记录保留、恢复权限后
  完成的断言通过；Windows ReadOnly 分支尚待目标机运行。
- 仍需 Windows 完整 App/XAML/MusicBackground/PRI 与 Installer/Portable/MSIX、Beta5
  升级、真实下载退出与日志锁、原生文件锁/ACL、虚拟文件和分享取消、局域网/两设备、
  检索/IME/拖放/选择、Unicode 显示、焦点/绑定及主题/DPI/多屏/无障碍验收。
- 托管 handler/后端/dispatcher fixtures 不是部署服务器、真实浏览器或原生桌面的
  验收。最终签名/哈希、模型授权、包与实际更新/API/下载资格仍未取得；没有发布。

## 明确未解决项与后续优先级

同根/嵌套暂存租约与 legacy `StagedFileImportService` 的联合所有权仍有结构性缺口：
当根已被删除但活动 lease 记录尚存时，另一次 Acquire 可能重用路径，旧活动 owner
随后能清理新数据。单独拒绝重复 root 又会触发旧 importer 在 Acquire 失败后仍删除
“admitted”文件；嵌套 root 还涉及祖先 lease 递归清理。已撤回未完成的孤立准入补丁，
没有把它计为修复或留下新的错误所有权分支。

后续应统一完成**重叠 lease 准入、导入 root 分组、仅由已持有 owner 清理**，并覆盖
冲突、部分准入、取消、恢复和嵌套目录。第三轮“已完成且记录已消失的旧 handle 不触碰
新 root”修复仍保留；它不等于解决上述“旧记录仍活动”的另一情形。

另有本地 SAS 确认等待期间取消后，远端 pairing offer 可能留到 TTL 到期的候选问题，
本轮未扩展原生配对测试/公共接口来处理。既有原生堆稳定性调查、macOS 和长时间运行
边界继续存在。不能以本轮通过结果宣称这些问题已解决或全项目已验收。
