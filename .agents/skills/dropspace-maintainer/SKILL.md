---
name: dropspace-maintainer
description: Guide DropSpace app, UI, internal-interface, website, updater, packaging, and release work in the DropSpace repository.
---

# DropSpace project guide

Use this skill as a compact map of the project. The user's request defines the task and takes
precedence over this guide. Prefer the current implementation and focused project documentation
over historical release notes.

## Product and stack

DropSpace is a Windows workspace for temporarily holding files and recently copied content so
people can find, reuse, and move items before deciding where they belong. Its main surfaces are
the native WinUI app, the Dynamic Island quick-access experience, and the bilingual static
website.

The application uses C#, .NET, WinUI 3, Windows App SDK, MVVM, SQLite, and dependency injection.
The website is generated from `website/_source` with Node.js scripts and published as static
pages and JSON endpoints.

## Choose the relevant guide

- For product wording or feature behavior, read only the relevant part of `PRODUCT.md`,
  `FEATURES.md`, or `UX.md`.
- For UI work, read `DESIGN_SYSTEM.md` and [App and UI guide](references/app-ui.md).
- For application layers, service contracts, or dependency injection, read `ARCHITECTURE.md`
  and [App and UI guide](references/app-ui.md).
- For the website, update feed, release JSON, or updater connection, read
  `website/_source/README.md` and [Website and release API](references/website-api.md).
- For packaging or publication, inspect `RELEASE_VERSION`, the current workflow files, and the
  scripts involved in the requested operation. Do not infer current release state from this skill.

Read only the route needed for the task. The linked references explain where things live and how
they connect; they do not impose a fixed implementation recipe.

## Keeping this guide useful

This skill contains durable orientation only. Version numbers, beta status, individual bug fixes,
test counts, temporary acceptance criteria, and one-off release authorization belong in release
notes, audits, or roadmap documents.

Ordinary product work does not require updating this skill. Update it only when explicitly asked
or when its long-lived project navigation, design language, or interface map is no longer accurate.
There is no personal counterpart and no synchronization gate.

## Completion report

Lead with the result. Then briefly name the important files or surfaces changed, the checks that
were actually run, and any remaining issue that affects the user's requested outcome.
