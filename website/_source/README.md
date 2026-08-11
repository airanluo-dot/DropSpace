# DropSpace Website

Official bilingual static website for [DropSpace](https://github.com/airanluo-dot/DropSpace). The deployable files live in the Windows App repository's `website/` directory and are served through jsDelivr's global CDN. The public entry point is `website/index.xhtml`; XHTML preserves the correct browser content type on the CDN.

## Local build

```bash
npm run sync-releases
npm test
npm run build
```

Release metadata is synchronized at build time from public GitHub Releases. A committed v0.1.0 fallback keeps downloads available if the API is temporarily unavailable.

After publishing an update to `main`, purge the changed `website/` URLs through jsDelivr so the branch-backed public URL refreshes immediately.
