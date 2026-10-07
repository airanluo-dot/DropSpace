# Overlay projection source probe

This is an evidence runner, outside the application and test projects. It compiles
five complete production method bodies extracted verbatim with real Core sources:
the service's `OnSnapshotChanged`, `OnExperienceChanged`, and `ApplySnapshot`, plus
the window's `ApplySnapshot` and `OnMediaGeometryChanged`.

`Fixture.cs.txt` supplies managed substitutes for native monitor lookup, the timer,
dispatcher, window, and WinUI elements. The compact event producer supplies one
scripted size change; it does not simulate text measurement. The real window
method reads the fresh geometry, then returns through its existing placement
suppression branch. Its subsequent native layout and animation are unexecuted.

Both source versions pass 13 console assertions. The final geometry-only change
removes one queued projection after synchronous preparation (1 → 0) and one
redundant window method invocation (2 → 1). Supplied dimensions, the single media
preparation, pre-existing pending callbacks, ordinary invalidation coalescing,
scope restoration after failure, and nested scope restoration remain consistent.
These are managed work counts, without Windows frame, CPU, latency or memory data.

The dual-monitor scenario uses the real experience coordinator and dismissal
predicate. The inactive window callback completes dismissal and publishes a
nested file snapshot before the active window receives its target. The unchanged
service's second projection preserves the final Hidden target. A proposed service
deduplication left both targets Dismissing while the authority was Hidden; it was
rejected and the service restored byte for byte. See the adjacent
`overlay-service-deduplication-rejected.json`.

The exact commands executed against the temporary project are in
`../overlay-projection-r3.json`; its sources are archived here. To reproduce in a
scratch directory, copy this directory there, set `DropSpaceRoot` to the repository,
and build `Probe.csproj`. The default `GeneratedSource` contains the final extracted
methods. To run the baseline, set `GeneratedSource` to the absolute path of
`baseline-methods.cs.txt`, then pass `baseline` to the executable.

`extract.py <app-source-directory> [output-file]` regenerates the method source.
The independent `../r3-overlay-geometry.patch` applies only the final window change
relative to the saved pre-R3 snapshot, which already contained the earlier media
lifecycle optimizations. Production `OverlayWindowService.cs` has no R3 diff.
