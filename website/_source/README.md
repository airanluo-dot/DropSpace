# DropSpace Website

Official static website for [DropSpace](https://github.com/airanluo-dot/DropSpace), deployed with GitHub Pages.

## Local build

```bash
npm run sync-releases
npm test
npm run build
```

Release metadata is synchronized at build time from public GitHub Releases. A committed v0.1.0 fallback keeps downloads available if the API is temporarily unavailable.
