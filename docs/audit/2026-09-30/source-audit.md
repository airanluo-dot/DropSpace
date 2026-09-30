# DropSpace source audit and local fixes — 2026-09-30

Base: `airanluo-dot/DropSpace` main at `1f238dd8ffe88929b95bab6bff6eb902789095de`
(Beta 28). The remote main SHA was rechecked after the work and was unchanged.
No branch was pushed, PR opened, deployment made, or release published.

## Scope and method

`scripts/audit-repository.py` read every tracked blob: 687 entries, 103 binary
files, and 64,115 C#/XAML source and test lines. No project-reference cycle was
found. Its full hashed inventory is saved in the execution workspace at
`/workspace/scratch/dropspace-inventory.json`; regenerate with:

```sh
python3 scripts/audit-repository.py --revision 1f238dd --output inventory.json
```

This exhaustive inventory and pattern scan is not whole-program semantic analysis.
Focused manual review covered the share worker, receiver crypto/download protocol,
nearby sharing, path containment, preview cache, settings persistence, update
streaming and recovery, authentication/replay policies, ZIP output, lyrics matching,
and application event/error boundaries. Build/test/analyzer, governance, dependency
and browser checks supplemented that review. No claim is made that every possible
bug has been eliminated or that unexecuted Windows behavior has been verified.

The environment initially contained no checkout or .NET SDK. Installed the
repository-pinned .NET SDK 10.0.401, PowerShell 7.5.3, website dependencies and
Playwright Chromium locally. Used the repository maintainer guide and cloud
runtime guide. No release version, package identity or product setting changed.

## Confirmed findings and fixes

| Finding | Consequence | Fix and evidence |
| --- | --- | --- |
| Worker route promises returned without awaiting inside `try` | Rejections bypassed JSON/status/CORS handling | Await routed operations; regression exercises all five routes |
| Coordinator reserve/commit promises returned without awaiting inside its callback | Policy rejections escaped the coordinator error boundary | Await each operation; invalid reservation retains HTTP 400 |
| Share creation wrote R2 metadata before atomically claiming an ID; identical initialization succeeded | Duplicate creators could obtain the original share's authorization or overwrite/delete its metadata and revoke it | Claim ID in the durable coordinator first, reject every duplicate, and keep conflict handling outside new-share rollback; tests cover identical/different duplicate claims, intact metadata, continued usability and concurrent initialization |
| Generated receiver download used an undefined `API_PREFIX` | Manifest rendered, but downloading a nonempty file raised `ReferenceError` | Emit the protocol constant; execute generated script with actual HKDF/AES-GCM ciphertext and verify downloaded plaintext and SHA-256 |
| Nearby HTTP suffix range recalculated the end from the suffix length; malformed endpoints were accepted | Valid suffix downloads returned 416 or incorrect bytes; invalid ranges could return data | Separate suffix/open/explicit parsing; 16 regression cases cover clipping, malformed and unsatisfiable input |
| Static preview server checked containment with string prefix | Encoded traversal could access siblings named `dist-*` | Validate `path.relative` before filesystem access; regression covers encoded sibling/parent escape; read body before sending successful headers |
| Playwright 1.55.0 affected by GHSA-7mvr-c777-76hp | Browser installer did not authenticate TLS correctly | Upgrade pinned test dependency and lockfile to 1.55.1; npm audit reports zero vulnerabilities |

Coordinator test storage now clones persisted values and serializes
`blockConcurrencyWhile`, matching durable-object semantics rather than sharing
mutable objects in the test fake.

## Checks actually run

- Core tests: **245 passed**.
- Infrastructure full suite after fixes: **193 passed, 7 failed, 5 skipped** (205 total).
  All seven failures are Windows DPAPI `PlatformNotSupportedException` on Linux:
  four authentication middleware tests, `PendingPairingsAreBoundedPerRemoteAddress`,
  and two Internet-share revoke-store tests. These are not verified as passing.
- Nearby range regression subset: **16 passed**.
- Share worker: **10 passed**, plus `node --check src/index.js` passed.
- Website build and Node tests after dependency upgrade: **29 passed**.
- Chromium browser tests on Playwright 1.55.1: **8 passed**. An earlier run on
  1.55.0 had one layout measurement failure; no layout source change was needed
  after the dependency/browser upgrade.
- npm audit after upgrade: **0 vulnerabilities**. NuGet vulnerable-package query
  for Infrastructure and transitive dependencies: no known vulnerable packages.
  The separate App package-list command failed with `Sequence contains no matching
  element`; it is not reported as a successful audit.
- Localization (597 synchronized keys), hardcoding governance, secret hygiene,
  release version/consistency and Windows compatibility policy checks passed.
- `git diff --check` passed.

## Remaining verification blocker

The available execution machine is Linux. App restore with
`EnableWindowsTargeting=true` succeeded, but WinUI build failed because
`XamlCompiler.exe` cannot execute on Linux (`Exec format error`). App tests depend
on this assembly and Windows App Runtime, so they were not executed.
`Test-BrandAssets.ps1` also failed at Windows System.Drawing initialization.
`Test-UpdateManifest.ps1` could not run without the Windows-built release EXE.
Native UI, DPAPI, installer lifecycle, portable smoke, MSIX and identity packaging
remain unverified. The existing Windows CI defines these checks; running it remotely
would require a push, which is outside this task's stopping point.

Before uploading the PR, rerun the Windows matrix's build, all .NET tests, brand,
installer, portable and package checks on a Windows machine. This audit is ready
for local review, but full Windows acceptance is blocked; it is not an all-green
release certification.
