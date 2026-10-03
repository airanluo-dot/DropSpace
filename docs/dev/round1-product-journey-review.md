# Round 1: product-manager and first-time-user journey review

Date: 2026-10-01. Reviewed commit `3e038e1b0cb7fcd0fc4a26282979a4c91f5d1e4b`, tree `d2d913eb68d60254af1f88861e85d9f39f39287e` (parent-provided equivalent PR 76 head `49ab7ef55423456d16560309c78d288d44b53655`). This report is a read-only application review; only this report was added. No application source, website appearance, version, publication, or push was changed.

## Verdict

**Not ready for product acceptance.** The biggest first-use failure is that enabling AI does not make its translations visible under default settings. The new AI experience also lacks several explicitly planned trust/recovery controls: visible attribution, per-song activity/failure state, model removal, and separate AI-cache cleanup. These are separate from the already-known semantic-quality blocker in `round1-full-song-semantic-review.md`; that blocker is not a new finding here.

This is one product-focused part of round 1, not completion of three full rounds. Reproduction instructions below are source-derived acceptance scenarios unless explicitly marked executed. Linux cannot validate WinUI rendering, native gestures, screen readers, real Windows players, installer behavior, or performance on the required hardware.

## Evidence and checks actually performed

- Read repository `AGENTS.md`, canonical maintainer skill and app/UI reference, relevant PRODUCT/UX/INSTALL/PRIVACY/README material, and the existing round-1 App findings to avoid presenting those as new findings.
- Read the approved planning DOCX supplied by the parent via its ZIP/XML text: `../ai-lyrics-plan/DropSpace_0.3.1_Beta1_AI歌词项目规划.docx`. This is a design-baseline comparison; the parent must reconcile any later user instructions before changing conflicting behavior.
- Traced defaults → startup/settings → clipboard registration, main navigation and empty states; Music/AI settings → download/verification → inference → presentation/cache; font/glow controls; updater deployment-aware action visibility and settings automation names.
- Executed Core focused tests: `dotnet test ...Core.Tests.csproj --no-restore --filter 'FullyQualifiedName~LyricsDisplayPolicy|FullyQualifiedName~LyricsFontSize|FullyQualifiedName~LyricsGlow' --verbosity quiet`: **14 passed, 0 failed**. This filter does not establish native font-control behavior.
- Executed Infrastructure focused tests using `-m:1`: filters `AiModelPackageServiceTests`, `AiLyricsCacheTests`, `LyricsTranslationCoordinatorTests`: **18 passed, 0 failed**. The initial parallel-MSBuild attempt failed creating a named pipe (`Permission denied`); serial execution succeeded. No network model download or native inference was performed by this reviewer.
- Native manual execution: **none**. Hardware/peak-memory/first-result/whole-song timing acceptance: **not established**.

## Integration update after the reviewed snapshot

At 21:06 UTC the parent reported remediation in progress: AI enabling now enables secondary display, presentation adds an `AI ·` prefix, and partial matching-provider documents remain unchanged without AI supplementation. The parent confirmed the approved DOCX policy remains controlling. PJ-01, PJ-02 and PJ-08 below describe the reviewed commit; this reviewer has **not** revalidated those concurrent edits. Model/cache/state work is also occurring in the shared worktree. No other worker's changes were edited by this review.

## Findings in the reviewed snapshot

Severity: High = major first-use/trust failure or release acceptance blocker; Medium = important missing control/ambiguous behavior; Low = polish/documentation debt. Evidence is relative to the repository root.

### PJ-01 — High: a first-time AI user downloads a model and still sees no translation

**Scenario:** Start with clean settings; play a supported foreign-language song with original lyrics; enable AI and accept/download the model; wait for successful translation. Do not separately enable “Secondary lyrics.”

