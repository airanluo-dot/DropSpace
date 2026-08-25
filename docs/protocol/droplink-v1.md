# DropLink protocol v1

DropLink is the platform-neutral Windows-to-Windows handoff protocol introduced in v0.3.0-preview.6. The protocol is HTTPS over the LAN, with a self-signed per-device ECDSA P-256 certificate pinned by SHA-256 fingerprint. The server certificate is not trusted through the machine store.

## Pairing

1. Each device creates a persistent DPAPI-protected identity certificate and a fresh ephemeral ECDH P-256 key.
2. `POST /v1/pairing/hello` exchanges protocol version, device ID, capabilities, certificate fingerprint, public key, and a random nonce.
3. Both sides derive a 32-byte secret with ECDH + HKDF-SHA-256. The transcript is rendered as a six-digit SAS; pairing is accepted only after the user confirms the same SAS.
4. The derived secret is stored with Windows DPAPI under the peer device ID. A peer can be blocked or removed without touching source files.

## Authenticated requests

Every post-pairing request carries `X-DropLink-Device`, a fresh 24-byte base64 nonce, the lowercase SHA-256 body hash, and an HMAC-SHA-256 over:

```text
DropLink:v1\nMETHOD\nPATH\nNONCE\nBODY-SHA256
```

Nonces are single-use. Authentication, certificate pinning, protocol version checks, request-size limits, and manifest validation all fail closed.

## Transfer

The sender posts a manifest and waits for explicit receiver approval. Files are chunked at 4 MiB, hashed per chunk and again as a whole file, written into an application staging directory, then atomically moved below `%USERPROFILE%\Downloads\DropSpace`. Relative paths are normalized and cannot escape that root. The status endpoint exposes accepted chunk indexes, so a cancelled/reconnected sender can skip already durable chunks.

Folders are represented by their bounded relative file paths; reparse points are skipped and empty folders are not synthesized. Text and URL envelopes use the same authenticated channel; no source path or clipboard content is written to the protocol log.

## Discovery and degraded mode

LAN discovery advertises `_dropspace._tcp.local` over mDNS/DNS-SD. Discovery failure, an occupied multicast port, or an unconfirmed Windows Firewall state does not silently enable a broad firewall rule: the UI must show direct-pairing/manual-endpoint and firewall-degraded states. DropLink v1 accepts Windows peers only; macOS/iOS/iPadOS/Android/Linux clients are out of scope.
