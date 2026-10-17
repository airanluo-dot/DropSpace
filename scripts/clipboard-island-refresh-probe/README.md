# Clipboard island refresh probe

```sh
dotnet run --project scripts/clipboard-island-refresh-probe/ClipboardIslandRefreshProbe.csproj -- results.json
# Select exactly one scenario:
dotnet run --project scripts/clipboard-island-refresh-probe/ClipboardIslandRefreshProbe.csproj -- --scenario refresh_error_recovery results.json
```

Four focused scenarios link the complete production `ClipboardIslandViewModel.cs`, the actual
dispatcher extension, item card, Quick Action button model and Core project:

1. A refresh failure clears after accepted recovery; a concurrent action failure survives a
   later successful refresh because reading cards does not retry the failed action.
2. A successful callback queued before every owner hides cannot replace the reopened surface;
   accepted cards remain cached while hidden, and the new visibility period can publish.
3. A failed callback from an older visibility period cannot publish on the reopened surface.
4. Hiding one owner leaves an in-flight read valid while another owner remains visible.

MainViewModel's recent-item/action boundaries return controlled tasks; capture events and the
WinUI dispatcher are narrow fixture substitutes. A managed single-thread synchronization context
is source-linked from the existing navigation probe without modifying or executing its scenarios.
Reads, actions and UI publication use explicit completion/queue barriers with timeout bounds,
not sleeps. Assertions accumulate and JSON records each reached assertion, including failures.

No view-model lifecycle algorithm is copied. This does not execute MainViewModel's repository or
thumbnail pipeline, SQLite, the system clipboard, native WinUI/COM, property bindings, focus or
real rendering. BitmapImage and IStorageItem are identity-only shims. Default analyzers and
warnings-as-errors remain enabled. This project does not run other probes or native diagnostics.

For a fixed-source comparison, use `-p:ClipboardSourcePath=/absolute/path/to/ClipboardIslandViewModel.cs`.
Use distinct `--artifacts-path` directories for before and after builds to prevent assembly reuse.
An older source with the same public API runs against the identical fixture and assertions.

An additional opt-in scenario runs only when explicitly selected:

```sh
dotnet run --project scripts/clipboard-island-refresh-probe/ClipboardIslandRefreshProbe.csproj -- --scenario pin_action_recovery results.json
```

It links the actual clipboard VM and item card to verify pin admission while pending,
duplicate rejection, failed-action recovery with explicit pin/label notifications, and a
successful retry. Only the upstream MainViewModel pin result is controlled. No owner is
made visible, so this invocation does not run the four refresh scenarios. Their default
selection and assertions are unchanged. This does not execute ToggleButton binding, UIA
state announcements, WinUI/XAML, the production mutation/Undo pipeline, or a real database.