**Evidence:** `Core/Models/NativeIslandSettings.cs` (under `src/DropSpace.Core`) leaves `SecondaryLyrics` false. `App/Views/Music/AiLyricsSettingsCard.cs:OnEnabled` saves only model, AI enable, and glow. `App/Views/Music/MusicPage.cs:RefreshLyrics` and `App/ViewModels/MediaViewModel.cs:SecondaryLyricText` require `SecondaryLyrics` to display any translation. `Core/Lyrics/LyricsDisplayPolicy.cs:Secondary` immediately returns null when disabled. AI work itself does not require that display switch (`App/Services/Media/AiLyricsService.cs:TranslateIfAvailableAsync`).

**Impact:** More than 1 GB and inference time can be spent for no visible benefit. The default AI glow also remains ineligible because no AI text is displayed. Users reasonably conclude the new feature is broken.

**Minimal product remedy:** Make the AI enable flow explicitly reconcile the secondary-display dependency, or show an adjacent actionable explanation before download. Preserve a deliberate later choice to hide translations; do not silently keep overriding it.

**Acceptance:** Clean-install and upgraded-settings journeys must produce visible translated text after a successful AI run, or explain the user's intentional hidden state. Cover both full Music and compact/expanded island surfaces, and AI-off → on after manual hiding.

### PJ-02 — High: generated translations are presented without an AI source label

**Scenario:** Enable secondary display, obtain a local AI result, and compare it with provider translations in Music and the island.

**Evidence:** The plan explicitly requires a concise AI marker. `MusicPage.RefreshLyrics` renders `line.Text` and `LyricsDisplayPolicy.Secondary(...)` as ordinary TextBlocks. `MediaViewModel.SecondaryLyricText` returns only the string. App-wide `TranslationOrigin` usage is in translation decisions and overlay glow eligibility, not a user-facing attribution element. There is no visible AI badge/string in these presentation paths.

**Impact:** A fallible generated translation looks like a source translation; glow cannot serve as attribution because it can be off, hidden, paused, reduced, or unavailable.

**Remedy/acceptance:** Add a restrained localized, accessible AI-origin indicator beside actual AI text, including cached output. Provider text must not acquire that label. Check mixed-origin documents after resolving PJ-08.

### PJ-03 — Medium: “ready to use” masks active translation and individual failures

**Scenario:** With a verified model, start a long translation; then cause one inference timeout or unavailable runtime. Observe the AI card and lyrics state before the third consecutive failure.

**Evidence:** `AiLyricsSettingsCard.Refresh` knows inspection/download/readiness and circuit pause, but no translating or temporarily-unavailable state. `AiLyricsService.TranslateIfAvailableAsync` catches ordinary inference failures, logs a category, and returns original lyrics. `MediaExperienceService.LoadLyricsAsync` sets the provider-query status, then performs AI without publishing AI activity/failure. Only the transition to the three-failure circuit pause raises a separate state event.

**Impact:** The user cannot tell whether to wait, retry, repair, or simply turn on hidden secondary display. The approved plan explicitly listed “正在翻译” and “暂时无法翻译.”

**Remedy/acceptance:** Publish bounded, generation-scoped activity/result states without an interrupting dialog. Show original lyrics throughout; distinguish model availability from translation availability, and do not flash notifications on cache hits. Test first/second failure, cancellation, stale-song results, runtime failure and recovery.

### PJ-04 — Medium: no supported way to delete a downloaded model

**Scenario:** Download Standard, try Compact, then try to reclaim one model's storage while retaining the other. Also try removal while the selected model is translating.

**Evidence:** `AiLyricsSettingsCard` creates Download, Cancel download and Resume translation only. `AiLyricsService` and `AiModelPackageService` have no model-removal operation. The catalog totals 2,009,392,000 bytes for both weights before runtime/cache overhead. The approved plan requires removal and stopping tasks before deleting an enabled model.

**Impact:** The feature is reversible only by manually navigating internal storage or deleting all app data, an unacceptable first-user cleanup story.

**Remedy/acceptance:** Explicit per-model removal with storage size and current-use state; cancel/await model-owned work before deleting its weights and partials, disable or switch selected model predictably, retain unrelated model/cache/provider data according to stated choice. Test active inference, in-progress download, locked-file error and restart.

