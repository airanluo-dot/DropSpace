# Hotkey stop lifetime probe

Run `dotnet run --project scripts/hotkey-stop-lifetime-probe -c Release`.
Two focused scenarios link the actual Quick Panel registration owner and Music
wrapper. A managed admission barrier queues Stop behind disposal; the standalone
fixture uses one ThreadPool worker to make continuation order deterministic, then
verifies all stops settle and startup remains closed. No native thread, user32
function, global key registration or GUI is started.

`-p:HotkeySourcePath=/absolute/path/GlobalQuickPanelHotkeyService.cs` selects a
saved baseline for reproducing the gate-disposal failure. Native Windows hotkey
registration, settings key swaps and process shutdown still require target checks.
