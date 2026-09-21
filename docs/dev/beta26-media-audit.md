# Beta 26 media and adjacent product audit

Date: 2026-09-21. Scope: SMTC metadata/artwork/session ownership, media experience
coordination, playback interpolation, Music/Compact/Expanded UI, process-loopback
lifecycle; additional review of shell actions, ZIP exports, quick preview,
DropLink authentication/client and Nearby/Internet sharing boundaries.

## Confirmed defects and repairs

- **S1 — Optional artwork could retain the previous track's metadata.** A failed
  thumbnail open/read propagated into the whole SMTC candidate read. The service
  could retain its previous active snapshot instead of exposing the current
  title/artist/timeline, blocking a new lyric query. Thumbnail reads now have an
  independent one-second cancellation deadline and recoverable artwork failures
  return no artwork while metadata proceeds. Parent cancellation still propagates.
  Failure/stale fallbacks now require the same session identity; exhausting all
  candidate reads produces Empty instead of resurrecting a detached session.
- **S2 — A removed or filtered media session retained its control target.** The
  no-candidate branch published Empty without detaching subscriptions or the old
  session. It now detaches before publication. The real native smoke test applies
  an empty restrictive allow-list and asserts that control reports no session.
- **S2 — Estimated lyric position could jump backwards during ordinary playback.**
  The five-second observation-gap guard measured time since a metadata callback,
  even when visible frames had continuously sampled the clock. Sparse callbacks
  therefore reset a playing NetEase-style zero timeline to an old anchor. The
  guard now measures the last clock observation and retains its last observed
  position. Continuous presentation does not rewind; a genuinely unobserved long
  interval still avoids inventing elapsed playback. Seven clock tests pass,
  including quantized Apple positions, seeking, pause/resume and missing timelines.
- **S2 — Real Apple Music resume counted paused time as playback.** During the
  13:11:09 resume, native position remained 95 seconds while the next interpolated
  frame jumped to 124.3998 seconds, then returned to 96.8241 on the next native
  position. Apple reused the pre-pause timeline timestamp. Resuming now clamps the
  interpolation start to the resume snapshot's observation time; the 28 seconds
  spent paused are excluded. Two additional regressions exercise stale-timestamp
  resume plus delayed delivery; the clock suite now passes **9/9**.
- **S1 — Transient preferred-player failure could leave media permanently empty.**
  Real Apple Next at 13:12:19 left the long-lived service Empty while an independent
  native manager subsequently read two sessions and valid playing Apple metadata
  (title length 32, artist length 90, duration 175). Failed candidate reads detached
  their event subscription; a readable paused fallback also prevented later
  preferred-player metadata events from reaching the service. A separate preferred
  recovery subscription now follows metadata/playback/timeline events even while
  another session owns display/control. Candidate removal and shutdown detach it.
  The post-artwork consistency check also now rejects only metadata changes:
  Apple's frequent ordinary timeline events must not indefinitely invalidate a
  newly read track while its optional artwork is loading.
- **S2 — ZIP export flattened directories and dropped empty folders.** A second
  name sanitization replaced already-assembled path separators. ZIP names now
  retain sanitized path components, reserve distinct selected root names, record
  empty directories, and count directories against the existing 10,000-entry
  budget before enqueueing them. Dot-segment display names cannot form traversal
  entries. Failed archives are removed without modifying source files.
- **S3 — Playback controls exposed the wrong capability.** Play/Pause was enabled
  when either capability was present. It now checks Pause while playing and Play
  otherwise. Changed timeline bounds notify relative elapsed/remaining bindings
  even when the absolute position is unchanged.
- **S3 — Artwork presentation inconsistency.** Expanded/Music ignored ShowArtwork;
  they now honor it. On track changes artwork clears before synchronous Session
  notifications, matching the existing lyric-clear publication ordering.
- **S3 — Shell process wrappers and diagnostics.** All four shell launch paths now
  dispose their returned Process wrapper (without terminating the launched app).
  Shell/preview exception logs retain category and item ID where relevant instead
  of exception messages that can contain private filesystem paths. Repository-wide
  Process.Start search also checked the updater launcher, owned by the main audit.

## Validation evidence

- `dotnet test tests/DropSpace.Core.Tests/DropSpace.Core.Tests.csproj -c Release
  --no-restore --filter FullyQualifiedName~MediaPlaybackClockTests`: **9 passed**
  after the real-player resume correction.