### PJ-05 — Medium: “Clear lyrics cache” leaves persistent AI results behind

**Scenario:** Generate a translation; click Clear lyrics cache; play the same song again and attempt to clear/retry a bad AI result.

**Evidence:** `MusicPage` wires the button to `MediaExperienceService.ClearLyricsCache`, which calls only `_lyrics.ClearCache()` and reloads. AI uses separate `AiLyricsCache` at `AiLyrics/Cache`, with JSON persistence and 100 MB trimming; its API has no clear operation. The approved plan requires separate AI cache cleanup.

**Impact:** Cache clearing does not mean what the expanded AI feature makes users expect, and users cannot deliberately force a fresh AI result or remove retained translations independently.

**Remedy/acceptance:** Provide an explicitly named AI-cache clear action and accurately scope the existing provider-cache action. Coordinate cancellation/generations so an in-flight old job cannot immediately repopulate cleared data. Verify actual disk removal and preservation of downloaded models and normal lyrics.

### PJ-06 — Medium: model confirmation omits runtime storage overhead

**Scenario:** Open a model confirmation on a machine with limited free space and inspect the total storage explanation.

**Evidence:** `AiLyricsConfirmBody`, `SizeLabel` and `AiLyricsModelDetails` describe only the weight bytes. `AiLyricsRuntimePackage` later extracts embedded completion/tokenizer executables into the app data root. The approved plan asks for model size plus estimated additional runtime size. `AiModelPackageService.DownloadAsync` does perform a remaining-weight + 64 MiB free-space check; this is not a claim that no check exists.

**Impact:** The user is not shown the full storage cost or told that runtime components are bundled/extracted. Do not incorrectly describe this as a second runtime network download.

**Remedy/acceptance:** Derive the additional installed/runtime estimate from the actual package manifest, distinguish download from disk footprint, and disclose temporary/resume headroom. Verify the chosen reserve against the final distributed runtime and low-disk recovery.

### PJ-07 — High product/privacy concern: online lyrics start enabled without an in-app first-use boundary

**Scenario:** Launch with clean settings while a supported player is running, before visiting Music. Observe permitted outbound provider calls and the first screen's explanation.

**Evidence:** `NativeIslandSettings.cs` defaults media and lyrics to enabled, `Mode` to enum zero (`Online`), and provider to NetEase. `App.xaml.cs` initializes `MediaExperienceService`; its worker queries lyrics. No first-use consent/disclosure gate was found in this flow. Music has an Online/Local selector and fallback order help, but no nearby plain-language disclosure of the metadata sent. `PRIVACY.md` describes online lyrics as opt-in and lists title/artist/album/duration. This is a discrepancy with the default, not evidence that file/clipboard content is transmitted.

**Impact:** “Local-first” users may unintentionally disclose playback metadata before encountering the setting. Stronger AI download consent does not cover a distinct default online-lyrics boundary.

**Remedy/acceptance:** Resolve the intended product default explicitly. Prefer offline/disabled online lookup until a clear choice, or provide a conspicuous, accurate first-use explanation before requests. Verify zero provider requests until the agreed boundary and no fallback from local LRC mode.

### PJ-08 — Plan conflict, pending parent reconciliation: partial provider translations are supplemented

**Scenario:** Supply two original lines, one with a matching-language provider translation and one without; enable AI.

**Evidence:** Approved DOCX says the first version must not automatically fill partial source translations, avoiding mixed provenance. `LyricsTranslationCoordinator.TranslateBatchesAsync:27–35` chooses every nonblank original line without a matching provider translation, so the missing line is inferred. `AiLyricsService` skips only if all relevant lines already match. Existing preservation of the translated line does not resolve the document-level policy conflict.

**Impact:** The current flow mixes provider and generated translation, made more confusing by PJ-02. This is reported as a policy conflict, not permission to revert potentially newer user instructions.

**Acceptance:** Parent must establish latest approved behavior, then add an explicit partial-provider fixture and visible provenance expectations for the selected policy.

