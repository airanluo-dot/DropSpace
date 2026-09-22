# Beta 27 lyrics and NetEase compatibility audit

Date: 2026-09-22. Target: `v0.3.0-beta.27`. Scope: lyric identity/matching,
NetEase provider transport/schema recovery, duplicate SMTC selection, enhancement live-progress
verification, repeated track-switch recovery, smooth release-only seeking, the durable project
Skill refresh and one fresh repository scan. The user authorized Beta 27 publication.

## Implemented behavior

- Title is still required. Featured-artist, remaster/reissue and player suffix decorations are
  normalized; small edit-distance differences require strong independent evidence. Live,
  remix, acoustic, instrumental, karaoke, demo, cover, sped-up and slowed conflicts still fail.
- Artist credits recognize semicolon/comma/CJK separators, spaced slash, feat./with/x and CJK
  slash forms. A primary list may be a complete-credit subset, but partial conflicts and
  substring impersonation such as `AC` versus `AC/DC` fail.
- Album is soft when artist identity is strong. Known durations allow 20–40 seconds depending
  on track length; a difference outside that bound remains a hard rejection. A provider ID is
  still mandatory and request metadata is never copied into a candidate.
- NetEase tries title+artist then title-only, returns at most 30 search rows per response and
  fetches lyrics for at most three distinct validated IDs. Legacy `artists/album/duration` and
  current `ar/al/dt` fields plus provider title aliases are accepted. Empty candidates are
  skipped and unusable YRC can fall back to independently returned LRC. A non-success
  application code inside an HTTP 200 response is a provider failure, not a catalogue miss.
- Same-source duplicate media sessions use normalized titles and credit tokens. Event-observed,
  plausibly advancing playback proves live progress even with stable LastUpdatedTime; a frozen
  position remains rejected.
- NetEase weak/native and InfLink rich/plugin sessions remain observed as a bounded same-source
  candidate set while their track metadata changes out of order. When the rich renderer catches
  up, its event triggers reselection and restores the exact rich control object. A briefly empty
  preferred renderer retains the still-live selected session instead of publishing an empty,
  uncontrollable transition.
- Progress dragging is local preview state. Playback-clock frames cannot overwrite the thumb,
  no native seek is sent while dragging, release submits exactly one final target, and a bounded
  acknowledgement hold prevents the old native position from snapping the thumb backward.

## Executed evidence

- Core lyric regression: 23 passed, 0 failed, 0 skipped.
- Infrastructure lyric regression: 21 passed, 0 failed, 0 skipped.
- App NetEase verifier plus media-selection regression: 39 passed, 0 failed, 0 skipped.
- Media selection now includes a 256-switch alternating-order stress fixture. On every cycle,
  the rich renderer remains in the recovery set while metadata diverges and is selected again
  after both renderers converge. Focused media-selection regression: 13 passed, 0 failed,
  0 skipped.
- A real NetEase 3.1.40 / BetterNCM 1.3.4 / InfLink 3.2.11 session was exercised through the
  current Beta 27 Portable build. DropSpace issued 20 rapid Next commands, 10 rapid
  Next→Previous pairs and another 30 rapid Next commands: 70 control actions total. Every
  observed transition retained Previous, Next and Play/Pause; final DropSpace and NetEase
  titles agreed. Pause and resume still worked after the stress run.
- The final rebuilt Portable UI (`SHA256 BBC6C098FE0D6939BA6CE44863DE29F0A104527703777364F7E97D42BF596FD7`)
  then dragged the live timeline back to about 1:06 while playback remained Playing. The thumb
  stayed at the released target instead of snapping back, and the subsequent native position
  advanced to 1:15 before the session was deliberately paused. The deterministic seek fixture
  additionally applies 500 playback frames during a slow drag, proves all are suppressed, proves
  canceled drags do not seek, and proves only one target is returned on release.
- Focused media/seek regression: 22 passed, 0 failed, 0 skipped; the four seek-interaction
  fixtures cover slow drag, acknowledgement, timeout/cancel and track-change reset.
- Production live-network matrix used the real transport/registry/service. Decorated Sunroof
  returned 50 NetEase lines; full-credit Color Your Night returned 62 NetEase lines; Deep
  Breath Deep Breath -Reload- returned 30 NetEase lines. The NetEase catalogue still did not
  return a valid original for the decorated `晴天` sample, and the service correctly found 63
  lines through another provider. Only metadata/counts were emitted; lyric text was not saved.
- A later repeated matrix triggered NetEase's real HTTP-200/application-`405` rate limit.
  Direct provider calls now reported `HttpRequestException`, while the production service
  continued to return Found through Kugou for all four cases (49/63/66/54 lines). This caught
  and fixed the previous Failed-versus-Not found misclassification without exposing the
  upstream message to diagnostics.
- Repository inventory scanned 681 tracked entries / 62,951 source-and-test lines with no
  project-reference cycles. NuGet's current transitive vulnerability audit reported no known
  vulnerable packages in all six projects.
- Final Release suites: Core 242, Infrastructure 183 and App 123; **548 passed, zero failed,
  zero skipped**. `dotnet build DropSpace.sln --configuration Release --no-restore` completed
  with zero warnings and zero errors. Version, release consistency, localization, hardcoding,
  secret-hygiene and Windows-compatibility scripts passed. The built executable reports product
  version `0.3.0-beta.27` and file version `0.3.0.27`.
- The repository `dropspace-maintainer` Skill was reduced to durable project, UI and API guidance;
  its obsolete personal duplicate and synchronization gate were removed. The canonical Skill
  passed `quick_validate.py`.

## Boundaries

- Deterministic fixtures and live HTTP samples do not prove every provider catalogue item,
  region, future schema or network path.
- The desktop run verifies this installed player/component combination and the exercised
  track/control paths; it does not prove every future NetEase, BetterNCM or InfLink version.
  Exact long-duration pointer velocity is not measurable through the desktop automation API,
  so slow-drag timing is covered by the 500-frame deterministic interaction fixture as well as
  the real pointer drag.
- Release publication, website synchronization and public API verification are performed by the
  protected repository release workflow and reported separately from the local evidence above.