- `dotnet test tests/DropSpace.Infrastructure.Tests/DropSpace.Infrastructure.Tests.csproj
  -c Release --no-restore --filter FullyQualifiedName~ZipActionServiceTests`:
  **4 passed**, including a real 10,000-directory budget/rollback fixture.
- MediaRegressionTests adds five tests: thumbnail failure, thumbnail timeout,
  caller cancellation, state-specific Play/Pause capability, and relative-time
  binding invalidation. The main audit repaired the unpackaged VSTest bootstrap;
  `tests/DropSpace.App.Tests/TestResults/beta26-native-bootstrap.trx` records
  **13 passed / 0 skipped** across the targeted media/OLE set. The earlier
  REGDB_E_CLASSNOTREG module-initializer failure was a test-host setup defect,
  not proof that this machine lacks Windows runtime support.
- An independent console probe under ignored `artifacts/media-audit-probe`
  compiles the actual WindowsMediaSessionService source against Windows SDK
  projections. `dotnet run --project artifacts/media-audit-probe/MediaProbe.csproj
  -c Release` actually passed SMTC subscription, empty allow-list detachment,
  disable/drain/restart, optional-artwork fault/deadline and caller cancellation.
  The current host exposed one Bilibili video session with zero duration and no
  artist/album. No player control was sent to that session.
- Code inspection confirmed separate artwork/lyrics cancellation generations,
  source/session identity inclusion, track-relative lyric highlighting,
  visibility-owned capture, and spectrum values sourced from actual PCM/FFT.
  Online-provider HTTP/matching/fallback/cache validation is reported separately
  by the lyrics audit owner; no claim here substitutes for those probes.

## Explicit limits

- The initial no-music-player limitation was superseded by the main audit's real
  Apple Music and NetEase UI playback session. Read-only production-source probes
  recorded the actual player metadata, artwork hashes, capabilities, timeline and
  full online LyricsService results while the main audit operated player/UI controls.
  `artifacts/media-live.jsonl` records Apple LRCLIB results (69 lines, 4.47/5.76 s)
  and four NetEase results (49/58/46/46 lines, 0.16-0.21 s), all with matching bound
  track identity. One incomplete Apple metadata query was canceled on the next
  generation; no completed result showed an identity mismatch. NetEase artwork
  updates followed new metadata within approximately 0.15-0.31 s and did not
  restart its lyric lookup. NetEase's native timeline remained zero/1601; its
  continuously observed estimated clock advanced and held correctly on pause.
- Apple resume and Next testing discovered the additional S2/S1 defects above;
  the main audit rebuilt the candidate and completed corrective live acceptance.
  `artifacts/media-live-extended.jsonl` preserves the before-fix evidence;
  `artifacts/media-live-fixed.jsonl` includes native timestamp/observation values
  and category-only diagnostic events for the corrected service.
- **NOT VERIFIED —** exact NetEase lyric synchronization from native position,
  because that player supplies no usable SMTC timeline. Playback estimation is
  explicitly a limitation, not an exact sync claim. Real-player PCM/UI acceptance
  and post-fix resume/Next results are recorded in the main audit's final runtime report.

Final fresh-candidate acceptance: Apple pause lasted 36.618 seconds, native 125
resumed at clock 125.144 and continued 126/127/128 without a paused-time jump.
Two Next transitions actually produced COMException candidate reads; Empty
recovered in 46.6/14.8 ms, with a superseded query canceled in 6.7 ms. LRCLIB returned
42 lines for the following track; an unmatched track reported NotFound. Returning
to NetEase and Previous returned 46 correct-identity lines in 181/167 ms. Main,
compact and expanded UI agreed; ShowArtwork-off was verified and restored.

- **NOT VERIFIED —** live multi-device DropLink and remote Worker sharing. Their
  auth/pinning/size/cancellation paths were inspected, without asserting a remote
  deployment or two-machine acceptance run.
- Shell disposal/privacy and Expanded artwork settings passed the final App
  build and applicable regression/desktop checks reported by the main audit.

## Documentation and Skill synchronization handoff

No architecture, dependency, persistence schema, source ownership or network
opt-in boundary changed. The maintainer Native Island reference must describe
independent bounded optional-artwork handling, preferred-source recovery event
ownership, metadata-only validation after artwork reads, frame-observed estimated
clock continuity and excluding paused time on resume. The maintainer release gate should include the ZIP directory budget
regressions. Repository/installed Skill synchronization and consolidated product
documentation are owned by the main audit to avoid concurrent inconsistent edits.
