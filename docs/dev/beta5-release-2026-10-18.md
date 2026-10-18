# v0.3.2-beta.5 release record

The owner authorized normal publication for exactly two repairs: restore the historical widget
layout interaction and withdraw the verified internal sample DLC. Baseline: v0.3.2-beta.4,
main 053b6c29c476e9403176d6abb5df1be1da0011a4. Other open PRs are excluded.

## Sample identification and withdrawal

- File: dropspace.sample-1.0.0-win-x64.zip; 33,034,143 bytes;
  SHA-256 f1825c582671fa211e4445c28df426cff043fe358f96735a40fdfd2fbca3ff4f.
- modules/sample/README.md identifies a lifecycle/UI example: a message, boolean setting and
  short-lived Island content. e2f15f3 registered it in both production catalogs; 72b759a pinned
  the committed-source archive. FeatureModuleRuntime exposes catalog packages to the DLC page.
- The live main catalog and public Release dlc-dropspace.sample-1.0.0 matched the descriptor.
  Actual public ZIP download returned HTTP 200 and the expected length/hash before withdrawal.
- Saved the original Release JSON and ZIP locally under artifacts/beta5-release-2026-10-18/.
  Changed the Release to draft, retaining asset 620933552 and its tag. The original public ZIP
  URL now returns HTTP 404. No asset or tag was deleted; the withdrawal is reversible.
- Remove only dropspace.sample from both user catalogs; reject that exact ID in the registration
  script. Preserve its internal descriptor in modules/sample/fixture-catalog.json, and point
  existing internal probe/website fixture consumers there. No real DLC was removed. No module
  runtime, installed data or lifecycle implementation changed. Older App embedded listings can
  remain until catalog refresh or upgrade, but the old public download is withdrawn.

## Reused checks and current scope

The preceding widget repair ran two focused source-linked scenarios (2 passed), one targeted
editor compile (0 warnings/errors), source/history audit and whitespace checks. See
widget-numberbox-regression-2026-10-18.md. Its original no-publication statement describes that
completed turn and is superseded by the current explicit publication authorization.
These functional checks are not rerun. Historical Beta 4 failures and native visual limitations
remain in beta4-release-2026-10-18.md and local-iterations-2026-10-17.md.

No new optional functional, visual, full-matrix, stress or model tests are requested. Mandatory
release route: full Release App/XAML PR compilation; identical-tree merged-main package producer
with one isolated installer-payload scenario; security, source/resource/runtime, version/hash,
release/update and actual public-download checks. No gates or workflow definitions are changed.
Compilation, catalog/source inspection and download hashes are integrity checks, not functional
passes. Native drag/Undo clicks, visual/DPI and full upgrade lifecycle remain unverified.

Publication run IDs and final public artifact verification are recorded after execution.
