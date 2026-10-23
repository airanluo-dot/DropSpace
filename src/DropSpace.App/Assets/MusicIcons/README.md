# Music action symbols

The lyric and translation actions use Microsoft's Fluent UI System Icons at commit
`463d58b8186f874ab2f51cf0a08b4ff5097c912f`:

- `Lyrics.svg`: `assets/Chat/SVG/ic_fluent_chat_20_regular.svg`
- `Translation.svg`: `assets/Translate/SVG/ic_fluent_translate_20_regular.svg`

Source: https://github.com/microsoft/fluentui-system-icons

`Views/Music/MusicWindowGlyphs.cs` retains the path data. The native view renders it
in the original 20-unit canvas, with white foreground and independent 44-DIP hit
targets. No font or online asset download is required at runtime. The original SVGs
are retained here for source review. The MIT license is included in the portable
bundle as `Licenses/fluent-system-icons-MIT.txt`.
