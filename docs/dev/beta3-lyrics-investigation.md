# Beta 3 lyrics and music response investigation

## Reproduced matching defects

- `零(《时光代理人 第三季》Part1片尾曲)` was compared with the complete decoration against the one-character catalogue title `零`. The old score rejected it before considering the matching artist/duration.
- `风的来信(feat.孙晔)[中文版]` contains stacked decorations; the old featured-credit regex also required whitespace after the period.
- The actual QQ catalogue names the Chinese recording `风的来信 A Letter From the Wind`, with HOYO-MiX / 孙晔 and duration 197 seconds. The old length-ratio title comparison rejected it. A narrow bilingual-title recovery now requires an exact Chinese base plus corroborating artist and duration, preserving foreign-language/live/wrong-artist rejection.
- QQ only inspected the top catalogue candidate. An empty lyric response hid subsequent matching candidates. It now inspects at most three distinct candidates.

## Actual-source observations, 2026-10-03 UTC

Public QQ search returned `000xs4VJ3vls2S` (零, 饭卡, 186 seconds) and `002wxhL93EPjZz` (风的来信 A Letter From the Wind, HOYO-MiX / 孙晔, 197 seconds).
The current service completed one live request for the latter with 52 parsed lines. A later live run timed out for both songs at the existing 8-second per-provider deadline. The actual 零 search and lyric responses were separately retrieved; replay through the production service returned the correct identity and 119 parsed lines. This is real-response replay, not a successful uninterrupted live fetch and not a Windows UI test.
The cloud NetEase endpoint returned an opaque result with a regional marker instead of searchable JSON. This cannot establish the user's local network behavior or catalogue absence. No access restriction was bypassed and no source outside the user's configured provider set was added.

## Translation priority and bounded latency

A found original no longer stops the allowed search before considering existing target-language translations. NetEase and QQ expose intermediate originals while checking candidates; the service owns one 1.5-second supplemental budget starting with the first validated original. Switching candidates/providers does not restart it. Timeout retains the original, does not persist an incomplete search as a definitive negative, and does not treat provider failure as proof that AI translation is necessary. Provider cleanup retains ownership even when the UI waiter leaves.

## Music response

Existing dB normalization, square-root brightness and diffusion mappings compressed normal/loud music into a narrow visual range. A glow-only slow local reference restores transient contrast without changing the accepted palette/geometry, inventing pulses for constant input, changing the spectrum meter, or increasing idle/global refresh. Synthetic envelope tests pass; they do not constitute human visual acceptance or GPU/present-frame-rate qualification.
