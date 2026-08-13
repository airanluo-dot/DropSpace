# DropSpace Website

Official bilingual static website for [DropSpace](https://github.com/airanluo-dot/DropSpace). Source and build tooling live in `website/_source`; generated production files exist only in the GitHub Pages deployment artifact and are never committed to `main`.

## Routes

- English: `/en/`
- 简体中文: `/zh-cn/`
- Localized changelog: `/<language>/changelog/`

These are independent generated HTML documents. The language switch is a normal link and does not replace strings at runtime.

## Build and verification

```bash
npm ci
npm run sync-releases
npm test
npx playwright install chromium
npm run test:browser
```

Production deployment is fail-closed: `npm run sync-releases` must fetch and validate the authoritative GitHub Releases response before the build can continue. Network, HTTP, JSON, contract, Stable-release, or required-asset failures exit non-zero, so GitHub Pages keeps the previous successful deployment. Production never falls back to the committed `data/releases.json`.

The committed `data/releases.json` is only a local-development and pull-request fixture. `npm test` validates it before building. An explicit offline validation can be run with `node scripts/sync-releases.mjs --fixture data/releases.json --validate-only`; fixture mode is rejected when `NODE_ENV=production`.

CSS, JavaScript, brand assets, screenshots, and demo media receive content-hashed filenames so each Pages artifact is internally consistent.

GitHub Pages does not expose arbitrary response-header or cache-rule configuration. The build therefore uses a strict per-document CSP meta policy, immutable versioned asset URLs, static redirects, a custom 404 page, and an atomic Pages artifact deployment. Moving to a host with response-header controls would allow the same CSP to be enforced as an HTTP header.
