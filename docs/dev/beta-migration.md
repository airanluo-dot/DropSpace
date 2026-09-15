# Beta migration

Canonical target: `v0.3.0-beta.24`; display: Beta 24; channel: Beta.

Version sources: RELEASE_VERSION, Directory.Build.props, Core ReleaseVersion,
scripts/ReleaseVersion.ps1. Readers retain preview.N and beta.N spelling;
ordering uses numeric major/minor/patch and prerelease N, then Stable.
Package/file version is 0.3.0.24; Stable package revision remains 9999.

The UpdateChannel JSON converter accepts legacy numeric values and case-insensitive
Preview/preview, maps to Beta, and writes Beta. JsonSettingsService rewrites legacy
representations atomically even at the current schema version. Downloaded update
state and manifest readers accept historical channels. No database schema changes.

CI and publishing validate the current RELEASE_VERSION with
Assert-DropSpaceNewReleaseVersion, rejecting new Preview versions while allowing
historical fixtures and notes. Matching release-note name/title are required.

Preview.24 had no public tag or Release at the migration check. Published
Preview.1 through Preview.23 tags, notes and assets are untouched. The old
Preview.23 binary rejects beta tags; the user explicitly accepted one-time manual
Beta 24 installation, retaining data/settings. Thereafter Beta updates proceed
normally. Test coverage of the new parser does not alter that old-client limit.

Skill refactor follows the official article's concise discovery and progressive
reference loading: https://developers.openai.com/blog/rethinking-skills-and-prompts-for-gpt-6-astra
The entrypoint routes to release and Native Island references and defines the
real completion boundary without repeating generic coding recipes.

Validation checkpoint: Core 211 passed; Infrastructure 151 passed; App tests
40 passed. App build has zero warnings/errors; MSTest test-host PRI257/PRI263
satellite-resource warnings are confined to the test host. Website 27 passed;
release naming, release consistency, localization, Windows compatibility and
hardcoding gates passed. Personal Skill was absent locally; the reconstructed
counterpart is now installed at C:/Users/Razer/.codex/skills/dropspace-codex,
with both Skill folders passing quick_validate.py. Remote release and website
verification remain pending until publication.

September 15 validation: Core 212 passed; website 27 unit and 6 Chromium browser tests passed. Real PCM capture and volume output were retried on this desktop: 2 passed, 0 skipped. Hosted Windows runners without a render endpoint explicitly report these hardware cases Inconclusive; local hardware evidence remains required. Placement release retains a preview until Confirm, with Cancel/Escape restoring the original.
