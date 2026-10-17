# Batch selection probe

```sh
dotnet run --project scripts/batch-selection-probe/BatchSelectionProbe.csproj
```

Three focused scenarios exercise the actual source-linked batch projection and selection
methods in `MainViewModel.ItemSelection.cs`, the actual item card, and Core selection resolution:
collapse/expand, paged header replacement, and removed/hidden-collection selection retirement.
The pinned CommunityToolkit dependency and default analyzers remain enabled.

The fixture supplies the main view model's collection/visibility state, while `BitmapImage`
and `IStorageItem` are identity-only shims. It does not compile the rest of `MainViewModel`
or `MainPage`, emulate WinUI binding/virtualization, or execute keyboard, accessibility,
drag/drop, or Windows file operations. Those native UI paths require Windows validation.
