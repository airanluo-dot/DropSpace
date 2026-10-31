# Rendered preview cache boundary probe

Run `dotnet run --project scripts/rendered-preview-cache-probe -c Release -- all`.
This portable probe links the actual App `QuickPreviewService`. Two focused
scenarios ensure a rejected optional-cache destination cannot throw after native
PDF rendering, while page cancellation still propagates before dialog publication.
The cache, registry and payload store are bounded fixtures; this does not render a
PDF or start WinUI and does not establish target Windows dialog/resource behavior.

`invalid-cache` and `cancel` select a scenario. An optional
`-p:PreviewSourcePath=/absolute/path/QuickPreviewService.cs` can reproduce the same
boundary against a saved baseline source.
