# Quick Preview architecture

Quick Preview is a bounded provider registry shared by inline cards and the full Preview dialog. Providers are ordered by priority and must probe before loading. A provider may read only the selected item or owned payload, never fetch remote URL metadata, and must accept a cancellation token.

The v0.3.0-preview.7 provider set is:

- image: bounded raster bytes plus dimension/pixel checks;
- PDF: signature validation and bounded bytes in the provider, followed by an App-owned `Windows.Data.Pdf` page load/raster with a 4-million-pixel/4,096-dimension/16 MiB output cap, page navigation, cancellation, corrupt fallback, and cache only after the first page renders successfully;
- text/code/JSON/Markdown: bounded UTF-8/UTF-16 decode with JSON pretty-printing;
- audio/video: metadata plus an explicit non-autoplay `MediaPlayerElement` surface when the source is available; transport controls are disposed when the dialog closes;
- URL: normalized local URL metadata only;
- unknown: a safe fallback with extension, size, and available-action metadata.

Descriptors are cached under the derived preview cache using item ID, revision, kind, page, and target width. Corrupt cache entries are deleted and regenerated. A cancelled load never becomes a cache hit. PDF descriptors bypass the provider cache until the native renderer successfully draws the first page. The UI never auto-plays media or launches a URL; opening a native file remains the explicit path for formats that are not safely rendered by the bounded provider.
