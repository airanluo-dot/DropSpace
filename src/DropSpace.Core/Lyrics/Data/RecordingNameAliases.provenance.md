# Pinned recording name aliases

`RecordingNameAliases.json` contains name metadata extracted on 2026-10-28 from the
user-supplied Dynamic Notch 2.0.3 (build 176, `com.yico.dynamicnotch`) application.
The source resource is `Dynamic Notch.app/Contents/Resources/singerConfig.plist`,
configuration version 1: one singer (`Jay Chou` / `周杰伦`) and 153 song-name pairs.

- DMG SHA-256: `55cdd608c0a9f56a3395bc93361715fdd6d830066cb9b71ca34f80bb17b69335`
- Source plist SHA-256: `9b5e6ce98a45d4ab7c9bedda49f61cf64a4d96fff6cc0588668b42ea72af90a9`
- JSON SHA-256 (canonical UTF-8/LF): `1d8c9a511d40d86c3cc338397bf8ec05465be1d0f0ca3c09aa0db054fd5fa946`
- Extraction: Python `plistlib.loads`, then UTF-8 JSON with `ensure_ascii=False`,
  two-space indentation and a final LF. Values, including spelling in the source,
  are preserved; they are not corrected or extended by inference.

This is a bounded lookup aid, not an authoritative general translation catalogue.
It contains no song bodies, executable code, service configuration, URLs, access
tokens, credentials, or user account information. No Dynamic Notch network service
is contacted by the alias policy. The full application and its unrelated resources
are not shipped with DropSpace.

Lookup can use all 153 name pairs. Direct cross-language recording confirmation
additionally requires matching complete performer credits, compatible explicit
language/version labels and known compatible durations. Six pairs contain featured
performer decorations; those remain lookup-only because this resource does not
establish the featured performers' name equivalence. Ambiguous normalized title
collisions are excluded. Original media and provider metadata are never rewritten.

The matcher version is part of the lyrics cache identity; change it whenever this
catalogue or its identity semantics changes. Updating the source does not authorize
relaxing recording, language, artist or duration guards.
