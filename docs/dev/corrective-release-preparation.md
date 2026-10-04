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

A workflow-only maintenance PR cannot produce or promote release binaries. Its required
jobs record a no-application-change outcome, not a functional test pass. Source/package
changes continue through the ordinary Windows producer. Beta6's owner test waiver is exact-version only.
