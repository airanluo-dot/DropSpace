# Round 3 — user-requested lyrics empty-state wording

This is a newly authorized Beta 18 product-copy change, not a repeated review finding. The user requested that the Music page lyrics card explicitly say **“无匹配歌词”** when no matching lyrics are found, with English **“No matching lyrics”**, while actual loading/network failures retain the refresh-and-retry message.

## Change

Changed only the existing `LyricsNotFound` resource value in `src/DropSpace.App/Strings/zh-CN/Resources.resw:517` and `src/DropSpace.App/Strings/en-US/Resources.resw:517`. No resource key, provider rule, matching rule, state transition or UI layout was added. The existing `LyricsFailed` strings remain unchanged: Chinese “歌词暂时无法加载，请点击刷新重试。” and English “Lyrics are temporarily unavailable. Select Refresh to try again.”

The current mapping already distinguishes those outcomes, so no ViewModel or MusicPage change is necessary. The UI theme owner completed and released its separate MusicPage/DesignTokens edits before this task; this change does not touch those files.

## Source trace

- `Core/Lyrics/LyricsModels.cs:26–39` defines separate `NotFound` and `Failed` query outcomes.
- `Infrastructure/Lyrics/LyricsService.cs:432–449` validates provider documents and distinguishes a completed no-match from `RequestFailed`/transport failure. At `212–215`, a usable result is Found; an empty result with provider failure is Failed; an empty completed search without failure is NotFound. Exceptions/timeouts stay Failed (`217–220`). Candidate identity or score rejection returns an empty document (`655–667`) without changing it into a transport failure.
- Empty bodies avoid native language prediction (`FastTextLanguageIdentifier.cs:55–57`), so preparing an empty no-match document does not invoke a language-model worker.
- `App/Services/Media/MediaExperienceService.cs:558,581` keeps the source result/status and publishes it to the media ViewModel. Its failure recovery retains the source status if the source stage already succeeded (`655`); translation failure does not automatically overwrite a completed source result.
- `App/ViewModels/MediaViewModel.cs:238–245` maps NotFound to `LyricsNotFound` and Failed to `LyricsFailed`. No song or disabled lyrics suppresses status text as before.
- `App/Views/Music/MusicPage.cs:352–358` renders that localized status in the lyrics card and independently hides the lyric scroll when no rows exist. It does not relabel every empty body as a network error.

Thus completed no-match outcomes now display the exact requested wording, and failed/incomplete loading remains retryable. A failed search is not falsely claimed to have conclusively found no match; mixed provider failures keep their existing Failed semantics.

## Static verification and limits

Both resource XML documents parsed successfully. Direct resource reads confirmed the exact new NotFound values and unchanged Failed values. The inspected diff contains exactly one changed resource value in each locale. `git diff --check` completed without errors. **Executed cases: 0.** No tests, new tests, fixtures, probes, build, application/native execution, commit or remote mutation. This source/copy check does not claim a real provider or native Windows UI run.

## Classification follow-up

The downstream ViewModel mapping reviewed above is correct, but subsequent source tracing confirmed that provider aggregation can discard a completed no-match when another provider fails. The initial copy-only update was therefore insufficient to address that mixed-result classification. [R3-LYR-01](round3-lyrics-state-classification.md) records the completed-provider evidence fix, consistent document quality, retained all-provider-failure/retry behavior and the two selected regression rows. No screenshot-specific provider/network sequence is claimed.
