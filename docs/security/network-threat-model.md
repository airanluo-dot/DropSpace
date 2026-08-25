# Network threat model for v0.3.0-preview.6

## Assets

Clipboard text/images, selected source files, peer identity keys, pairing secrets, share ciphertext, and the receive destination are sensitive. Source paths and URL fragments are secrets in practice even when ciphertext is present.

## Threats and controls

| Threat | Control |
| --- | --- |
| LAN observer reads handoff | HTTPS with a pinned per-device certificate and HMAC request authentication |
| Rogue device pairs | ECDH transcript SAS and explicit user confirmation |
| Replay | Single-use nonces and bounded session expiry |
| Path traversal / overwrite | Relative-path normalization, staging, destination containment, atomic move, no source mutation |
| Malicious oversized input | Manifest, chunk, preview, clipboard, receiver, and object limits |
| Clipboard echo loop | 10,000-entry/24-hour content-hash LRU plus existing self-write suppression |
| Share backend sees plaintext/key | AES-256-GCM client-side encryption; key only in URL fragment |
| Leaked nearby link | 192-bit token, short TTL, receiver cap, revoke, private-address binding |
| Firewall overreach | Capability check and explicit elevated helper boundary; no silent rule creation |

The model does not defend against a compromised Windows account, administrator, malware already able to read the user profile, a user who shares a URL fragment, or a malicious trusted peer. Internet Share deployment additionally depends on the operator's Cloudflare/R2 account policy and must be reviewed before production use.
