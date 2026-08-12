# Legacy Black Main runtime exports

These files preserve the complete previously active Black Main Windows export set and website exports exactly as they existed before the transparent Final logo refresh.

They are deliberately inactive:

- no application, installer, package manifest, website template, or release build references this directory;
- the active generator reads only `branding/master/transparent/DropSpace-Logo-Transparent-Final.png`;
- the original Black and White canonical packages remain under `branding/master/black/` and `branding/master/white/` for provenance and deterministic rollback.

Do not delete or silently reactivate these files. A future rollback must be an explicit branding decision with updated tests and documentation.
