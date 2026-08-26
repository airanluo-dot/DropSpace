# Device Handoff architecture

`DeviceHandoffService` owns the lifecycle boundary: when the user enables handoff it starts the Kestrel HTTPS host, checks the inbound capability, attempts standard mDNS/DNS-SD registration, and exposes discovery/pair/send/approve operations. The host and client are infrastructure adapters; ViewModels receive only domain records and safe error categories.

Transfer state is persisted in schema v2 tables `paired_devices` and `transfer_sessions`. Receive sessions are staged before an atomic commit. The client polls approval, sends 4 MiB chunks, resumes from the receiver's accepted chunk set, and reports cancellation/integrity failures without touching original source files.

The cross-device clipboard reuses `ClipboardCaptureService.ItemCaptured`; it does not add a polling watcher. Per-peer modes are Off, Manual, Automatic Text+URL, and Automatic Text+URL+Image. The 10,000-entry/24-hour loop guard keys content hashes and is applied before both send and receive. Remote text/image is persisted through the same repository/payload path and then written to the Windows clipboard with the existing self-write suppression only when Clipboard Pause is not active. Pause is persisted and held as a commit barrier through remote validation, repository write, event propagation, and automatic clipboard mutation.

Text and URL device handoff uses a separate authenticated message route and a bilateral UI flow: the receiver sees sender, kind, normalized preview, byte length, and expiry context, then Accept adds the payload to Temporary Space while Reject/expiry/cancel leaves Clipboard History, the system clipboard, and peer secrets unchanged.
