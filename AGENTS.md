# DropSpace Agent Guide

These instructions apply to the repository. User and system instructions take precedence.

## Project skill

Use `.agents/skills/dropspace-maintainer/SKILL.md` as the project guide for DropSpace app,
website, updater, packaging, and release work. This repository copy is the only canonical
DropSpace skill. Do not create or synchronize a personal duplicate.

## Read the context the task needs

- Product behavior and terminology: `PRODUCT.md`, `FEATURES.md`, and `UX.md`.
- Visual language and interaction design: `DESIGN_SYSTEM.md`.
- Project layers and internal interfaces: `ARCHITECTURE.md` and the current source tree.
- Website structure and public release API: `website/_source/README.md`.
- Interface resources, shared language definitions, incremental translation confirmation and
  publication boundaries: `docs/dev/interface-localization.md`.
- For each app version, update the [shared release history](docs/dev/interface-localization.md#release-history) for the Island and website.
- Release history or a current milestone: its release notes, roadmap entry, or audit document.

Read only the relevant sections. Historical preview or beta notes describe their releases; they
are not standing instructions for unrelated work.

## Release comparison rule

For the app, website and GitHub, compare each Beta with the previous Beta on the same platform.
Compare each Stable release with the previous Stable on that platform and summarize the changes
users receive across the intervening prereleases. The first release in a channel introduces its
shipped features. See [release history](docs/dev/interface-localization.md#release-history).

## Working in the repository

- Treat the executable and worktree the user runs every day as their main copy, regardless of its
  version label or branch name. Start changes on a new branch in a separate Git worktree from the
  requested baseline. Build and run the local preview there. Keep the daily process running.
- Before the first interactive preview build in that worktree, create `.local/preview-id` with
  a fresh GUID in `N` format (32 hex digits). Keep that local file for subsequent builds. The
  executable uses its embedded ID for private data and process coordination, including on double-click.
  Use a clean checkout without the preview ID when preparing a public release.
- Infer routine details from the current code and the user's request. Ask only when a missing
  choice would materially change the result.
- Check the worktree and preserve unrelated user changes.
- Follow the existing C#/.NET/WinUI 3/MVVM structure and website tooling when extending them;
  use `ARCHITECTURE.md` to understand where a new interface or implementation belongs.
- Treat the skill and its references as tutorials and navigation, not as approval gates.
- Ordinary product changes do not require a skill edit. Revise the skill only when the user asks
  for skill maintenance or its durable project map has become materially inaccurate.

## Minimal tests and defensive code

Follow [the repository testing and development rules](docs/dev/passive-test-budget.md).
Start with zero functional tests. For a reproduced failure, choose one relevant scenario.
Use the existing boundary check before adding validation, retries or fallbacks.
[The core case list](TEST_PLAN.md) also gives the current product and maintenance rule sources.

## Completion report

Briefly report the outcome, the important files or surfaces changed, checks actually run, and
any remaining issue that affects the requested result. Do not report checks that were not run.
