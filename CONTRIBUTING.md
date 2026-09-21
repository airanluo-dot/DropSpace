# Contributing to DropSpace

Thank you for contributing to DropSpace.

## License for contributions

DropSpace uses the ordinary Apache License 2.0 “inbound = outbound” model and does not currently require a Contributor License Agreement. Unless you explicitly mark a submission as “Not a Contribution” before it is incorporated, intentionally submitted contributions are provided under Apache-2.0, consistent with Section 5 of [LICENSE](LICENSE).

By submitting a contribution, you represent that you have the right to provide it under those terms. Preserve applicable copyright, attribution, patent, trademark, and license notices.

## Third-party material

- Do not copy source code, assets, documentation, or generated output from a third-party project unless its provenance and license have been verified and recorded.
- Identify the upstream source, exact license, and any required notices in the pull request and update [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) when applicable.
- Do not copy or mechanically translate GPL, proprietary, source-available, or otherwise incompatible code and present it as original DropSpace code.
- Dependencies should be added deliberately, with their license and redistribution impact reviewed separately from source incorporated into this repository.

Public APIs, documented platform behavior, and high-level interaction ideas may be studied and independently implemented. Do not copy an upstream project's expressive source, assets, constants, or distinctive control flow without a compatible license and attribution plan.

## Quality and scope

Keep pull requests focused. Add or update tests and documentation when behavior changes, preserve user files and privacy boundaries, and never commit signing keys, tokens, private certificates, or user data.

For Beta 26, keep real provider responses and native execution evidence distinct
from deterministic fixtures. On clean runners, run
`./scripts/Install-TestWindowsAppRuntime.ps1` after locked restore; the script
registers the Microsoft runtime MSIX packages selected by the existing lock file
and verifies them. Use `-InspectOnly` to check a workstation without installing.
Missing runtime fails with an HRESULT rather than opening a headless dialog.
App tests bootstrap their unpackaged Windows App SDK
host. Release validation checks actual executable versions, and upgrade lifecycle
tests use the real historical installer in an isolated account, preserving all
pre-existing user data and installation state. Synchronize the repository
maintainer Skill and the installed `dropspace-codex` counterpart when contracts
or validation procedures change.

For v0.3 network/preview changes, read the DropSpace maintainer skill and the contracts under `docs/protocol`, `docs/architecture`, and `docs/security`. Do not add a new platform client, public backend, firewall rule, telemetry field, or release claim without an explicit documented boundary and a fail-closed unavailable state.

## Beta release naming

The current target is `v0.3.0-beta.26` (Beta 26). All new prereleases use
`vMAJOR.MINOR.PATCH-beta.N`; the update channel is Beta. Historical Preview
releases remain immutable. Preview.23 requires one manual installation of
Beta 24, preserving data/settings, because its shipped parser rejects Beta
tags. Beta 26 is the Beta 25 release-candidate audit remediation; subsequent Beta
updates are automatic according to user settings.
See [migration contract](docs/dev/beta-migration.md).
