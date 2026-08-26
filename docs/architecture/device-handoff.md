# Device Handoff architecture

`DeviceHandoffService` owns the lifecycle boundary: when the user enables handoff it starts the Kestrel HTTPS host, checks the inbound capability, attempts DNS-SD registration, and exposes discovery/pair/send/approve operations. The host and client are infrastructure adapters; ViewModels receive only domain records and safe error categories.

Transfer state is persisted in schema v2 tables `paired_devices` and `transfer_sessions`. Receive sessions are staged before an atomic commit. The client polls approval, sends 4 MiB chunks, resumes from the receiver's accepted chunk set, and reports cancellation/integrity failures without touching original source files.

The cross-device clipboard reuses `ClipboardCaptureService.ItemCaptured`; it does not add a polling watcher. Per-peer modes are Off, Manual, Automatic Text+URL, and Automatic Text+URL+Image. The 10,000-entry/24-hour loop guard keys content hashes and is applied before both send and receive. Remote text/image is persisted through the same repository/payload path and then written to the Windows clipboard with the existing self-write suppression.
