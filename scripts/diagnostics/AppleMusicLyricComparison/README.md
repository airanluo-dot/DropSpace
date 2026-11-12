# 对比 Apple Music 和 DropSpace 的屏幕逐词高亮

在 Windows 上用 [AppleMusicTimeline](../AppleMusicTimeline) 的 `--frames` 录制两个歌词窗口，等采集结束后，在仓库根目录运行下面的命令。需要 .NET 10 SDK、Python 3.11 或更新版本和相应的 Windows OCR 语言包。英文录制用 `en-US`，简体中文录制可用 `zh-Hans-CN`。

```powershell
.\scripts\diagnostics\AppleMusicLyricComparison\Compare-AppleMusicLyrics.ps1 -Timeline .\artifacts\timeline.jsonl -OcrLanguage en-US
```

程序读取 `timeline.jsonl`、相邻的 `timeline.jsonl.frames/pairs.jsonl` 及清单中列出的裁图。默认重算并写入 `timeline.jsonl.comparison`。可通过 `-OutputDirectory` 指定目录。原始图片、时间线和 OCR 输出可能含有本机播放的歌词，复验数据应留在本地。

## 解读输出

- `summary.json` 统计有图、可关联播放状态的样本，以及各种词边界的数量。
- `pairs.csv` 逐对列出图片、两边识别的活动行及达到半亮的词序号，从 1 开始。`appleInk` 和 `dropInk` 给出有观测的词序号及其亮起比例，缺少的词序号表示 OCR 未能判读。缺图、当前行无法确认和两边不同句均保留。
- `crossings.csv` 将同一曲段、同一句的同一词在两边分别夹出时间区间。列 `leadMin`、`leadMax` 是 DropSpace 相对 Apple 屏幕高亮的领先区间，以秒计。两端均为正值时 DropSpace 先亮，均为负值时 DropSpace 后亮。区间含零时，当次采样分辨率不能区分先后。`applePre/Post` 和 `dropPre/Post` 是各自窗口在边界附近的播放条读数。
- `pixels.jsonl` 保存 Windows OCR 的文字、字框位置和前景像素列分类，便于回看识别是否正确。使用 CSV 中的文件名可定位原始帧。

逐词边界定义为同一词的可见笔画列至少一半越过亮度阈值。前一帧低于一半、后一帧达到一半才构成观测，时间范围覆盖两次截图各自的起止时刻。检测使用 DropSpace 较亮的前景墨迹与 Apple 较柔和的前景墨迹，适用于两个窗口均展开歌词且对比清晰的录制。OCR 收录连续采样中出现的歌词行，简繁、常见缩写和分词差异会先做归一化。重复行、和声、逐字动画及字体颜色可能影响活动行或词的判读，具体异常须核看原图。

切歌、暂停和跳播处分段。缺少对侧边界、同一词有多个可能对应或相邻观测相距超过 1.35 秒时，在 `evidence` 列标出原因。只有一个画面不能测出固定延迟。两个窗口的亮起时间和播放条读数均属于视觉观测，判断真实发声时间或歌词源打点还需对应证据。
