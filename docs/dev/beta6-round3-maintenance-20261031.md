# Beta6 第三轮维护与验证 — 2026-10-31

## 同一版本，连续迭代

三轮开发全部属于 **`v1.1.0-beta.6`**，基于前一轮工作区最新代码继续迭代，升级基线始终
为 Beta5。此前助手把第二轮误标为 Beta7；本轮已恢复 RELEASE_VERSION、README、ROADMAP
及文件/包版本，合并全部改动到 Beta6 发布说明，并将第二轮记录更名为
`beta6-round2-maintenance-20261031.md`。没有另一个 Beta7 候选，也没有发布任何版本。

第二轮曾经实际产生的 `artifacts/beta7-validation/` 文件名和误标版本的 MSBuild 读数，
保留为历史执行事实，并在第二轮记录注明已纠正；不修改旧证据来伪装当时的执行结果。
第一轮记录保持完整。当前三轮发布说明只有 `.github/release-notes/v1.1.0-beta.6.md`。

起点快照：`artifacts/beta6-round3-validation/round2-baseline.{tar.gz,patch,json}`，
共 42 个变更/新增文件。最终哈希校验确认其中 34 个源码、测试、probe、Worker 和网站
文件逐字节保留；本轮是在其基础上新增修复，未回退或重新实现前两轮改动。

环境延续 Linux x64、.NET SDK 10.0.401、Node 24.19.0 和 Python 3.12.14，沿用已锁定
依赖。四个并行代理负责包/更新、数据/暂存、App、独立复审及 HTTP/文本边界；主代理
负责版本纠正、下载限速、集成和统一执行。代理的只读子复审不重复执行功能用例。

## 本轮修复与架构约束

| 问题 | 修复 |
| --- | --- |
| 更新其他设置时也会重新应用相同限速，token bucket 被补满、等待下载被无谓唤醒 | `DownloadBandwidthLimiter.SetLimit` 在锁内对相同速率直接返回；实际速率变更和取消仍正常。公开无参构造保留，内部 TimeProvider 仅让测试冻结时间，生产沿用系统单调时间 |
| 收到 HTTP 响应后，observer/重定向处理抛错会使响应及正文失去释放者 | `DownloadRequestPolicy` 在成功移交调用者前始终拥有响应，异常时释放；成功响应仍可由调用者读取，跳转信任规则保持 |
| 已取消的模块激活仍先创建 native job/尝试启动进程 | `ModuleWorkerClient.StartAsync` 在构造任何原生 owner 前检查取消；启动后发生的取消仍走既有退场路径 |
| 已完成暂存 lease 的旧 handle 再次 Complete 可误删新操作复用的同名目录；Renew 可复活已完成工作 | `StagingLeaseStore` 在既有 gate 内读取并核对活动持久 lease，采用其真实 root；缺失的已完成记录不再拥有路径，重做 Complete 幂等、Renew 拒绝；正常续期后的原 handle 仍可清理 |
| PDF 已渲染后，可选缓存的 InvalidDataException 仍在显示对话框前逃逸 | `QuickPreviewService.CacheSuccessfulAsync` 隔离缓存路径拒绝，保留渲染结果；取消不被吞掉 |
| 老搜索底层 IO 延迟失败，可在新导航/查询成功后覆盖其状态为 SearchUnavailable | 将原 debounce 方法归入既有 Navigation partial；错误提示在 `ReloadProjectionAsync` 捕获的查询代次、页面/内容身份与取消边界内发布；当前查询失败仍显示原提示，220ms 防抖不变 |
| 内联文本先分配全部 UTF-8 副本，才发现超过预览限额；直接调用也未先处理取消 | `TextPreviewProvider` 在分配前检查取消、明显超限字符数与实际 UTF-8 字节数；外部文件的流式读取限额、输出格式和现有阈值不变 |

生产项目依赖图保持 App → Core/Infrastructure/MusicBackground、Infrastructure → Core，
Core 无项目或 NuGet 依赖；没有新增生产层、schema、公开服务 API、包依赖、后台任务或
用户配置要求。UI、动画/材质、交互入口、默认值、语言资源和 AI/模型/CUDA/provider
机制均保持。搜索只修正过时错误的归属，不改变正常结果、导航或分页行为。

独立复审检查了限速同值更新、HTTP 响应移交、暂存持久所有权、取消准入、渲染后缓存与
搜索成功/失败共用代次边界，未发现阻塞回归。ARCHITECTURE.md 记录了这些所有权约束。
本轮没有以类行数为由扩大重构；仅把 debounce 与其投影所有者放进同一既有 partial。
消除重复额度/唤醒和拒绝前的大分配有源码依据，没有整体性能基准或量化收益声明。

## 实际验证账本

