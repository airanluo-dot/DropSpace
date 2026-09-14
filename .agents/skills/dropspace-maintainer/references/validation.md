# Validation and evidence

Read this file when deciding how much validation a DropSpace change needs or whether a requested implementation/release is actually complete.

## Evidence ladder

Keep these states distinct:

1. source inspected/changed
2. targeted tests passed
3. affected project/build passed
4. broader local suite passed
5. commit pushed / PR created
6. CI passed
7. public Release published
8. website/deployment completed
9. live endpoint/UI verified
10. physical Windows, peer-device, or browser acceptance recorded

Do not use evidence from one layer to claim success at a later layer.

## Proportional validation

Prefer the smallest high-signal validation first.

- Isolated logic change: targeted tests, then affected build if compilation/integration matters.
- Bug fix: reproduce from evidence when possible and add a useful regression test when practical.
- UI/window/drag change: targeted tests plus the Windows-specific acceptance relevant to focus, DPI, OLE, accessibility, or lifecycle.
- Schema/data change: migration/model tests from supported prior state plus retention/clear/recovery paths affected by the change.
- Updater/release-data/security change: malformed/failure/trust-boundary tests plus affected build/workflow validation.
- Shipping task: release/build checks, CI, public assets, deployment, and live verification required by `release-delivery.md`.

Do not run an unrelated full matrix merely because a tiny file changed. Do not skip a broad matrix when the change genuinely crosses a broad compatibility or shipping boundary.

## Safe local iteration

Local repository tests and disposable fixtures have no production access by default. Run affected tests, fix failures caused by the requested change, and rerun without asking for approval after each safe step.

If a failure is clearly external/transient (for example runner provisioning, registry outage, rate limit, or network outage), do not change product logic merely to hide it. Report or retry the appropriate layer when the task allows.

## Windows-specific evidence

Source inspection and hosted CI do not prove real Windows behavior that depends on other apps, OS permissions, desktop composition, DPI/monitors, OLE providers, audio/media sessions, notifications, startup registration, or shell integration.

When a task changes such behavior, record what was actually exercised. Keep unrun rows conditional rather than converting them into success statements.

## Network/browser evidence

Protocol/unit tests do not prove two-device discovery/pairing, real LAN topology, deployed backend behavior, browser File System Access support, or long-running resource health. Those claims require their own target evidence.

## Definition of done by task type

### Source implementation

Done means:

- requested behavior implemented
- affected validation passed or exact failures are reported
- regressions caused by the change were fixed
- durable docs/decisions updated when their contract changed
- repository state/PR reflects the intended change

A Preview release is not automatically required for source-only work.

### Release/deployment

Done means the requested public state is verified end to end. Read `release-delivery.md`; do not stop at a green local build, pushed commit, or created PR if the request was to ship.

### Website release-data change

Production synchronization must remain fail-closed, contract/security tests must pass, and live deployment must be verified when deployment is part of the task.

### Investigation/review

Done means the relevant evidence has been inspected and the conclusion identifies uncertainties. Do not mutate merely to make the investigation feel complete unless mutation was requested.

## Reporting

Report concrete identifiers/results when available:

- files/behavior changed
- test/build command or check and result
- branch/commit/PR
- CI run/status
- version/tag/Release and asset completeness
- Pages/site/API state
- physical/manual evidence performed
- exact residual risk or blocked layer

Avoid vague phrases such as “should work” when stronger evidence exists. Also avoid overstating missing evidence: an unrun physical acceptance row is a known verification gap, not proof that the implementation is broken.