### PJ-09 — Medium: clipboard recording begins before the first screen explains it

**Scenario:** Fresh launch into Temporary Space; copy sensitive-looking test text in another application before opening Clipboard or closing the window. Return to Clipboard.

**Evidence:** `AppSettings` defaults capture on, launch page Space, startup true and close-to-tray. `MainViewModel.InitializeAsync:618–639` ensures startup registration and initializes capture before navigation. `ClipboardCaptureService.InitializeAsync` subscribes and starts the worker. Main header recording status is visible only on Clipboard (`MainPage.xaml.cs:UpdateSectionChrome`); the privacy warning is in Settings. A first-close dialog does correctly explain continued capture, but arrives after the initial recording window.

**Impact:** A new user focused on file staging can be unaware that text/images/file references are being retained and that subsequent sign-ins launch capture. These defaults predate this feature; this is product-onboarding debt, not a newly introduced implementation regression.

**Remedy/acceptance:** A concise first-run explanation/choice for capture, retention and startup, with obvious pause and clear routes. Keep Space/Clipboard distinction and source-safe behavior; no elaborate redesign required. Test launch, close, reopen, explicit exit and next sign-in, including paused-state persistence.

### PJ-10 — Low: the installation guide still leads with a historical version

**Evidence:** `INSTALL.md` recommended setup still describes the “v0.1.0 installer.” The current candidate/version/channel naming is elsewhere. README also contains long historical release narratives ahead of core instructions.

**Impact:** New users cannot easily distinguish current supported installation guidance from historical preview details. This review did not change the approved desktop/mobile website appearance.

**Remedy/acceptance:** Make evergreen installation statements version-neutral and clearly distinguish currently distributed Stable/Beta, installer/portable and signing/Share limitations. Verify final published filenames and requirements at release time rather than assuming candidate artifacts already exist.

## Product friction requiring native observation, not yet asserted as a defect

- Music's lyric viewport automatically recenters at each current-line change (`MusicPage.RefreshLyrics`) without a manual-scroll grace period. A user reading another verse may be pulled away on the next lyric. Test manual inspection during playback; decide whether temporary follow suspension is warranted.
- AI card `OnUnloaded` cancels its download and clears transient status; interrupted weight bytes can resume. Do not infer that simply navigating a collapsed Music surface unloads it. Test actual page retirement/language change/window lifecycle and make interruptions understandable.
- Both model files are hash-verified during inspection. On a slow disk, verify “Checking downloaded models” duration, page responsiveness and cancellation with both installed.
- Large 144×88 glow thumb, bilingual labels, 12–28 lyric-size range and scaled content require actual narrow-window/200% DPI/text-scaling/high-contrast checks. Source inspection alone cannot establish clipping or smoothness.

## End-to-end new-user acceptance checklist

All Windows/manual rows below remain unexecuted here; source support is not a pass.

