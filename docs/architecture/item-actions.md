# Quick Actions architecture

Actions are explicit `IItemAction` contracts. Each action declares its group, order, single/multi-selection requirement, destructive flag, and an availability reason. The registry returns at most three available actions for a selection, so the More menu cannot become an unbounded command surface.

Preview.6 ships hash, ZIP, QR, and Windows image resize/convert actions. Hash and ZIP write unique output files and never modify the source. ZIP skips reparse points and enforces entry/byte bounds. Image output is written to a new path through Windows Imaging APIs; source overwrite is not permitted. QR output is local-only and uses QRCoder. An action that cannot prove its input is available returns a disabled/unavailable result rather than a fake option.

Network actions are separate from local transforms. Send to Device requires a trusted DropLink peer; Nearby Share requires a private LAN address and expiring 192-bit token; Internet Share requires explicit settings and a configured HTTPS Worker backend.
