# Beta12 QQ Music sign-in

## Scope

The music lyrics settings now contain a compact QQ Music card. Signed out: “登录后可获取 QQ 音乐歌词”; signed in: “已登录”. Only necessary expired/connection/storage messages are shown; Retry appears only after rejection. A themed WinUI WebView2 window opens QQ Music's official website. After authentication it reveals **Save**, and closes automatically after successful persistence. Connection verification runs in the background; candidate/translation details are diagnostic data, not ordinary UI copy. The app does not collect passwords, inject login scripts or import credentials from other browsers/clients.

`QqMusicLoginService` passes selected music session cookies to `QqMusicSession`. The latter encrypts its atomic local store using Windows DPAPI CurrentUser under the existing data directory (`QqMusic/session.bin`). The WebView is InPrivate and closes with the window. Other account cookies and password/autofill storage are not retained by this feature.

The production `LyricsHttpClient` sends these credentials only to HTTPS `u.y.qq.com` and `c.y.qq.com`, respecting cookie domain scope. Automatic cookie handling is disabled. The existing `QqMusicLyricsProvider` supplies the session identity and request token to its search and lyric requests; other providers do not receive the session. Sign-in/sign-out invalidates pending lyric work, while generation checks prevent late responses from resurrecting a signed-out session.

## Lifetime and failure behavior

Saved, connected, expired, rejected and storage-error states remain distinct internally. “Signed in” describes the saved session, not a promise of matching every song. The background connection check uses the app's actual lyrics service and QQ adapter, with other sources and AI excluded. Verification failure does not pretend that persistence failed.

Restart restores the encrypted session. Server-issued cookie renewal, expiry and deletion are respected; no lifetime is invented or extended. This is persistent sign-in, not a guarantee of permanent authentication or a replacement for provider access rules. Expired/rejected sessions stop repeated automatic requests; explicit check or sign-in allows another attempt. Sign-out removes the local encrypted session and closes the login browser; it does not claim to revoke every QQ session on other devices.

The anonymous request previously returned an empty filtered response (`meta.is_filter=-2`); that did not prove absence of matching songs or translations. With the user's explicitly saved login, search now returns actual candidates. This establishes authenticated success on this machine, not the provider's complete anonymous-access policy.

Live investigation also found that the legacy lyric endpoint returned original-only lyrics for Happier and then `-1901` for another candidate. QQ's current official website uses `music.musichallSong.PlayLyricInfo/GetPlayLyricInfo`; the adapter now follows that request shape, asks for translation, checks outer/nested status, decodes bounded base64/UTF-8 and preserves timeline alignment. QQ's `//` translation placeholders are removed. Parser revision 2 refreshes old QQ entries without deleting other caches. No retry or alternative endpoint bypass is attempted after server rejection.

## Validation, 2026-10-06

- Final WinUI Debug compilation: successful, zero warnings/errors; `.codex/beta12-qq-login/build-simple.log`.
- Four focused Windows checks passed: encrypted restart persistence and credential destination/domain restrictions; expiry/sign-out and late-response isolation; actual adapter/transport with encoded native translation; next candidate after empty lyrics. The changed placeholder case was rerun alone and passed. Logs: `checks-lyrics.log`, `check-placeholder.log` in the same evidence directory.
- User completed official login. Subsequent app restart restored it without another QR scan. Apple Music sample: **Happier — Ed Sheeran — ÷ (Deluxe)**, media duration 207 s. QQ song MID `001kyWoz1JdiDQ`; search returned 20 candidates, `is_filter=0`, HTTP 200/code 0. The current lyric API returned Chinese translations where the old endpoint did not. Trace `live-modern-lyrics.jsonl` request `45944-3` is the QQ-only check; `45944-4` is the normal UI pipeline with QQ temporarily preferred, ending at `ui-source` with provider translations. No AI fallback supplied this result.
- The earlier Saving Grace/KIRBY check completed without a same-recording match; no claim that its QQ catalogue has no lyrics. QQ authentication does not guarantee every song exists or passes recording validation.
- Screenshots `qq-native-check.png` and `happier-qq-native-display.png` record the real success before the requested UI simplification. The latter exposed `//` placeholders, fixed afterward. Final build identity is in `running-delivery.json` (DLL SHA256 `B52CEF4E375371887189A1B2A352673D7701B022180EFC5DACB21F19AFC46E44`).
- Final revision 2 was also exercised live after restart: `live-delivery.jsonl`, request `44552-2`, has 34 original lines and 32 valid provider translations, reaching `ui-source`. `happier-final-display.png` shows no placeholder rows; `login-card-final.png` shows the simplified signed-in card. All temporary lyrics-setting changes were restored (zero changed fields, `settings-restored.json`), test playback was paused, and the same final binary was relaunched without opt-in diagnostic tracing (`running-final.json`).
- Long-term expiry/renewal over days and other QQ accounts are not live-tested. No claim of permanent login. The final simplified first-login/save flow has not required another user authentication; its code is compiled, while real authentication/persistence was verified through the earlier UI.
- Existing Beta12 downloader, CUDA and lyrics changes retained. No packaging, publishing, model downloads or broad regression run.
