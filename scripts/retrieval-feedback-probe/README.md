# Retrieval feedback and card preview probe

Run `dotnet run --project scripts/retrieval-feedback-probe -c Release`.
An optional positional path writes the five scenario results as JSON.

This portable probe links the production retrieval view model, item card and Quick
Action button model. Controlled upstream reads exercise query failure followed by
recovery, and successful refresh after successful/failed copy feedback. The card
scenarios verify whitespace compatibility, a supplementary character at the
truncation boundary, and bounded formatting allocations for a 300,000-character
synthetic CR/LF body. The allocation assertion is a focused regression bound, not
an application throughput or memory-peak benchmark.

The actual 180 ms query debounce is retained. Platform identity/dispatch and
upstream operations are narrow fixtures; no clipboard, WinUI controls, native
thumbnails or GUI is started. Target-system focus, binding and native resources
remain outside this evidence.

`-p:RetrievalSourcePath=/absolute/path/IslandRetrievalViewModel.cs` and
`-p:CardSourcePath=/absolute/path/ItemCardViewModel.cs` select saved baseline
sources without changing the expectations.