- [ ] **Install/start:** Clean supported Windows installer and portable runs, official-artifact/signing messaging, no admin surprise, correct startup registration, close explanation, explicit exit, restart and data preservation
- [ ] **Understand storage:** Empty Space clearly teaches reference semantics; external file drop → open → drag out → remove record leaves original intact; actual text/image/virtual-file intake clearly distinguishes owned local copies from references
- [ ] **Missing source:** Rename/move/delete an external original; unavailable state → locate/replace/remove remains understandable and recoverable
- [ ] **Clipboard:** Discover capture state, copy text/image/files, recover and copy again without loops; pause before sensitive work; confirm clear ranges/pinned exceptions; restart while paused; disk/clipboard-busy errors remain actionable
- [ ] **Music baseline:** No player, supported player, unsupported/weak metadata, player allowlist, local LRC/offline, online provider failures, song change, seek, pause/resume, nested lyrics scroll, provider fallback boundaries
- [ ] **AI first success:** PJ-01 fixed; download consent with truthful source/bytes/overhead, Standard/Compact choice, hash verification, translated text visible with AI label, original preserved
- [ ] **AI recovery:** Cancel/resume, app restart mid-download, wrong hash/Range, low disk/permissions, missing runtime/model, translating/temporary failure/circuit pause/resume, model switch, disable during inference, latest song/language wins
- [ ] **AI management:** Remove selected/nonselected model safely, independent AI cache cleanup, no in-flight repopulation, privacy retention copy matches disk behavior
- [ ] **Fonts/glow:** Decimal keyboard entry, invalid/intermediate values, slider drag/release, reset, persistence, page changes and save failure; all three glow modes across AI/provider/original/interlude/pause/no-track/hidden states, reduced motion and high contrast
- [ ] **Accessibility:** Narrator names/current values/live states, full keyboard journey, focus after dialogs/errors/navigation, 100–200% text scaling and mixed DPI. Existing legacy label finding is not repeated: current `MainPage.ApplySettingsAutomationNames` adds names; native verification is still required
- [ ] **Updates:** Stable/Beta ordering, installer/portable action differences, recoverable failed download/install, launch acknowledgment, Beta32 → 0.3.1 Beta1 data/model preservation, no unauthorized channel change
- [ ] **Hardware/quality:** Required ordinary no-discrete-GPU laptop and discrete-GPU Windows machine; cold/warm model load, first visible output, full-song time, actual peak RAM/CPU/GPU, cancellation latency, idle release, glow frame/CPU/GPU cost

The approved 15-second first-result / 60-second whole-song targets are **not passed** by generic runtime limits or Linux fixture tests. The current coordinator returns only after its complete batch loop, so “first segment” must be measured as an actual visible user result, not merely internal batch completion. Semantic quality remains the already-recorded blocker. Three final-code full rounds and release verification remain outstanding.

## Concise issue checklist for integration

- [ ] PJ-01 High: reconcile AI enable with hidden secondary display
- [ ] PJ-02 High: visibly and accessibly attribute AI text
- [ ] PJ-03 Medium: show translating/temporary failure separately from downloaded readiness
- [ ] PJ-04 Medium: implement safe per-model removal
- [ ] PJ-05 Medium: independently clear AI cache; scope existing clear label
- [ ] PJ-06 Medium: disclose actual runtime/additional storage estimate
- [ ] PJ-07 High product decision: reconcile default online lyrics with opt-in disclosure
- [ ] PJ-08 Policy resolved by parent; implementation retest pending: retain partial matching-provider document
- [ ] PJ-09 Medium existing debt: disclose recording/startup at first use
- [ ] PJ-10 Low: remove stale installer-version wording
- [ ] Execute native journey/accessibility/hardware matrix; do not count this static review as that execution

## Follow-up: first implementation of product remedies, 21:09–21:11 UTC

Read-only review of the concurrent working-tree changes requested by the parent. This is **not** another full round and does not supersede the semantic or Windows acceptance blockers.

### Source-level improvements verified

- AI enable now also turns on secondary display; a subsequent deliberate manual hide remains possible (PJ-01).
- `SecondaryPresentation` prefixes only local-AI translations with `AI ·`, leaving stored content and provider output unchanged. Music and island view-model presentation call it (PJ-02).
- App service and coordinator both respect document-level matching provider translation, including a partial source translation. The parent resolved PJ-08 against the approved plan.
- Model deletion and independent AI-cache clearing now have explicit confirmations, cancel/drain inference ownership, and block new AI work during maintenance.
- Download confirmation now distinguishes bundled runtime extraction from additional network download and computes an approximate upper bound from embedded EXE resource lengths (PJ-06).
- New service states and localized card labels exist for translating, completed and temporarily unavailable, but the card precedence bug below prevents reliable first-use feedback.

### New remedy-review findings reported immediately to the parent

**PJ-R1 — Medium: success messages permanently mask live inference state.** After download, `_message` is `AiLyricsDownloadComplete`; cache clear/delete similarly assign success text. `Refresh` uses service TranslationState only when `_message` is empty, and ordinary `OnTranslationStateChanged` calls only Refresh. A first-time download/enable therefore continues to say downloaded while translating or failing. Reproduce download → AI enable → inference state changes without starting another card operation. Separate operation feedback from live activity, expire success feedback, or clear stale success text on a meaningful current-generation state transition. Test first download and cache-clear → later song as well as the three-failure pause override.

