# Round 5 — managed module inventory and recovery

Only the newly added feature-module probe **case 6** ran, **once and passed**, after the
runtime implementation and independent review were complete. No historical cases ran and
there were no failed attempts or retries. The parent iteration ledger recorded the planned
single invocation before execution.

One locked restore used **only the existing local package-cache source**, followed by one
necessary probe build/run. The run returned exit code 0 and emitted no stdout/stderr;
`module-inventory-probe.log.txt` is therefore intentionally empty. Do not interpret it as a
reconstructed build log. The probe's actual JSON receipt is copied verbatim to
`module-inventory-result.json`; it records `passed: true`, one functional scenario and the
10 assertions, with about **173 ms** between its scenario timestamps. The raw restore output
is `module-inventory-restore.log.txt`. Input and assembly hashes are recorded separately.

The case links the actual production runtime, store, catalog, worker ownership code and
Infrastructure project. It writes only new fixtures under its required fresh probe artifact
root. Archive bytes, extracted files, receipts and manifests are checked by the actual store.
It first reads the actual state enumeration, then makes the earlier enumerated module depend
on the later one, avoiding assumptions about filesystem enumeration order.

The executed assertions cover:

- Dependent-first discovery accepts a verified, installed **disabled** dependency.
- A real interrupted uninstall removes its package, clears the version in memory and in the
  persisted journal, and preserves the module's scoped data.
- The removed module does not satisfy another module's dependency; a genuinely absent
  dependency also remains rejected.
- A temporary-store semaphore blocks initial inventory while `DisableAsync` is called.
  The public mutation waits, and initialization retains one authoritative slot.
- The runtime owns no workers, active calls or expiry timer.

**All five fixtures were disabled (`Enabled=false`).** This run does not establish suppression
of an enabled module during startup, immediate retirement of an already running worker,
physical worker termination, handshake, Windows compatibility, rendering or live module IPC.
Those changed startup-generation branches were reviewed statically, not functionally run.

The new managed-only case allows Linux at the probe entry point and explicitly sets fixture
`MinimumWindowsBuild=0`. This is a legal synthetic manifest value; production compatibility
checks were unchanged and the Windows requirement for cases 1–5 remains. Inert `.exe` bytes
are never launched. No network request, downloader, installation action, official package
creation, release output or user data modification was part of the scenario. Synthetic
canonical package URLs are not evidence of official publisher identity or public downloads.

Commands used .NET SDK 10.0.401 at its previously recorded local path:

```sh
dotnet restore scripts/feature-module-probe/FeatureModuleProbe.csproj --locked-mode --source /home/agent/.nuget/packages/ --ignore-failed-sources --nologo
dotnet run --project scripts/feature-module-probe/FeatureModuleProbe.csproj --no-restore --no-launch-profile -- --case 6 --root /workspace/DropSpace/scripts/feature-module-probe/artifacts/round5-inventory-20261013 --evidence /workspace/DropSpace/scripts/feature-module-probe/artifacts/round5-inventory-20261013.json
```
