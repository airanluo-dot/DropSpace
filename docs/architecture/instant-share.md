# Instant Share architecture

## Nearby browser share

`NearbyShareServer` binds an ephemeral HTTP port and advertises only a private IPv4 address. Each share has a 24-byte random token, a ten-minute default TTL, a two-receiver default cap, no directory listing, revoke support, and bounded HTTP range reads. The token is checked in constant time and expired shares are removed before content is returned.

## Encrypted Internet Share

The desktop creates a random 32-byte master key, derives per-file AES-256-GCM keys with HKDF-SHA-256, encrypts the manifest and 5 MiB chunks, and uploads only ciphertext. The canonical wire format is `12-byte manifest nonce | manifest ciphertext | 16-byte tag` and `chunk ciphertext | 16-byte tag`; HKDF salt uses explicit .NET Guid byte order and AAD includes share/file/index/plain-length. The key is placed in the URL fragment, not the request path/query. The browser receiver uses WebCrypto to decrypt and SHA-256 verify each file locally. The reference Cloudflare Worker stores opaque R2 objects, applies TTL/size/authentication limits, and never receives the fragment key.

The desktop retains the authenticated backend session and exposes an explicit revoke action. Revocation derives `DELETE /v1/shares/{shareId}` from the known HTTPS backend origin and share ID; it never follows an arbitrary returned revoke URL or puts the fragment key in a query. The Worker is source-only until an operator configures an R2 bucket, secret, domain, rate limits, lifecycle rule, and privacy/logging policy. No release note or UI state may claim that Internet Share is live when `DROPSPACE_SHARE_BACKEND_URL` is not configured.
