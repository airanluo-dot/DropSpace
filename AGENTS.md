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
- Release history or a current milestone: its release notes, roadmap entry, or audit document.

Read only the relevant sections. Historical preview or beta notes describe their releases; they
are not standing instructions for unrelated work.

## Working in the repository

- Infer routine details from the current code and the user's request. Ask only when a missing
  choice would materially change the result.
- Check the worktree and preserve unrelated user changes.
- Follow the existing C#/.NET/WinUI 3/MVVM structure and website tooling when extending them;
  use `ARCHITECTURE.md` to understand where a new interface or implementation belongs.
- Treat the skill and its references as tutorials and navigation, not as approval gates.
- Ordinary product changes do not require a skill edit. Revise the skill only when the user asks
  for skill maintenance or its durable project map has become materially inaccurate.

## Completion report

Briefly report the outcome, the important files or surfaces changed, checks actually run, and
any remaining issue that affects the requested result. Do not report checks that were not run.
