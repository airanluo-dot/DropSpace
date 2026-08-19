---
name: dropspace-maintainer
description: Maintain, debug, refactor, test, release, deploy, or validate the DropSpace repository end to end. Use for implementation work, bug fixes, Windows integration, UI/UX, persistence, website/API, updater, packaging, CI, releases, and repository maintenance. Start from fresh repository state; do not rely on prior chat context, remembered versions, old paths, or historical assumptions.
---

# DropSpace Maintainer

Use this skill as the execution playbook for end-to-end DropSpace repository work. It is intentionally version-independent and context-independent: every run must rediscover the current repository state before acting.

## 1. Authority and freshness

Follow instructions in this order:

1. System and explicit user instructions.
2. The nearest applicable `AGENTS.md` files.
3. Current repository code, tests, workflows, configuration, and accepted decision records.
4. Current product, architecture, UX, privacy, data-model, Windows-integration, roadmap, and contribution documentation.
5. This skill.
6. Historical releases, old PRs, comments, and prior chat context.

Never treat memory, a previous Codex run, an old prompt, a release number mentioned in chat, or a historical file path as current truth.

Before meaningful work, re-read the repository state from disk and, when the task involves GitHub state, releases, deployment, CI, issues, or PRs, query the current remote state as well.

If current code and documentation disagree, investigate which is authoritative before changing behavior. Do not silently choose the more convenient source.

## 2. Bootstrap every task from the repository

At the beginning of a task, establish a compact current-state snapshot.

### Local repository state

Determine:

- repository root;
- current branch and HEAD;
- default remote and repository identity;
- worktree status, including untracked and unrelated user changes;
- applicable `AGENTS.md` files from root to the files being changed;
- recent commits relevant to the requested area.

Preserve unrelated user changes. Never use broad destructive cleanup, reset, checkout, or staging commands to make the tree look clean.

### Project guidance

Read the root `AGENTS.md` first when present. Then read the documents relevant to the requested surface. In DropSpace these commonly include, when present:

- `PRODUCT.md`
- `ARCHITECTURE.md`
- `DECISIONS.md`
- `ROADMAP.md`
- `FEATURES.md`
- `UX.md`
- `DESIGN_SYSTEM.md`
- `DATA_MODEL.md`
- `PRIVACY.md`
- `WINDOWS_INTEGRATION.md`
- `EDGE_CASES.md`
- `CONTRIBUTING.md`
- branding or release-specific guidance

Do not read every large document mechanically when the task is narrow. Read enough to understand the current contract, then expand when the change crosses boundaries.

### Build and automation discovery

Discover, rather than assume:

- solution/project files;
- central package/version files;
- build scripts;
- test projects and test scripts;
- installer/package/signing scripts;
- website source and package scripts;
- GitHub Actions workflows;
- release-note conventions;
- canonical version source;
- release artifact names;
- update metadata contracts;
- official website/update endpoints;
- CI artifact retention and deployment behavior.

If a well-known file from an older revision no longer exists, search for its replacement instead of recreating the old architecture from memory.

## 3. Classify the task before editing

Identify all affected surfaces. A single request may cross several:

- product behavior and core policy;
- WinUI/UI/UX and accessibility;
- Windows/Win32 integration;
- drag/drop, clipboard, tray, startup, windowing, display, DPI, focus, or shell behavior;
- persistence, migrations, retention, search, payload storage, or recovery;
- privacy, logging, diagnostics, or network boundaries;
- updater and release discovery;
- installer, MSIX, portable build, identity, signing, or upgrade lifecycle;
- website, release metadata API, localization, browser behavior, or deployment;
- CI/CD, repository automation, artifact retention, or release publication;
- documentation, decisions, and tests.

Build a small impact map before editing. Changes to a boundary often require tests and documentation in another layer.

## 4. Non-negotiable product and engineering invariants

Treat the current `AGENTS.md` and accepted decisions as the detailed source of truth. In particular, preserve these principles unless the user explicitly authorizes a product/architecture change and the repository records that change:

- DropSpace is local-first. Do not add telemetry, accounts, content upload, or cloud dependencies casually.
- Removing a DropSpace record must never delete, move, or mutate the referenced source file.
- Clipboard and drag/file inputs are untrusted. Bound time, bytes, pixels, lengths, retries, queues, and concurrency.
- Clipboard capture must remain event-driven; do not introduce polling to simulate reliability.
- Do not put direct filesystem, database, clipboard, shell, or platform access into Views/ViewModels when repository boundaries provide adapters/services.
- Do not block the UI thread with file, thumbnail, database, image, clipboard, or network work.
- Do not claim a feature works by adding placeholders, fake data, no-op handlers, swallowed exceptions, arbitrary sleeps, or disabled behavior.
- Do not weaken security or integrity validation merely to make an update, installer, website, or test path pass.
- Accessibility, keyboard behavior, focus, high contrast, reduced motion, DPI, mixed-display behavior, and honest unsupported states are acceptance criteria.
- Historical releases and user data are not disposable implementation details.

