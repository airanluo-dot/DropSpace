# Corrective release preparation

Standard CI and release workflows only retrieve the exact reviewed native runtime.
Missing or inconsistent approval metadata fails with its specific error; neither workflow
may silently compile a new AI engine. Native engine changes require a separate explicit task.

For unchanged-model corrective Beta releases, first write the exact owner decision and
release notes, then use `scripts/prepare-corrective-release.mjs --version VERSION
--owner-decision REPOSITORY_PATH --accepted-at ACTUAL_UTC_TIMESTAMP` from the repository root.
The command updates RELEASE_VERSION, README/ROADMAP current identities, the exact-Beta gate,
both scope copies, userAcceptance.releaseVersion, the owner-decision reference and review hash.
It does not create owner authorization, rewrite historical model results, publish, or build.
It refuses already-current releases, missing evidence, future authorization timestamps,
and existing source-scope mismatches. Never edit publication fields piecemeal.

Changed or newly fingerprinted production inputs require an exact
`--reviewed-source-path REPOSITORY_PATH` for every reviewed file. The command
checks each path against the current code-owned fingerprint and binds its real
hash; it cannot remove a previously reviewed input. A fingerprint algorithm change
also requires review of the gate itself. Preparation hashes the exact gate text
that it will write, including the target-version pin. It creates a new review
record and leaves historical acceptance and model observations unchanged.

A new frozen host-admission computation additionally requires
`--reviewed-fixture-admission EXACT_CURRENT_PATH`, with that path, the admission
policy and gate each explicitly listed as reviewed source inputs. The independent
fixture must be unchanged. Only a computation explicitly marked as no model
execution and no semantic approval can be renewed. Runtime, model, prompt or
other scope changes still fail closed; this option does not renew historical
model results.

A workflow-only maintenance PR cannot produce or promote release binaries. Its required
jobs record a no-application-change outcome, not a functional test pass. Source/package
changes continue through the ordinary Windows producer. Beta6's owner test waiver is exact-version only.