**PJ-R2 — Medium: failed settings persistence does not stop model deletion.** `OnDelete` awaits `SaveAsync` to disable the selected model, but SaveAsync returns no success result and merely sets error text when `NativeSettingsEditor.UpdateAsync` returns false. It then calls DeleteModelAsync anyway and may show removal success alongside the save error while persisted settings still enable the deleted model. Inject save failure; assert no weights are removed until disable is durably accepted, or use a deliberately specified atomic/recoverable alternative. Deletion failures after a successful disable should remain truthful and retryable.

**PJ-R3 — Medium: restarted partial/corrupt models cannot be removed through the new UI.** Delete visibility depends on `_installed` or in-memory `_interrupted`; startup inspection only verifies complete `.gguf` files. Restart with a leftover `.partial`, or a corrupt final model, and neither set contains the model, so Delete is hidden despite consumed disk space. Inspect owned artifact existence separately from verified readiness; expose safe deletion for these states and preserve the distinction from usable models.

**PJ-R4 — Medium: idle cache clearing can execute a large filesystem loop on the UI thread.** `ClearAsync` is synchronous enumeration/File.Delete returning an already-completed Task. With no active inference and a free maintenance gate, `MaintainAsync` awaits already-completed tasks and invokes that method inline from the card's click continuation. A cache with many small JSON files can block input; 100 MB does not bound the number of files. Execute maintenance filesystem work off the dispatcher, expose a busy/clearing state, and test cancellation/error feedback with a large cache. Synchronous model deletion and cancellation callbacks deserve the same thread-ownership discipline, though the demonstrated unbounded loop is cache clear.

### Lifecycle observations and remaining tests

The new `AiLyricsWorkLifetime` rejects new inference while maintenance owns the gate and awaits active scope retirement before mutation. Its two added tests cover basic cancel/drain/admission and recovery after a maintenance exception. More targeted acceptance is still needed: actual runner exit/file release before removal, cancellation during drain, two queued maintenance requests, page retirement during confirmation/maintenance, save failure, file locks, corruption/partials after process restart, and no old job repopulating a cleared cache.

A fresh cache hit currently still passes through Translating then Completed; ensure this does not create repeated live-region announcements contrary to the plan's quiet-cache-hit intent. Runtime readiness and missing-model states should also be tested separately from successful weight verification. These are follow-up acceptance points, not native failures observed here.

### Compilation actually performed

`dotnet build /tmp/round1-ai-circuit-check/check.csproj --no-restore -m:1 -p:BuildProjectReferences=false --verbosity quiet` completed with **0 warnings, 0 errors** at approximately 21:10 UTC. It links the real AI card, service, font/glow controls and dialog lifetime against real WinUI/Windows projections; its `NativeSettingsEditor` is a stub, so this is compile evidence, **not** a settings-failure or native UI runtime test. Parent-owned Core/Infrastructure runs must be reconciled separately. Application source was not changed by this reviewer.

## Second remedy inspection and PJ-09 implementation guidance, 21:12 UTC

### Updated source-review status

The parent addressed PJ-R1 through PJ-R4 while this review continued. Re-read confirmation:

- Translation-state events now clear stale operation messages for non-Ready states.
- `SaveAsync` returns a current-generation success flag; selected-model deletion returns without touching weights when disabling fails.
- Inspection now records `HasModelArtifacts` separately from verified installation; deletion includes `_removable`, so partial/corrupt artifacts survive restart as removable items.
- Actual maintenance actions run in `Task.Run`, and cache clear sets a localized clearing message before awaiting work.

These changes address the specific source paths identified above. No native failure-injection execution was performed. The earlier projection build predates this second edit set; wait for the parent's build/test stabilization before another final compile.