When a requested change conflicts with an accepted invariant, surface the conflict and update the relevant decision/documentation as part of the same work if the user has authorized the change.

## 5. Investigate before fixing

For bugs and regressions:

1. Reproduce or identify the failing state from logs, tests, code paths, CI output, or a minimal deterministic scenario.
2. Trace the full path from input/event to state mutation to UI/output.
3. Identify the root cause, not only the visible symptom.
4. Check neighboring cases that share the same mechanism.
5. Add a regression test when practical before or with the fix.
6. Fix the smallest complete layer that owns the bug.
7. Re-run the focused test, then the broader tests required by the affected boundary.

Do not “fix” a failure by removing a feature, relaxing validation, ignoring an exception, skipping a test, or hard-coding the observed example unless that behavior is the explicit product decision.

For Windows behavior that depends on Explorer, the shell, another app, COM, OLE, UI Automation, window styles, DPI, or OS version behavior, prefer official Microsoft documentation plus an executable spike/integration test when uncertainty remains. Clearly distinguish supported, best-effort, limited, and unsupported behavior.

## 6. Implementation discipline

Prefer the smallest coherent change that fully satisfies the request.

- Reuse established patterns and boundaries.
- Keep platform-specific APIs behind narrow adapters.
- Avoid speculative abstractions and broad rewrites unrelated to the task.
- Keep asynchronous cancellation, lifetime, and ownership explicit.
- Preserve state-machine invariants and make transitions testable.
- Use typed configuration, design tokens, policies, or constants instead of scattering magic values.
- Maintain deterministic generation for generated branding, manifests, package metadata, or release outputs when the repository expects it.
- Keep comments focused on non-obvious rationale, platform constraints, and invariants.

When adding a dependency, document why it is needed, its maintenance/security cost, the built-in alternative, and why existing dependencies are insufficient. Pin versions according to repository policy rather than choosing “latest” by habit.

## 7. Data, migrations, and recovery

Any persistent schema or stored-format change must be treated as an upgrade problem, not only a fresh-install problem.

- Add a numbered migration or the repository’s current equivalent.
- Test migration from every still-supported prior schema/fixture.
- Preserve the original data on migration failure.
- Never silently replace a corrupt or failed database with an empty one.
- Verify payload containment and path safety.
- Verify retention, clear-history, search projections, thumbnails, backups, and app-owned payload cleanup remain consistent.
- Keep sensitive clipboard content, secrets, raw payloads, full paths, and URL query strings out of diagnostics unless explicitly required and safely redacted.

For destructive data operations, test interruption and partial-failure behavior.

## 8. UI and UX changes

Before changing user-visible UI, read the current design and UX guidance.

Validate at least the relevant subset of:

- normal, empty, loading, error, missing, and unsupported states;
- keyboard-only operation;
- focus order and focus restoration;
- accessible names and UI Automation exposure;
- high contrast and theme behavior;
- reduced-motion behavior;
- text scaling;
- single- and mixed-DPI displays;
- compact/expanded/hidden lifecycle where applicable;
- pointer drag and drop targets;
- no accidental taskbar/Alt-Tab/focus behavior for auxiliary surfaces.

Do not convert the product into a generic web-dashboard visual style or hide core actions behind hover-only affordances.

## 9. Website and public metadata

Treat the live website and release metadata as production surfaces.

When the website derives release/download information from GitHub Releases or another current canonical release source:

- discover the current source and schema from code/workflows;
- keep production metadata generation fail-closed if that is the accepted repository decision;
- never silently deploy stale fixture data as fresh production metadata;
- validate provenance, release identity, asset identity, URL allow-lists, and required fields before deployment;
- preserve atomic deployment semantics when available so a failed build leaves the previous known-good site live;
- keep local/test fixtures explicitly separated from production authority;
- validate generated static output and browser behavior;
- verify localization routes, canonical links, download links, changelog rendering, and release selectors when affected.

Do not reintroduce a retired hosting path, proxy, API implementation, or domain merely because it appears in historical commits or release notes.

## 10. Updater and executable-delivery security

Changes to update discovery, manifests, download URLs, checksums, signatures, installers, or package identity are security-sensitive.

Before changing them, map the complete trust chain:

release authority -> release metadata -> manifest -> asset identity -> download -> integrity/signature validation -> installer/package execution.

Preserve or strengthen:

- strict origin/repository/release matching;
- same-release asset identity checks where the current contract requires them;
- schema/version validation;
- bounded metadata sizes/counts;
- checksum verification;
- signature/publisher verification when configured;
- safe temporary paths and atomic replacement/installer ownership;
- stable/preview channel semantics;
- downgrade prevention and deterministic version ordering.