| 执行 | 实际结果 | 证据与范围 |
| --- | --- | --- |
| 相同限速重应用的修复前检查 | 1 次预期失败 | `round3-bandwidth-baseline.trx`；冻结时间后仍凭空补发额度，非等待时序猜测 |
| 基础设施主过滤组 | 14 通过，0 失败/跳过 | `round3-infrastructure.trx`；限速 3、响应所有权 4、模块取消 1、租约 6 |
| 内联文本准入过滤 | 3 通过，0 失败/跳过 | `round3-text-preview.trx`；中文 UTF-8 1023/1026 字节边界与预取消 |
| PDF 渲染后缓存修复前 probe | 1 次预期失败，exit 134 | 缓存路径拒绝从 CacheSuccessfulAsync 逃逸 |
| PDF 修复后 probe `all` | 2 场景通过 | 缓存拒绝不阻断、页面取消继续传播；链接实际服务，不渲染 PDF 或启动 GUI |
| 搜索修复前 probe `--search-only` | 1 通过、2 次预期失败 | 当前错误提示通过；旧导航/取消查询的迟到错误污染新状态；`search-feedback-before.json` |
| 搜索修复后同组 | 3 场景通过 | 最新状态保持，当前失败反馈保留；`search-feedback-after.json` |

累计 **27 次用例/场景执行：23 次通过、4 次预期基线失败**。最终 **17 个不同的
Infrastructure 用例和 5 个托管 App 场景均通过**。基线中通过的当前搜索错误场景不算成
第二个不同用例。没有未解决的非预期失败，未把未执行的 Windows 行为计为通过。
本轮全部原始 TRX/搜索 JSON 位于 `artifacts/beta6-round3-validation/`。

主过滤组：

```text
FullyQualifiedName~DownloadBandwidthSettingsTests
|FullyQualifiedName~DownloadRequestPolicyOwnershipTests
|FullyQualifiedName~ModuleWorkerAdmissionTests
|FullyQualifiedName~StagingLeaseCustodyTests
|FullyQualifiedName~CompleteDeletesRootBeforeLeaseRecord
|FullyQualifiedName~ALeaseFromAnAbandonedOwnerIsRecoveredWithoutTouchingExternalSentinel
```

第二组：`FullyQualifiedName~TextPreviewAdmissionTests`。两组均使用
`dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj
-c Release --no-restore --filter ...`，实际编译 Core、Infrastructure、ResidentWorkerFixture
和整个测试程序集，但只执行过滤的用例。

App 命令：

```sh
dotnet run --project scripts/rendered-preview-cache-probe -c Release -- all
dotnet run --project scripts/navigation-projection-probe -c Release -- --search-only
```

前者以保存源码参数重现一次缓存基线；后者扩展已有源码链接 probe，仅运行新加的三个
场景，不执行默认五场景或 ownership 组。fixture 保留实际 220ms debounce、生产查询构造
和调度，以受控完成源模拟已开始且忽略取消的 IO。真实 SearchText setter/绑定、WinUI
dispatcher、SQLite IO、焦点与页面控件仍不在 probe 内。针对旧提取文件的兼容性调整后
额外编译成功、0 警告/错误，没有重复功能场景。

## 完整性检查与复用边界

- `node scripts/test-release-metadata.mjs` 通过，Beta6 唯一目标、Beta5 唯一直接基线。
- MSBuild 当前读取：release `1.1.0-beta.6`，文件/包版本 `1.1.0.6`。
- 生产引用图、34 个前两轮文件哈希、`git diff --check`、新增文件空白和项目 XML 检查通过。
- `node scripts/passive-test-budget.mjs audit` 通过，12 个 workflow 分类/范围约束保持，
  未触发 CI 或扩大全套自动回归。
- 本轮未改变网站/Worker/语言资源，复用第二轮网站构建/Node 验证与第一轮十语言 App
  完整性结果；没有为了当前账本重新计入或重复执行它们。
- 未运行完整套件、模型推理、大文件压力、原生诊断、安装包产出、部署或发布。

## 目标 Windows 与发布前待验

1. 完整 App/XAML、MusicBackground、PRI、Portable/Installer/MSIX 编译及 Beta5 → Beta6
   实际安装升级；当前环境没有 Windows 原生工具链和桌面。
2. 真实模块激活时取消，确认 worker/job 完整退场；在途取消不仅限于预取消场景。
3. 虚拟文件、分享、传输的清理/取消，目录复用、文件锁及崩溃重启后的暂存恢复；新测试
   只证明受测持久 lease 及原文件哨兵保护，不是 Windows ACL/锁文件验收。
4. 真正下载与并发设置更新下的限速、改变速率/解除限速、连接释放及失败重试；受控 HTTP
   handler 测试不代表部署服务器和长时间吞吐结果。
5. 实际 PDF 渲染和预览对话框，损坏/拒绝缓存后的显示与 native 资源退场；搜索快速输入、
   切页、取消及失败后的反馈、键盘焦点/绑定、旧行和分页行为。
6. 继续完成前两轮列出的 UNC、两设备 DropLink、真实浏览器/部署 Worker、多屏/DPI、
   Light/Dark/Liquid Glass、剪贴板、拖放、音乐/DLC/快捷键及无障碍回归。
7. 最终 exact-candidate Windows 包、签名/哈希、模型授权、真实下载及更新/API 回读门禁。
   当前历史授权仍不能证明本候选获得发布资格。

既有原生堆稳定性调查仍未解决；macOS、长时间运行和真实跨设备验证边界没有因这些
托管检查而消失。当前交付仍是单一 Beta6 开发工作区，不声明全项目无缺陷或原生验收完成。