PJ-07 also has a source remedy: fresh `LyricsSettings.Enabled` defaults false, Music presents the metadata/network explanation, and AI enable asks about online lyrics before model download if it would turn on disabled online lookup. AI enable then enables lyrics and secondary text together. Existing explicitly serialized Enabled=true remains deserialized true. Regression tests must cover fresh/off and existing/on separately.

Two **NativeSmoke-only default assumptions** need explicit Enabled=true after this change: `tests/DropSpace.Infrastructure.Tests/LyricsProviderStrategyNativeSmokeTests.cs:35–43` (four scenarios) and `tests/DropSpace.App.Tests/MediaSessionNativeSmokeTests.cs:86`. Without updating the setup, disabled short-circuiting prevents the provider behavior those tests intend to exercise. A normal suite excluding NativeSmoke will not catch that omission.

### PJ-09 minimal insertion point and ownership

Current order is:

1. `App.xaml.cs` resolves settings service; optional safe-mode/reset writes preferences; loads settings for language.
2. It constructs MainWindow, whose constructor creates MainPage and puts it in RootContent; activates the window for ordinary launch.
3. It initializes ClipboardNotificationService, then calls `MainViewModel.InitializeAsync`.
4. `MainViewModel.InitializeAsync` loads settings, migrates placement, calls `EnsureStartupStateAsync` (registration), initializes repository/undo, then `ClipboardCaptureService.InitializeAsync` (recording worker/subscriptions).
5. Tray, overlays, media and remaining experience initialization follow.

**Recommended small change:** Add a startup-owned first-use choice after constructing/activating MainWindow and obtaining its XamlRoot, but **before** calling MainViewModel.InitializeAsync (and preferably before registering clipboard notifications). It must complete and durably save the explicit choices before any clipboard recording or startup registration. Do not hang it off MainPage.Loaded: that callback already sets up ordinary page controls and is not the owner of async startup ordering.

Use the existing `MainWindow.WaitForXamlRootAsync` pattern and `ContentDialogLifetime.ShowAsync`; the recovery dialog demonstrates a bounded wait, catch and cancellation. The first-close dialog demonstrates localized content, process-lifetime cancellation and durable acknowledgment, but happens too late to serve as initial disclosure. Keep that close explanation separately.

A compact dialog should state: clipboard recording stores text/images/file references locally while running, has finite retention and pause/clear controls, and window-close can retain the process in the tray. Offer independent recording and launch-at-sign-in choices, initially unchecked, with Continue and Exit/Not now behavior explicitly defined. Closing/canceling must not silently approve capture or startup registration. On save failure, keep the startup gate closed and show retry/exit, rather than treating an in-memory choice as complete. Temporarily disable the ordinary page while awaiting the dialog/XamlRoot to avoid pre-initialization settings/intake mutations.

**Persistence/migration:** Introduce a durable onboarding version/completed marker. Use successful settings-load provenance to distinguish a fresh root from an existing valid settings file containing explicit ClipboardPaused/StartWithWindows values. Preserve those old choices and mark an eligible legacy configuration complete through migration; do not reset or re-prompt an already configured user solely because the marker did not exist historically. Missing/corrupt settings should fail closed. Do not use a late `File.Exists` check as the only signal: safe-mode/reset and corrupt-file recovery may already have written newly synthesized defaults. Capture provenance in the settings layer or persist marker state through those paths, and include the marker in relevant merge/recovery policies.

Save the first choice through the settings service before runtime initialization. Avoid `MainViewModel.UpdateSettingsAsync`/`SettingsApplicationCoordinator.UpdateAsync` at this point: those are live transactions that can register startup and update the clipboard subsystem as side effects before the gate is complete.

**Headless activation:** A fresh `--startup`, Share or shell activation must not bypass the gate. Either bring forward the same first-use surface or defer passive services safely while completing only a narrowly defined explicit intake flow. Existing configured users retain their normal hidden startup. Specify cancellation/shutdown so the app cannot remain in an unexplained half-initialized state.