Never allow remote metadata to point the updater at arbitrary executable content without an explicit, reviewed architecture change.

## 11. Testing strategy by change surface

Run tests proportional to the actual change, not a ritual fixed list. Discover current commands from CI and scripts before executing them.

Typical expectations:

### Core/policy changes

- focused unit tests;
- all relevant core tests;
- application build when public interfaces or composition changed.

### Persistence/infrastructure changes

- focused integration tests;
- migration/recovery tests;
- persistence/infrastructure suite;
- application build.

### Windows integration changes

- unit/policy coverage where possible;
- application build;
- targeted executable/manual integration matrix on real Windows for behaviors that mocks cannot prove.

### Installer/package/update changes

- version/release validation;
- portable/package/installer build paths;
- upgrade, restart, uninstall, identity, checksum/manifest, and signing verification relevant to the change;
- test both signing-enabled and signing-unavailable assumptions when possible without pretending credentials exist.

### Website changes

- package install/lockfile consistency;
- unit/contract tests;
- static build;
- real-browser tests when supported;
- production-mode metadata sync behavior when release data is affected.

### Workflow-only changes

- syntax/config review;
- local script-equivalent validation where possible;
- inspect the resulting GitHub Actions run after push/PR.

Every bug fix should get regression coverage when practical. Do not mark work complete while required tests or acceptance criteria still fail.

## 12. CI and GitHub Actions handling

After pushing changes, inspect the checks that actually cover the changed surfaces.

If CI fails:

1. Read the failing job and step logs.
2. Distinguish code failure, environment/toolchain failure, credentials/signing limitation, flaky external dependency, and GitHub infrastructure failure.
3. Fix deterministic repository failures at the root cause.
4. Re-run only when the failure is plausibly transient or after a fix.
5. Do not repeatedly rerun the same deterministic failure hoping it turns green.

Do not use `continue-on-error`, `|| true`, swallowed exceptions, broad test exclusions, or disabled validations to obtain a green check unless failure tolerance is the explicit design of that step.

For noncanonical CI artifacts, prefer explicit short retention when consistent with debugging needs. Before deleting or shortening retention, verify the artifacts are not release inputs, signing handoffs, audit evidence, or the only copy of a user-facing deliverable. GitHub Release assets and historical public releases must not be treated as disposable Actions artifacts.

## 13. Versioning and release procedure

Never assume the current version or next version from memory.

When the task is intended to ship:

1. Discover the repository’s canonical version source and accepted version grammar.
2. Query existing tags/releases before choosing a new version.
3. Follow current Stable/Preview/channel policy.
4. Never overwrite or reuse an existing release tag.
5. Create/update the release notes required by the current workflow.
6. Ensure build, package, update metadata, and website outputs derive from the canonical version rather than independent manual edits.
7. Let the current release workflow produce final public bytes when that is the repository contract.
8. Verify required release assets, checksums/manifests, prerelease/latest flags, and target commit after publication.
9. Verify the official website/deployment and public release API if the release workflow is expected to refresh them.
10. Verify the application’s update path can discover the newly published release according to the selected channel.

Do not publish Stable merely because a task is complete. Stable requires explicit user intent or an already-authorized release plan. For user-facing changes intended for testing, use the repository’s current Preview policy unless the user says not to release. Documentation-only, skill-only, or internal CI maintenance does not require a version bump unless current repository policy or the user explicitly requires one.

If signing credentials are absent, do not claim artifacts are signed. Follow the repository’s supported unsigned Preview/test path if one exists, and report the limitation precisely.

## 14. Release and deployment verification

A release is not complete when the workflow merely starts.

Verify from fresh remote state:

- the intended commit is on the target branch;
- the release/tag exists and targets the intended commit;
- prerelease/latest/channel flags are correct;
- all expected public assets exist and have plausible nonzero sizes;
- checksums/manifests refer to the final published bytes;
- signing state matches what the workflow actually performed;
- release notes are correct;
- release-related Actions jobs succeeded;
- website deployment succeeded when applicable;
- the live public metadata contains the new release when applicable;
- download links resolve to the intended official release assets;
- the updater’s current contract accepts the release.

Do not infer live deployment success solely from repository source changes.

## 15. Pull requests, branches, and commits

Unless the user explicitly requests direct-main work and repository policy allows it:

- branch from fresh default-branch state;
- use a focused branch name;
- commit only intended files;
- keep unrelated worktree changes untouched;
- open a PR with what changed, why, user impact, tests, risks, and release implications;
- inspect the final diff before merge;
- merge only when required checks and requested validation pass.

Never force-push, rewrite shared history, delete remote branches/tags/releases, or close unrelated work without explicit authorization.

When a task spans code plus release, separate “source merged” from “release published” in status reporting.

