# Beta10 Chinese-original regression follow-up

Comparison: Beta9 `9fc34f7e7505fac46bd9f15b08bdd567963a51f8` versus released
Beta10 main `4044baed6b1c393a815b91e1406a83e0995f1f94`. Follow-up branch starts
from that exact Beta10 main. All investigation was performed in the cloud.

The user reports some Chinese lyrics missing after Beta10, including 邓紫棋《唯一》.
The real failing media metadata, provider payload and generation trace have not
been supplied. The song name is a symptom/example, not proof of a catalogue,
artist-alias, script or upstream failure. No real provider requests were made.

## Defects established by code

### Same-language originals lost the normal response-reuse window

Beta10 `NetEaseResponseCache.ReuseLifetime` only tested for a matching secondary
translation. A Chinese original displayed under a Chinese target normally needs
no secondary, so the valid response was reduced from Beta9's ten seconds to one
second. Source-level `LyricsService.NeedsTranslationSearch`, however, uses
`NeedsProviderTranslation` and correctly accepts such an original. The HTTP cache
and source service therefore used different definitions of a satisfied target.

The follow-up also tests `!NeedsProviderTranslation` with a nonempty parsed
original before assigning the longer reuse lifetime. It uses the existing paired
NetEase parser and host language policy; no language/matcher/model rules change.
An original with foreign eligible segments or a requested different target still
gets only one-second reuse. It is never relabeled as a provider translation.

This is a confirmed increase in repeat-request pressure in Beta10, not proof that
any particular repeated request was rejected on the user's machine.

### Blanket source identity change dropped already-valid Beta9 originals

Beta10 advances all target-aware identities from `source-v4` to `source-v5`.
Even a verified same-language Chinese original cannot use its old disk entry,
so the upgrade requires fresh online discovery despite preserving cache files.
This can turn an available offline/cache result into an empty fresh-search result.

The follow-up checks the exact old v4 key only when v5 is absent and refresh is
not requested. It permits only the configured primary provider's nonempty
original that the current host policy says needs no translation. The existing
provenance checks, NetEase revision check and exact metadata matcher still run
before returning it. Foreign originals and old lower-priority decisions remain
ineligible. The identity includes the same strategy, target, track evidence and
exact duration; no title-only or cross-song cache reuse is introduced.

### Observed cancellation could disappear when snapshots coalesced A -> B -> A

`MediaExperienceService.OnMediaChanged` invalidates `_generation` and cancels the
current `RetirableMediaWork` at observation. `RunAsync` decides whether to reload
by comparing its previous processed snapshot to the latest snapshot. If B and a
return to A are both observed before the coordinator handles them, its comparison
is A to A. The old A fetch has been cancelled but no reload is required. A worker
retirement notification only queues another iteration; it does not itself request
a new lyric load. Therefore the final stable A can have no active fetch and no
original. Beta10's new 150 ms admission delay increases the interval in which A
has not fetched anything yet when this inherited cancellation race is hit.

The follow-up increments the already-existing `_reloadRequest` counter on each
observed track invalidation, so a cancellation remains a reload request even
when metadata events coalesce back to A. `RunAsync` consumes the captured request
version, retains its normal publication fences, and continues to respect the
two-owner bound. The 150 ms delay is retained; removing the delay alone would not
repair the lost cancellation/reload handoff. This App change was source-reviewed;
it has not been run against native SMTC in the Linux cloud.

## Boundaries checked without speculative fixes

Core language policy did not change between Beta9 and Beta10. Same-target Chinese
originals use `NeedsProviderTranslation`, not the presence of a translated row,
at source selection and source caching. Ambiguous pure Han still follows the
existing conservative abstention policy; mixed/foreign segments remain eligible.

`CandidateSearch.Report` keeps validated originals independently of a provider's
final result. Primary/supplemental selection and internal timeout catches recover
that document. Backup-winner cancellation targets remaining presentation work,
not the primary or already-retained original. The outer song token still correctly
prevents publishing a stale track. No new direct Chinese-original erasure was
established in this supplemental cancellation path.

The real 《唯一》 metadata may include different artist aliases or sparse/rich
sessions, but the matcher and Windows session-selection implementation are
unchanged between these two releases. They cannot be declared this regression's
cause without the actual evidence. The synthetic fixture uses that title/artist
only as metadata with original synthetic Chinese text; it is not a real-song API
or parsing validation.

## Actual focused verification

Only three new fixture executions were run; earlier twelve checks were not rerun.
The test command also compiled Core, Infrastructure and the infrastructure tests
successfully under the temporary cloud SDK 10.0.100. The pinned SDK 10.0.401 and
all release metadata remain unchanged.

* Same-language original is reused after five seconds without a secondary;
  switching to an English target refetches, and the foreign-target original
  expires again after two seconds (manual clock/fake HTTP).
* A valid Beta9 v4 primary Chinese original is returned without any provider call.
* A Beta9 v4 foreign original is not accepted as a Chinese-target completion and
  invokes the provider to obtain a new original.

WinUI App/native Windows compilation and the observed A/B/A scenario remain
unverified. The user's Beta9 comparison with unchanged data/settings is still
needed to determine how the confirmed defects relate to their real symptom.
No version publication or laptop access occurred.

## Read-only downgrade findings

Both versions have identical `installer/DropSpace.iss` and identity-registration
scripts. The official Beta9 Inno installer checks the installed registry
`VersionCode` in `InitializeSetup` and rejects an older code by default. It
explicitly permits `/ALLOWDOWNGRADE=1`. Its EXE file entry uses `ignoreversion`,
and the same AppId/previous install directory are retained. A graceful maintenance
shutdown is requested before replacement; ordinary installation does not invoke
the uninstall data-purge path.

The settings schema is 14 in both versions. There is no diff in the settings
model/service, schema validation/migration or item database/storage implementation
between the two commits. No Beta10-specific irreversible settings/database
migration was introduced. v4 lyric files were not deleted by the source-v5 key
change, so retaining the data directory preserves them for a Beta9 comparison.

Optional Windows Share identity registration uses `Add-AppxPackage` without a
force-downgrade parameter. Windows may independently refuse registration of an
older identity MSIX. That is distinct from the Inno EXE version guard, and its
actual outcome was not tested here; do not promise every OS integration rolls
back successfully. The unpackaged EXE/settings findings above remain separate.
