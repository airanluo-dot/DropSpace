# Website and release API

Use this reference for the public website, release metadata, updater feed, or the connection
between the website and the Windows app.

## Website layout

The website source is in `website/_source`:

```text
src/                         HTML, CSS, JavaScript, images, and localized page sources
src/locales/*.json           Ten complete interface-language resources
src/localization-runtime.js Shared-catalog language selection and DOM binding
scripts/i18n.mjs             Shared catalog/resource loading
scripts/build.mjs            Static-site and JSON-endpoint generator
scripts/sync-releases.mjs    GitHub Releases to local release-data conversion
scripts/release-contract.mjs Release and latest-change JSON schemas
data/releases.json           Local development fixture
package.json                 Available Node.js commands
```

The generated site provides one homepage at `/DropSpace/` and one changelog at
`/DropSpace/changelog/`, each with ten runtime interface languages and complete static English
content. `/en/`, `/zh-cn/` and their changelog documents are compatibility redirects, not canonical
content pages. Static showcase mode uses a unified `/index.html` and explicit legacy files.
Edit `website/_source`; generated deployment output is produced by the build script.

The shared catalog is `localization/languages.json`; resource IDs and reviewed source fingerprints
are checked by `scripts/check-localization.mjs` before website builds and artifact publication.
See `website/_source/README.md` for language matching, storage fallback, redirects, fixed/sample
exceptions and the focused verification workflow. Keep the global passive test budget intact.

Common local commands are:

```powershell
Set-Location website/_source
npm ci
npm run sync-releases
npm run build
```

Use `npm run sync-releases` when current GitHub Release metadata is needed. For layout or copy work
that should stay offline, the committed `data/releases.json` is the local fixture consumed by the
build.

## Public JSON endpoints

The static build generates two versioned endpoints:

- `https://airanluo-dot.github.io/DropSpace/api/v1/releases.json`
- `https://airanluo-dot.github.io/DropSpace/api/v1/latest-change.json`

`releases.json` is the updater-facing list of recent releases and their assets.
`latest-change.json` is presentation-oriented metadata for the latest release headline and
English-only highlights (the legacy locale fields both carry the same English body). Their schema and normalization logic live in
`website/_source/scripts/release-contract.mjs`; `build.mjs` writes the final JSON files.

The metadata flow is:

```text
GitHub Releases
  -> scripts/sync-releases.mjs
  -> data/releases.json
  -> scripts/build.mjs
  -> /api/v1/releases.json and /api/v1/latest-change.json
```

Release-specific website copy should normally come from release metadata. Evergreen product copy
belongs in the localized website source and `scripts/i18n.mjs`.

## How the Windows app consumes the release API

The update contracts are defined in
`src/DropSpace.Core/Abstractions/IUpdateServices.cs`:

- `IUpdateSource` reads release metadata and the selected update manifest.
- `IUpdateDownloader` downloads an update candidate.
- `IUpdateVerifier` and `ITrustedUpdateVerifier` inspect the downloaded artifact.
- `IUpdateInstallerLauncher` starts the installer path.
- `IUpdateService` coordinates user-visible update state and operations.

`src/DropSpace.Infrastructure/Updates/OfficialWebsiteReleaseUpdateSource.cs` implements
`IUpdateSource` for `/DropSpace/api/v1/releases.json`. GitHub Release support and source fallback
are in the same Infrastructure updates folder. `src/DropSpace.App/App.xaml.cs` constructs these
sources and registers the update service.

When changing the JSON interface, start with `release-contract.mjs`, then follow the corresponding
DTO mapping in `OfficialWebsiteReleaseUpdateSource.cs`. When changing app update behavior without
changing the wire format, start with `IUpdateServices.cs` and the relevant implementation under
`src/DropSpace.Infrastructure/Updates`.

## Other internal APIs

DropSpace's application APIs are C# interfaces rather than a general HTTP API. For other domains,
start in `src/DropSpace.Core/Abstractions` or the domain folder in Core, find the registration in
`App.xaml.cs`, and then follow its App or Infrastructure implementation. The
[App and UI guide](app-ui.md) lists the main interface locations.
