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

The release sync reads public GitHub Releases at build time. A committed Stable fallback preserves working download links if GitHub's API is temporarily unavailable. CSS, JavaScript, brand assets, screenshots, and demo media receive content-hashed filenames so each Pages artifact is internally consistent.

GitHub Pages does not expose arbitrary response-header or cache-rule configuration. The build therefore uses a strict per-document CSP meta policy, immutable versioned asset URLs, static redirects, a custom 404 page, and an atomic Pages artifact deployment. Moving to a host with response-header controls would allow the same CSP to be enforced as an HTTP header.
