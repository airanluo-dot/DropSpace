# 同时记录 Apple Music 和 DropSpace 的播放进度

在 Windows 上打开 Apple Music、播放一首歌，再打开 DropSpace 的音乐页。在仓库根目录用 .NET 10 SDK 运行以下命令，记录 60 秒后自动结束。

```powershell
dotnet run -c Release --project scripts/diagnostics/AppleMusicTimeline -- .\artifacts\music-timeline.jsonl 60
```

如果同时运行了日常版和预览版，用 `Get-Process DropSpace | Select-Object Id,Path` 选定进程，再在秒数后填写它的 PID。每次指定一个新的文件名。录制期间可以切歌、暂停、恢复或在播放器里拖动。

需要复查逐词画面时，展开 Apple Music 的歌词栏和 DropSpace 的独立音乐视窗，保持两个窗口展开，追加 `--frames`。工具只把两个歌词区域的裁图写到同名的 `.frames` 目录。默认只写时间线和按歌曲汇总的 `.summary.json`。样本包含本机播放的歌曲信息和音乐页正在显示的歌词。[对照截图的分析命令](../AppleMusicLyricComparison/README.md)

| JSONL 字段 | 用途 |
| --- | --- |
| `started`、`finished` | 一次跨进程读取的起止时刻。跳秒、拖动和切歌时，两次读取之间可能刚好发生变化。 |
| `native`、`nativeTimestamp`、`start`、`end`、`state`、`rate` | Windows 媒体会话发布的位置、发布时间、范围、播放状态及速率。 |
| `appleSeconds`、`dropSeconds` | 两个窗口的实际滑块值，单位为秒。Apple Music 在本次实测中提供整秒，DropSpace 提供小数。 |
| `lyric`、`status` | DropSpace 音乐页显示的原文和状态。界面控件不可用时留空。 |

汇总只比较两个滑块都有值且读取不超过 250 毫秒的样本。出现不同秒时，还会统计 Windows 原始整秒与哪边相同。两侧显示同一整秒时，仍可能存在词级差异。要判定 DropSpace 自己是否画晚，须另外核对它实际选中的歌词文档、该词的起止时间、用户歌词偏移和窗口画面。若要判断是否与人声同步，还需要与该录音对应的音频证据。

如需精确定位一次查词，启动待测 DropSpace 进程前，将该进程的环境变量 `DROPSPACE_LYRICS_TRACE` 设为一个新的 JSONL 文件的绝对路径。查找与截图时间接近的 `ui-source`，可取得实际采用的提供方、候选 ID 和行级时间。首屏预览另记为 `ui-preview`。按 ID 取对应文档的原始词时标，可以区分检索时选错录音、解析时改动时标和绘制时落后。[请求追踪实现](../../../src/DropSpace.Infrastructure/Lyrics/LyricsRequestTrace.cs)
