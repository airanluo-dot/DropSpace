# Quick Actions architecture

Actions are explicit `IItemAction` contracts. Each action declares its ID/provider, group, order, single/multi-selection requirement, destructive flag, and an availability reason. The registry evaluates the complete capability set; callers choose the bounded primary slice (three actions) separately from the More slice, so adding an action cannot silently truncate the registry or advertise an unavailable command.

Preview.7 ships hash, ZIP, QR, Windows image resize/convert, and metadata-stripping actions. Hash and ZIP write unique output files and never modify the source. ZIP skips reparse points and enforces entry/byte bounds. Image output is written to a new path through Windows Imaging APIs; source overwrite is not permitted and re-encoding omits arbitrary source metadata. QR output is local-only and uses QRCoder. An action that cannot prove its input is available returns a disabled/unavailable result rather than a fake option.

Network actions are separate from local transforms. Send to Device requires a trusted DropLink peer; Nearby Share requires a private LAN address and expiring 192-bit token; Internet Share requires explicit settings and a configured HTTPS Worker backend.
