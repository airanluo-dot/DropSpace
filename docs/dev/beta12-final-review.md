# Beta12 final source review and release boundaries

The owner authorized publication after the final code review on 2026-10-06 local time.
The previous preparation-only boundary in earlier development notes is historical.
No deployed Beta11 asset is modified. Existing unrelated worktrees remain untouched.

Reviewed the new downloader's global admission/connection/bandwidth ownership, range
probe and strong identity checks, worker cancellation/settlement, saved partition,
size/hash checks, disk budgeting, atomic publication, settings merge/rollback and
exit draining. Reviewed the settings/backend/lyrics/QQ changes against their recorded
focused live evidence. This is a bounded review, not a zero-bug guarantee.

Final additions: updater, InfLink loader/plugin and Microsoft prerequisite all inject
the same HttpRangeDownloader singleton. Their redirects remain policy checked. App
updates preserve manifest size/SHA256 and installation checks; enhancement preserves
pinned/release hashes, receipts, rollback and player verification; the prerequisite
preserves size bounds and Microsoft Authenticode. NetEase feature/dependency entries
are registered in DLC, while version-update packages stay on the update page.
System runtime inventory uses registry data and cannot uninstall shared dependencies.

The user's screenshot exposed a separate build-input error: dev-simple/DropSpace.dll
was Beta12 with a descriptor naming Beta11, source 3e094dc35999372187bf6ecebe738f8700dc7ad6.
The binding guard correctly rejected it, which removed the provider and disabled the
CUDA choice. The installed component was not removed. Test-CudaBuildBinding now rejects
that stale metadata during compilation. The final build must restage the descriptor
for its exact commit and keep the original inner manifest/cache identity.

Actual targeted checks this phase: 10 UpdateDownloadTests and 2 ManagedDownloadSizeTests
passed. They cover segmented ranges/shared queue, validator resume, ignored Range,
hash/size, untrusted redirects, cancellation, containment and chunked upper bounds.
The old CUDA descriptor was explicitly rejected by the new build-binding check.
Final compilation and artifact results are recorded separately with the release.

Not rerun: full regression, real update installation, NetEase installation/removal,
cross-restart downloads, large/network/256-connection stress, other GPUs or 7B inference.
Earlier narrowly authorized CUDA and music playback evidence retains its own scope.
No user model, account, playlist or normal lyric cache is cleared for publication.
