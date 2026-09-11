# Preview.23 WinIsland parity notes

This document records the clean-room parity decisions used for DropSpace Preview.23. It is a
behavior and geometry reference only; no WinIsland source is copied or translated into DropSpace.

## Reference set

| Reference | Revision | What was reviewed |
| --- | --- | --- |
| [WinIsland](https://github.com/4JX/WinIsland) | `705cb011e8951710052dab854defd63b5a1effbf` | compact/expanded geometry, music controls, lyrics, visualizer, widgets, settings defaults |
| [PowerToys](https://github.com/microsoft/PowerToys) | `6022efc25d5fae9880d48448f804581b0bd9a121` | popup window style, DWM backdrop, fake-active non-client behavior |
| Windows App SDK samples | `0057c23` | backdrop and windowing lifecycle |
| Windows classic samples | `434f6002` | native window/message patterns |
| Files | `aafd9aae` | navigation and recent-item presentation |
| EarTrumpet | `f6d864c` | audio-session lifecycle and device-aware presentation |
| ModernFlyouts | `ecf5708` | transient flyout lifecycle and accessibility conventions |

## Adopted behavior and geometry

| Area | Preview.23 decision |
| --- | --- |
| Compact order | Artwork → current lyric → live spectrum; fixed height, measured dynamic width, clamped to a safe range |
| Compact fallback | Artwork falls back to the source-app icon/placeholder; lyric falls back to title, then source app |
| Compact visualizer | Six damped bars from the shared `SpectrumFrame`; bars stop while paused or hidden |
| Compact interaction | Playing media opens Music; paused/no session opens Temporary Space; an already expanded island does not jump pages |
| Expanded navigation | One HWND and one shell with Files → Music → Widgets; no wraparound; rails are Files/right, Music/both, Widgets/left |
| Files | Three to five recent staged items, empty state, and Open DropSpace action; no oversized blank panel |
| Music | Cover, title/artist/album, spectrum, elapsed/remaining/progress, previous/play-pause/next, and debounced seek |
| Widgets | Real 6×3 visual grid with Clock, Calendar, Resource, and Settings cards; no settings controls in the island |
| Backdrop | Windows 11 DWM owns the outer corner/backdrop; XAML owns only the inner clip. No outer XAML stroke |
| Windowing | Popup/tool-window semantics, no focus stealing, non-client recalculation and frame refresh after DWM attributes |

## Settings mapping

| WinIsland capability | DropSpace Preview.23 mapping |
| --- | --- |
| SMTC/media session | `IslandActivitySettings.EnableMediaActivity` and the Music page diagnostics |
| Lyrics mode/visibility | `LyricsSettings.Mode`, `Enabled`, `ShowLyricsInCompact` |
| Secondary lyrics/provider/delay/scroll | `SecondaryLyrics`, `Provider`, `DelayMilliseconds`, `Scrolling`, `MaxScrollWidth` |
| Media app filter | Music-page allowlist, persisted with the native-island settings |
| Auto hide/hide delay/hidden width | `IslandAppearanceSettings.AutoHide`, `HideDelayMilliseconds`, `HiddenWidth` |
| Right-click drag | `IslandAppearanceSettings.RightClickHoldToMove` |
| Compact/expanded scale | `CompactScale`, `ExpandedScale` |
| Widget layout/compact widget layout | `WidgetSettings.WidgetLayout`, `CompactTime`, `CompactResource` |
| Notification display | `SystemActivitySettings.ShowNotifications` |
| Cover options | `ShowArtwork`, `CompactCoverStyle`, `ExpandedCoverStyle`, `RotateCover` |
| Plugins | **SKIP BY PRODUCT DECISION** |
| Custom fonts | **SKIP BY PRODUCT DECISION** |
| Replace native volume flyout | **SKIP BY PRODUCT DECISION** |
| Liquid Glass | **SKIP BY PRODUCT DECISION** |

## Clean-room implementation boundary

DropSpace re-implements the observed product behavior in C#/WinUI 3 and keeps the reference
projects outside the repository. The shared `SpectrumFrame`, lyrics frame, compact layout
calculator, expanded pager, idle-hide policy, and DWM controller are DropSpace-owned contracts.