**Smoke setup without a normal-start bypass:** Have `scripts/Test-PortableSmoke.ps1` seed explicit onboarding completion and the test's chosen capture/startup settings in the isolated `DROPSPACE_TEST_DATA_ROOT/data/settings.json` before launching. That script already creates a GUID-scoped data root. Do not add an automatic `--smoke-test`, `--test-mode`, or environment-variable consent bypass in app startup. Include dedicated first-run tests with no seed, and a persisted-legacy fixture. Other installer lifecycle automation should likewise provision a bounded test fixture where needed, preserving ordinary production startup semantics.

Required focused tests: fresh/no-choice no registration or persisted captures; opt in independently; opt out independently; close/cancel; failed settings save; valid old explicit choices; missing/invalid marker; malformed settings recovery; safe-mode/reset on fresh and existing roots; startup/shell/share activation; concurrent second instance; seeded smoke config; unseeded test-mode cannot bypass onboarding.

## PJ-09 implementation inspection, 21:17–21:19 UTC

The parent implemented the suggested gate. MainWindow starts with page interaction disabled; initial choices are unchecked; Skip stores capture paused/startup off; App persists choices before `MainViewModel.InitializeAsync`. Fresh/startup/share/shell primary launches all reach the same gate, while valid legacy JSON with explicitly persisted capture/startup booleans migrates without changing those choices. Fresh, empty, corrupt and explicit-uncompleted configurations remain gated, including the early safe-mode/reset path. Portable smoke now seeds its isolated settings fixture instead of adding a production bypass switch. These are source-level checks.

Two startup lifecycle follow-ups were immediately sent to the parent:

- **PJ-R5 — Medium, close-after-consent race:** The initial version used only the window close-explanation token for the dialog and called settings SaveAsync/InitializeAsync without the app-lifetime token. Clicking Continue and then X while settings persistence is pending can start shutdown while the startup continuation still registers startup/capture afterward. Thread app cancellation through the dialog/save/initialize path, check after dialog and save, and own/drain the choice task during window retirement. Confirm no service starts after shutdown wins; an explicit persisted choice may remain, but cancellation must not continue initialization.
- **PJ-R6 — Medium, redirected activation before readiness:** `OnInstanceActivated` is subscribed before setup finishes and immediately invokes Share/Shell handlers through the dispatcher. A second instance can enter intake against an uninitialized view model/repository while the privacy dialog is pending. This is not proof of passive clipboard capture before consent, but is an initialization bypass/error path. Queue behind a cancelable startup-ready gate or fail/defer the request explicitly; do not lose the external activation silently.

The AI card/service projection build was repeated at approximately 21:19 UTC with `BuildProjectReferences=false`: **0 warnings, 0 errors**. Its scope remains AI card/service/controls and real WinUI projections, not App/MainWindow XAML-generated startup compilation or native first-run execution. No application source changes were made by this reviewer.

## Final read-only follow-up, 21:20 UTC

Re-read PJ-R5/R6 remedies: the initial-choice task now links application and window cancellation, checks cancellation after the dialog, and is owned/awaited by MainWindow disposal. App checks the app token before saving, after saving/before initialization, and after initialization; both SaveAsync and InitializeAsync receive it. The expected canceled-startup path avoids a crash marker. Redirected activations now wait in a bounded concurrent queue until `_startupReady`, including arrivals before a window exists, and callbacks check app cancellation before processing.

Those edits address the two specific pre-consent startup paths reported above. No additional definite release-blocking defect was identified in this final source pass. This is deliberately **not** a native startup/closing-race execution result or proof of every possible app-shutdown race. Actual App/MainWindow XAML-generated compilation and Windows first-run/second-instance tests belong to the upcoming head's CI/native verification; the local projection build covers only the explicitly listed AI surfaces.

Current product-remedy disposition: PJ-01–PJ-09 and PJ-R1–PJ-R6 have source-level remedies or the parent-resolved policy, with native acceptance/injected-error tests still required. PJ-10 installation wording remains documentation follow-up. The known semantic-quality blocker, required hardware measurements and three final-code full-review rounds remain open. No release approval is implied.
