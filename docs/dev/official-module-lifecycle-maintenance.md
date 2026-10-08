# Official module lifecycle maintenance

Baseline: main 461be3d10b72b6ae1b872643fd6d00541e794ef8 (PR #122).
This maintenance does not publish an App version or alter any existing Release assets.

## Confirmed source paths

- Uninstall wrote Cleaning with Version == CandidateVersion before removing files. Startup/retry removed CandidateVersion but retained Version. A crash in that interval could resurrect a missing installation.
- Initialization registered, validated and activated one journal at a time. Dependency results depended on enumeration order.
- Process.Start preceded job assignment. A child created in that interval was not covered by later assignment; stop checked only the root process.
- Optional capability validation and HasCapability accepted nonpositive versions.
- Per-slot gates allowed an install's dependency snapshot and another module's uninstall to overlap.

## Combined invariants

Removal intent (Version=null, Enabled=false) is durable before deletion. Legacy Cleaning/PendingCleanup records with CandidateVersion == Version are normalized before deleting anything. Update/rollback cleanup with a different candidate retains its committed version. Staging failure retains the cleanup journal.

All journals recover and all installed package inventories are read before dependency activation. Dependency-first traversal rejects cycles and excludes faulted/unverified dependencies from validation. The runtime serializes graph mutations through validation, activation, durable commit, rollback and cleanup. An uninstall queued during a dependency handshake rechecks the graph before retirement; an incompatible dependency update is rejected before switching workers.

Workers are created with STARTUPINFOEX JOB_LIST and a restricted HANDLE_LIST. The job is attached by CreateProcess itself. The primary thread stays suspended until stream/process ownership is ready; no post-start assignment window exists. Stop terminates the job even after root exit, and succeeds only after root exit and ActiveProcesses==0. Job isolation retains the existing trusted-code model and is not a permissions sandbox.

No record/schema/interface version, production catalog, module resource, or user-data deletion policy is changed.

## Focused execution

Run scripts/feature-module-probe/Run-LifecycleChecks.ps1 in a clean Windows checkout with .NET 10. It builds an isolated worker, ZIPs and an independently embedded test-only catalog, then exercises linked production runtime/storage/IPC types.

| Scenario | Functional cases | Boundary |
| --- | ---: | --- |
| uninstall-crash | 1 | Legacy journal after directory deletion, durable restart and retained data |
| cleanup-retry | 1 | Locked legacy uninstall, release and explicit retry |
| update-cleanup | 1 | Retired version cleanup preserves current verified package |
| capabilities | 4 | Zero, negative, unsupported omit, supported v1 |
| dependency-order | 1 | A journal before B, dependency-first startup |
| dependency-corrupt | 1 | Corrupt B prevents A activation |
| dependency-cycle | 1 | Two-node cycle starts no worker |
| dependency-race | 2 | Uninstall during A handshake, incompatible B update |
| job-stop | 1 | Child spawned before hello, graceful root stop, child lock release |
| job-parent-crash | 1 | Root crash with live child, job-wide stop/lock release |

Total: 10 scenarios / 14 functional cases. Each writes a separate JSON receipt even on failure. CI runs this selection only for affected module/probe paths. It does not dispatch full regression/model/browser matrices.

Source inspection establishes the original paths and intended state invariants. Windows CI probe results establish only the recorded automated scenarios. The probe does not establish installed App navigation/UI behavior, real user-machine shutdown, abrupt host-power loss, every OS build/nested-job environment, or public module download provenance. No user-machine App verification has been performed by this change.