## 16. Deletion and cleanup rules

Before deleting code, configuration, assets, workflows, API endpoints, or documentation:

1. Search the entire repository for references.
2. Check build scripts, tests, packaging, deployment, release workflows, and documentation.
3. Check whether the item is used only dynamically or by convention.
4. Check current remote deployment/release dependencies when relevant.
5. Delete it only after its replacement/current authority is proven.
6. Remove empty directories and stale references where appropriate.
7. Add regression coverage if the deletion closes a previously dangerous fallback or duplicate path.

Do not delete historical releases, tags, public release assets, migrations, compatibility fixtures, signing inputs, or user data merely because the current code no longer reads them. Their retention policy must be established first.

## 17. Rollback and recovery

Prefer forward fixes or explicit revert commits/PRs over history rewriting.

If a deployment fails, preserve the last known-good deployment whenever the hosting system supports atomic deployment. If a release is partially published, determine exactly which public state exists before retrying; do not blindly recreate tags or upload conflicting bytes.

If a migration, installer upgrade, or updater fails, prioritize preserving user data and the previously runnable application state over forcing completion.

When an external service, GitHub, package feed, signing service, or official API is unavailable, fail clearly at trust boundaries instead of substituting stale or unverified data unless the repository explicitly defines a safe offline mode.

## 18. When to ask versus when to proceed

Proceed without unnecessary clarification when the user’s goal is clear and the repository can resolve implementation details.

Ask or stop only when a decision cannot be safely inferred, such as:

- mutually exclusive product behaviors with material user impact;
- destructive deletion of public history or user data;
- a new privacy/network/account boundary not authorized by the request;
- unavailable credentials/permissions that are strictly required for the requested final state;
- an architecture choice that conflicts with an accepted decision and the user did not authorize changing it.

Otherwise make the safest repository-consistent choice, implement it, validate it, and report any bounded assumption.

## 19. Mandatory Skill synchronization gate

Treat Skill freshness as part of every mutating DropSpace workflow, not as optional follow-up documentation.

Before declaring an App, website, API, updater, packaging, CI/CD, release, documentation, or repository change complete:

1. Compare the completed change with both DropSpace maintenance Skills:
   - the repository copy at `.agents/skills/dropspace-maintainer/SKILL.md`;
   - the installed personal Skill whose frontmatter name is `dropspace-codex`, resolved from the personal-skills checkout rather than from a remembered internal directory name.
2. Update both Skills in the same task whenever the change affects product facts, architecture, invariants, paths, commands, API contracts, test expectations, release/deployment procedures, live-verification rules, or the definition of done.
3. Keep durable workflow knowledge in the Skills. Rediscover volatile version numbers and live state unless a current implementation snapshot is intentionally documented and updated with the release.
4. Validate both Skill folders after editing. Publish the repository copy through the normal DropSpace branch/PR/check/merge flow, save the installed Skill through its supported personal-skills workflow, and verify both remote results.
5. If neither Skill needs a semantic edit, explicitly report `Skill sync: verified current` in the completion report. The synchronization check is mandatory even when the outcome is no file change.
6. If either Skill changes, apply this same gate recursively: inspect and synchronize its counterpart before finishing. Updating only one copy is incomplete.
7. If one destination cannot be updated or verified, report the exact blocked destination and do not describe the overall update or release as fully complete.

Skill-only maintenance does not require an App version bump or Preview release unless the user or current repository policy explicitly requests one.

## 20. Completion gate

Before declaring success, perform a final pass:

- re-read the diff;
- confirm only intended files changed;
- confirm no temporary/debug secrets, generated junk, or local paths were committed;
- run the required focused and broader tests;
- verify documentation/decision updates required by the change;
- inspect relevant CI results;
- verify release/deployment/live state if the task includes shipping;
- verify known edge cases adjacent to the changed mechanism;
- identify remaining limitations honestly.

Do not claim “done” when the repository is only locally modified, when CI is still deterministically failing, when a requested release is not published, or when a requested live deployment has not been verified.

## 21. Final report format

Return a concise, evidence-based completion report with these sections when applicable:

- **Result** — what is now true.
- **Changed** — important files/components and behavior changes.
- **Root cause** — for bug fixes.
- **Validation** — exact tests/builds/manual checks and their results.
- **GitHub** — branch, commit, PR, merge state, and relevant CI runs.
- **Release** — version/channel/tag/assets/signing state when shipped.
- **Website/API** — deployment and live metadata verification when affected.
- **Skill sync** — both Skill copies updated and verified, or explicitly verified current with no edit required.
- **Risks / limitations** — remaining bounded issues, unsupported cases, or external blockers.

Report concrete failures rather than vague statements such as “permissions issue.” State the exact unavailable capability, what was still completed, and what final state could not be verified.
