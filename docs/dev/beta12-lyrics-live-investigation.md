# Beta12：真实切歌与五个歌词源排查

记录时间：2026-10-05 至 2026-10-06（北京时间）。工作分支 `codex/v0.3.1-beta.12`，基线提交 `3e094dc35999372187bf6ecebe738f8700dc7ad6` 加当前未提交修改。没有打包、推送或发布，也未改动线上 Beta11。

## 已证实的问题与修复

1. **酷狗成功响应被本地误判为失败。** 真实歌词接口返回 `status=200, errcode=200`；旧适配器只接受 `errcode=0`。新增 `KugouResponseStatus`，区分歌曲目录的 `status=1/error_code=0` 与歌词接口的成功码，仍拒绝其他错误码。修复前《Happier》没有歌词；修复后同一查询取得酷狗原生中文译文，34 行原文、32 行译文，候选 ID `38627419`。
2. **酷狗单独返回的录音版本字段被丢弃。** 《Shake It Off (Taylor's Version)》的目录项是 `SongName=Shake It Off`、`Suffix=(Taylor's Version)`。旧代码仅匹配 SongName，严格版本检查反而排除了正确重录版。现在合并来源实际提供的 Suffix 后再评分，不使用查询标题补造版本。原版、伴奏版、翻唱版仍按原规则排除。修复前只有 LRCLIB 英文；修复后酷狗 ID `248893216` 返回 79 行原文、72 行中文译文。
3. **已经取得的译文没有及时刷新界面。** 原有渐进回调只发布第一份原文。网易云客户端播放《STORM II》时，LRCLIB 原文先到，14 ms 后酷狗的 46 行中文已到达服务层，但界面继续等待其他来源约 3 秒。现在在同一匹配和来源优先规则下，出现更好的有效文档就立即发布；串行发布避免竞争导致旧原文覆盖新译文。最终结果、切歌代际及取消检查保留。
4. **播放器分批发布元数据导致重复搜索。** Apple Music 曾先发新歌名加旧时长，1.244 秒后才发正确时长；网易云也会先改时长，再改歌名、歌手和专辑。歌词查询现在普通稳定等待 400 ms；零时长、仅时长变化、沿用旧时长等过渡信息等待 1500 ms。期间更新会取消旧轮次。最新实测 `24356-3/4`、`24356-6/7` 都在发出 HTTP 前被取消，仅完整元数据 `24356-5/8` 访问来源。暂停、继续、进度和封面变化本身不发起歌词查询；首次加载、切歌、明确刷新或相关设置变化仍可查询。不能把任意歌手/版本修正都永久忽略，否则会缓存错误录音。
5. **搜索词被播放器展示字符串污染。** Apple Music 的 Artist 可能是完整的“艺人 — 专辑”，AlbumTitle 为空。搜索优先使用已有的完整艺人署名候选，保留原始署名作为后备，不删除合作歌手；LRCLIB 精确查询和 AMLL 窄查询同时使用该规则。
6. **QQ 旧搜索接口实测持续 HTTP 500。** 搜索改接当前官网使用的 `music.search.SearchCgiService/DoSearchForQQMusicDesktop` 请求及批量响应结构；解析完整歌手、专辑、歌曲 mid、原文和 trans。根业务码、子请求业务码及空列表异常标记分别检查，拒绝/异常不记为“没有这首歌”。**这项协议适配不等于 QQ 实机搜索已经通过**，剩余限制见下文。

## 网易云侧结论

确定发生过搜索接口限制，不能解释成“歌曲没有中文译文”。`/api/search/get/web` 多次 HTTP 200，但业务码 405，消息“操作频繁，请稍候再试”。同一查询经系统代理与直连都返回该结果；现有证据不能确定限额针对 IP、查询、设备还是其他访问条件，也不能证明所有 405 都由本轮快速切歌造成。

应用侧已经减少无效请求：分批元数据合并；网易云请求共享至少 500 ms 的发送间隔；405/429 后该接口类别冷却 30 秒；已验证目录缓存复用；搜索冷却不阻断已知歌曲 ID 的歌词读取；继续允许其他已选歌词源回退。没有更换代理绕过限制、无限重试或抓取客户端登录信息。

不是网易云全站不可用。本轮真实播放取得了《酸橙色信笺》《SEVENTH HEAVEN》等原生中文译文。最后在 **2026-10-06 00:55:24**，同一《Shake It Off (Taylor's Version)》搜索和歌词接口均 HTTP 200/业务码 200，严格选中网易云 ID **2092285062**（Taylor Swift，1989 (Taylor's Version)，219.209 秒）。`lrc`、`tlyric` 都存在，解析为 73 行、71 行中文译文，进入界面并成功写入缓存；原版、卡拉 OK 和其他艺人的候选被排除。正常重启后仍显示该译文。

网易云客户端显示《STORM II》的中文译文也已截图，但当时 DropSpace 搜索被限制，未取得该录音的网易云歌曲 ID。因此不能据此断言该曲的歌词接口缺少 tlyric；DropSpace 当时显示的是酷狗原生译文。

## 五个来源的实际结果

| 来源 | 本轮实际证据 | 边界 |
| --- | --- | --- |
| 网易云 | 多首中文原文及中文译文到达界面；Taylor 重录版 ID 2092285062，73/71 行 | 搜索间歇返回 405，具体限制维度未确定；冷却与回退不能保证服务器放行 |
| QQ 音乐 | 旧搜索 HTTP 500；新结构得到根/子码 0、`meta.is_filter=-2`、空列表；另一请求形式返回 2001、`is_filter=-12` 及登录反馈地址 | **未取得真实成功搜索，不能标为已修复通过。** 当前官网搜索代码会在未登录时先打开登录框，传输还使用签名/加密；本应用匿名直连尚不具备完整的已授权会话接入。不能仅靠更换字段宣称解决，也未抓取其他应用凭据 |
| 酷狗 | Happier 34/32、STORM II 50/46、Taylor 重录版 79/72，均原生译文 | 已修成功码与版本后缀两项实际错误；没有放宽同录音、时长及版本检查 |
| AMLL | 网易云客户端播放《Avid》时，AMLL ID 5742574828001667 提供 38/38 行并到达界面 | 同一 STORM II 单独查询返回正常空结果；不能把无收录视为解析故障 |
| LRCLIB | STORM II ID 33723520 返回 40 行原文；其他歌曲也取得原文 | 较早出现过 HTTP 503，随后恢复；这些响应没有中文译文字段时继续向允许的译文来源查询 |

QQ 核对依据为本次下载的官方 [搜索页面](https://y.qq.com/n/ryqq_v2/search?w=Happier)及其 `search.chunk.4acd51132e8e6b1e22cd.js`、`common.chunk.ddca9cfefd825f4a733d.js`、vendor 资源。AMLL 对照其[原生 HTTP API](https://amll.dev/reference/http-api/native)。官网登录要求是源码证据；尚未证明 `is_filter=-2` 的唯一成因就是缺少登录。

## 实机范围与复核

使用真实 Windows 媒体会话、App 的歌词服务和显示链。严格快速切歌通过仅 Debug、显式诊断开关启用的 `LyricsRapidSkipDiagnostic` 调用 App 自身 `SkipNextAsync`，每 2.5 秒切一首；不另造模拟播放器。记录完整标题、署名、专辑、时长、来源、请求代际和结果计数。App 为系统语言（实际 zh-CN），首选网易云、无指定备用、搜索其余来源开启、附加歌词开启；排查平台译文时临时关闭 AI。

- Apple Music 第一轮 20 首快速切歌：Blueberry Eyes、花海、Hands in the Fire、爱人错过、oh yeah?、Way Less Sad、this is what winter feels like、PS5、Nothing Else Matters、Lost、Midnight Sun (Girls Trip)、Happier、This is My Time、Dangerous、热恋情节、WOAH、还在流浪、Loser、Psycho Pt. 2、BACKSEAT。另有之前的慢速观察，不能把慢速轮次称作 2.5 秒测试。
- Apple Music 修复首项后再切 20 首：Zoo、Small Worlds、Done For Me、TWILIGHT!!!、Counting Stars、If We Ever Broke Up、Home（Charlie Puth / 宇多田光）、Mother、GDFR、你看到了、Pay That Toll、Only Human、那天下雨了、Waiting For Love、Color Your Night、雨爱、Juice、走马、The Cure、Saving Grace。19 首在离开前出现原文预览，其中 12 首出现译文；中文原文无需再次译成中文。Home 在 2.5 秒内未取得结果，不据此认定没有歌词。
- 网易云音乐连续 20 首：刀子、酸橙色信笺、不再错过的盛夏、this is what heartbreak feels like、SEVENTH HEAVEN、Tell Your World × 唯有追赶风的方向、Flamingo、Notion、グランドエスケープ (Movie edit)、Babydoll、Kiss Me Kill Me、Ticking Away、Sunroof、Moonlit Dream、Stars、XƎTЯOVerthink、SOS、Avid、Enemy（Arcane 版本）、STORM II。Grand Escape、Enemy 在停留窗口内未出现预览，不声称全部歌曲均通过。
- Happier：修复前后同曲对照；Happier → The Way You Felt → Happier 成功恢复 32 行中文；明确刷新绕过该次响应缓存仍返回同一酷狗译文。
- Taylor 重录版：修复前后同录音对照；Taylor → STORM II → Taylor（最后一次为自动播放下一首）恢复 72 行酷狗译文；后续首选网易云恢复后显示 71 行网易云译文。两种来源的行数不同不代表译文丢失。
- 2026-10-06 00:53:21–00:54:28：暂停 → 继续 → 进度推进 → 暂停，新增查询及 HTTP 结果/失败计数均为 **0**。
- 结束时两个播放器均暂停；AI 开关、光效及所有 Lyrics 字段与排查前保存值一致。正常关闭/重启当前开发版后，AI 仍开启、原生译文仍显示。没有清空模型、歌词缓存、资料库或歌单。

## 文件、诊断与验证

主要代码：`MediaExperienceService.cs`、`LyricsMatcher.cs`、`LyricsService.cs`、`LyricsHttpClient.cs`、五个 `*LyricsProvider.cs`、`KugouResponseStatus.cs`、`NetEaseResponseCache.cs`。新增本地诊断 `LyricsRequestTrace.cs`，将搜索、候选、HTTP/业务码、字段存在性、解析行数、缓存与界面串联。只有显式指定本地诊断路径才记录歌曲元数据，普通启动关闭；不记录歌词正文、敏感请求头、URL 查询参数或凭据，单会话最多 8 MiB。快速切歌工具仅在 Debug 且显式设置开关时执行。

最后 WinUI 开发编译：`build-verified.log`，**0 警告、0 错误**，30.56 秒。为每项实际失败修复进行了必要增量编译，未打包。5 个针对本次原因的小型检查通过（4+1），覆盖译文及时发布、网易云分类冷却、QQ 新响应及异常空结果、酷狗版本后缀与成功码；不是完整回归，也不是 QQ 真实联网成功证明。首次 `--no-restore` 检查因测试依赖缺 assets 失败，恢复该依赖后针对性检查通过。`git diff --check` 无错误。

最终正常运行产物：`.codex/beta12-lyrics-live-20261005/dev-verified/DropSpace.exe`；对应 `DropSpace.dll` SHA256 `2625C4D6123606FED8D6EDDF999B56770520BEF8E51F0110B0E1798D853CEB4E`。以当前未提交工作树编译，不能仅用基线提交代表全部修复。最后启动关闭了诊断、自动切歌和逐源检查开关。

本次原生歌词排查不以 CUDA 是否成功代替平台结果。CUDA 的实际 GPU 翻译证据见 [运行时修复记录](beta12-runtime-fixes.md)；本轮没有扩大模型推理测试。

本机证据目录：`.codex/beta12-lyrics-live-20261005/`，完整曲目版本/时长及请求 ID 在 `trace-before.jsonl`、`trace-after.jsonl`、`trace-after-rapid.jsonl`、`trace-netease.jsonl`、`trace-final.jsonl`、`trace-verified.jsonl`；计数摘要见 `playback-evidence-summary.json`。原始证据留在本机，没有上传。

- [Happier 修复前](../../.codex/beta12-lyrics-live-20261005/happier-drop-before.png) / [修复后](../../.codex/beta12-lyrics-live-20261005/happier-drop-after.png) / [返回后](../../.codex/beta12-lyrics-live-20261005/happier-after-aba.png)
- [Taylor 重录版修复前](../../.codex/beta12-lyrics-live-20261005/taylor-before-suffix-fix.png) / [修复后](../../.codex/beta12-lyrics-live-20261005/taylor-after-suffix-fix.png) / [返回后](../../.codex/beta12-lyrics-live-20261005/taylor-after-aba.png)
- [网易云客户端 STORM II](../../.codex/beta12-lyrics-live-20261005/netease-storm-native.png) / [DropSpace 的酷狗译文](../../.codex/beta12-lyrics-live-20261005/netease-storm-drop-final.png)
- [正常重启、原生译文及 AI 偏好保留](../../.codex/beta12-lyrics-live-20261005/final-restart-ai-restored.png)

仍未确定：网易云具体限流维度和恢复条件；QQ 在完整授权访问条件下的实际成功搜索；快速经过但未等到结果的个别歌曲是否有匹配译文。本次不能声称“五个来源所有歌曲零 bug”，也没有把未完成的 QQ 实机验证标成通过。
