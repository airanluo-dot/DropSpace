# Beta 28 media interoperability and Music UI audit

Date: 2026-09-22. Target: `v0.3.0-beta.28`. Scope: generic lyric discovery and
identity evidence, late media metadata, offline NetEase enhancement inspection, Music-page
hierarchy and real Apple Music/DropSpace desktop acceptance. The user authorized publication.

## Implemented behavior

- Track artist and album artist are alternative publisher credits. All five online providers
  use the same bounded normalized title/artist search terms and can recover with title-only
  discovery before the shared identity validator accepts a candidate.
- Provider order is preferred, optional backup, then the remaining unselected providers only
  when enabled. The remaining-provider switch is independent of backup selection, and the full
  strategy participates in cache identity.
- A same-track media session that initially lacks duration retries only when duration becomes
  available and the earlier lookup did not find lyrics. Wrong artists, conflicting versions,
  incompatible known durations, missing provider IDs and stale track identities still fail.
- Passive NetEase inspection never contacts an update source. A committed managed receipt is
  checked against both installed file hashes; independent BetterNCM/InfLink files are described
  as installed without claiming that a live capability test just ran.
- Now Playing, lyrics and enhancement use consistent WinUI card surfaces. The bounded lyric
  viewport consumes wheel input without moving the outer page, media source text is part of playback
  metadata, and an installed enhancement card moves below the normal Music content.

## Executed evidence

- The final solution test run completed with 558 passing tests and two explicitly gated native
  tests skipped: Core 245/245, Infrastructure 188 passed plus one skipped, and App 125 passed
  plus one skipped. The focused source-strategy suite completed 13/13.
- The network-enabled provider-strategy smoke test contacted the real provider transports and
  passed all four request-order scenarios in 11.6 seconds: preferred only, preferred then backup,
  preferred then remaining sources, and preferred/backup before remaining sources.
- Apple Music 1.1540.23042.0 was opened and played real catalogue tracks. DropSpace observed
  `Together Forever` with the display-ready publisher credit
  `Rick Astley — Whenever You Need Somebody`; the native media-session test then resolved lyrics
  through the generic pipeline. In the final desktop UI, lyrics reappeared after switching from
  preferred-only to a QQ Music backup, enabling remaining-source search, removing the backup while
  keeping remaining-source search enabled, and restoring preferred-only mode.
- The desktop wheel test started with outer page offset 322 and lyric offset 0. Scrolling inside
  the lyric card moved only the lyric offset to 305; scrolling outside moved only the page offset
  to 612. At lyric offset 2297.6 (the bottom boundary), another wheel-down left the page offset at
  612, confirming that the safe area does not leak wheel input to the page.
- The installed NetEase enhancement was detected without requesting an update and rendered after
  the normal Music settings and application list with `✓ 增强组件已安装`.
- Version, repository consistency, localization (597 synchronized keys, 237 imperative
  references and 181 XAML IDs), hardcoding and Windows-compatibility checks passed. The final
  Release solution build completed with zero errors; the test-resource packaging step emitted
  its two known PRI default-language/neutral-resource warnings.
- The portable executable reports file version 0.3.0.28 and product version 0.3.0-beta.28. It is
  261,579,623 bytes with SHA-256
  `84DA0FC02EC08D02174B82E0EDA6572BF0041BA920C18458518F8CEE152844FB`.

## Boundaries

- Provider catalogues and Windows media publishers can change after release; bounded fallback
  and multi-signal validation reduce coupling but do not make third-party metadata authoritative.
- Local component presence does not by itself prove current runtime capability. Installation
  status and live verification are intentionally distinct.
