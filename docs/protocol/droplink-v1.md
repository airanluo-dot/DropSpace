# DropLink protocol v1

DropLink is the Windows-to-Windows handoff protocol hardened in v0.3.0-preview.7. The protocol is HTTPS over the LAN, with a self-signed per-device ECDSA P-256 certificate pinned by SHA-256 fingerprint. The server certificate is not trusted through the machine store.

## Pairing

1. Each device creates a persistent DPAPI-protected identity certificate and a fresh ephemeral ECDH P-256 key.
2. `POST /v1/pairing/hello` exchanges protocol version, device ID, capabilities, certificate fingerprint, public key, and a random nonce. The offer is `Created → HelloExchanged → AwaitingLocalSasConfirmation` and expires after five minutes.
3. Both sides derive a 32-byte secret with ECDH + HKDF-SHA-256. The canonical transcript sorts both device IDs, then renders a six-digit SAS. The initiating UI and receiving UI independently confirm that same SAS for that session.
4. Confirmation transitions through `LocalConfirmed`/`RemoteConfirmed` and reaches `Trusted` only after both confirmations; `Rejected`, `Expired`, `Cancelled`, `Failed`, and SAS/transcript mismatch paths never persist the secret.
5. Only after `Trusted` does each side store the derived secret with Windows DPAPI under the peer device ID. A peer can be blocked or removed without touching source files.

## Authenticated requests

Every post-pairing request carries `X-DropLink-Device`, a fresh 24-byte base64 nonce, the lowercase SHA-256 body hash, and an HMAC-SHA-256 over:

```text
DropLink:v1\nMETHOD\nPATH\nNONCE\nBODY-SHA256
```

Nonces are single-use. Authentication, certificate pinning, protocol version checks, request-size limits, and manifest validation all fail closed.

## Transfer

The sender posts a manifest and waits for explicit receiver approval. Files are chunked at 4 MiB, hashed per chunk and again as a whole file, written into an application staging directory, then atomically moved below `%USERPROFILE%\Downloads\DropSpace`. Relative paths are normalized and cannot escape that root. The status endpoint exposes accepted chunk indexes, so a cancelled/reconnected sender can skip already durable chunks.

Folders are represented by their bounded relative file paths; reparse points are skipped and empty folders are not synthesized. Text and URL envelopes use the same authenticated channel; no source path or clipboard content is written to the protocol log.

### Explicit text/URL handoff

`POST /v1/handoff/text` carries a separate `HandoffMessage`: session ID, sender device ID/name, `Text` or `Url` kind, UTF-8 byte length, lowercase SHA-256, normalized payload, optional display label, and UTC creation time. Text is limited to 1 MiB and URLs to 32 KiB. HTTP(S) URLs have fragments removed before hashing and display; the receiver previews the content and must explicitly Accept or Reject. Acceptance adds a Space item only; it does not enter Clipboard History, mutate the Windows clipboard, launch a URL, or overwrite an existing session.

## Discovery and degraded mode

LAN discovery advertises `_dropspace._tcp.local` over mDNS/DNS-SD at `224.0.0.251:5353`. An unsolicited announcement has `QDCOUNT=0` and exactly four answer records: PTR to the instance, SRV to a host/port, TXT for the version/device/capabilities/fingerprint descriptor, and private IPv4 A. The parser is bounded, strict for known fields, ignores unknown TXT keys, rejects malformed/non-private/invalid data, and deduplicates by stable device ID. Discovery failure, an occupied multicast port, or an unconfirmed Windows Firewall state does not silently enable a broad firewall rule: the UI must show direct-pairing/manual-endpoint and firewall-degraded states. DropLink v1 accepts Windows peers only; macOS/iOS/iPadOS/Android/Linux clients are out of scope.
