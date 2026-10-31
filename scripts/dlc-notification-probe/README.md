# DLC notification lifetime probe

Run `dotnet run --project scripts/dlc-notification-probe -c Release -- all`.
This portable probe links the actual DLC manager, notification boundary and storage
metadata types. It runs three focused scenarios: a throwing observer during local
inventory/install/delete, during the real throttled progress timer, and during
download cancellation. Providers are deterministic fixtures; no model download,
native process or GUI is started.

`transaction`, `progress` and `cancel` select an individual scenario. An optional
`-p:DlcSourcePath=/absolute/path/DlcManagerService.cs` lets the same fixture run
against a saved baseline. A baseline progress callback can terminate the process
because it throws directly from the ThreadPool timer.

This is managed lifecycle evidence. It does not establish WinUI rendering,
real package integrity, provider/native cancellation or Windows shutdown behavior.
