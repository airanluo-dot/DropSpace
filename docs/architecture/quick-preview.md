# Quick Preview architecture

Quick Preview is a bounded provider registry shared by inline cards and the full Preview dialog. Providers are ordered by priority and must probe before loading. A provider may read only the selected item or owned payload, never fetch remote URL metadata, and must accept a cancellation token.

The v0.3 provider set is:

- image: bounded raster bytes plus dimension/pixel checks;
- PDF: signature validation, bounded bytes, page-count metadata;
- text/code/JSON/Markdown: bounded UTF-8/UTF-16 decode with JSON pretty-printing;
- audio/video: metadata only, with autoplay disabled;
- URL: normalized local URL metadata only;
- unknown: a safe fallback with extension, size, and available-action metadata.

Descriptors are cached under the derived preview cache using item ID, revision, kind, page, and target width. Corrupt cache entries are deleted and regenerated. A cancelled load never becomes a cache hit. The UI renders only text/metadata in the first full-preview surface; opening a native file remains the explicit path for formats that are not safely rendered by the bounded provider.
