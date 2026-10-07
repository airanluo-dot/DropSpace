# Isolated Linux runner compatibility validation

Source baseline: `00d07b3773dc614587b75fbcf1affd45a136535b` (main, 2026-10-07).

This temporary validation branch compares GitHub-hosted `ubuntu-24.04` and `ubuntu-26.04` against unchanged production command paths. It never builds Windows binaries, dispatches the release workflow, changes release assets or tags, edits the owner waiver, or deploys Pages.

Real services: checkout, npm cache, production release reads/synchronization, Pages configuration read, artifact upload/download/discovery, published metadata verification. Isolated services: OIDC and Pages deployment REST writes/status, and GitHub Release creation/upload/publication; pinned upstream bundled JavaScript runs through the Actions Node24/Node20 runtimes against loopback endpoints. Successful mocked publication is not live publication evidence.

The five production runner replacements are prepared as unreferenced Git blobs only. They are not automatically committed, merged, or activated. The final runner choice requires inspection of both matrix outcomes, exact build-output comparison, and any baseline-versus-new-image failures. This validation workflow and its helpers do not belong in the production patch.
