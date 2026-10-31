# Navigation projection probe

```sh
dotnet run --project scripts/navigation-projection-probe/NavigationProjectionProbe.csproj
```

Five focused scenarios link the actual production navigation and selection partials,
`ItemProjectionService`, item card, Quick Action button model, and Core project:

1. A cross-section failure retires old rows, selection, asset ownership, and paging state.
   It then **calls the real `LoadMoreItemsAsync`**. Any unexpected query is recorded and
   answered with a destination row so obsolete-cursor reuse and mixed rows are observable.
2. A cross-section cancellation retires the old projection and publishes cancellation status.
3. A same-section failure preserves accepted rows, selection, paging, and asset ownership.
4. An older navigation's late success cannot replace the current destination.
5. An older navigation's late failure cannot overwrite the current empty-search status.

Assertions accumulate within each scenario; a baseline failure does not skip the paging
operation. All five scenarios execute, and the process exits nonzero if any fails. An optional
single positional argument writes the results as JSON. No sleeps or native UI are required.

Run only the two navigation-ownership scenarios, without the five scenarios above:

```sh
dotnet run --project scripts/navigation-projection-probe/NavigationProjectionProbe.csproj -- --ownership-only
# An optional path after --ownership-only writes those two results as JSON.
```

These scenarios call the actual navigation ticket API to check that an external public
navigation retires an earlier reserved request before it can publish a destination or query;
same-section navigation and a round trip through another section each replace ownership,
while ordinary and explicit search reloads preserve it. Reservation represents the boundary
before settings preflight; the actual MainPage preflight, Settings deep link, Widget/Clipboard
Island caller, search setter/debounce, and native focus guards are not source-linked or executed.
Only the selected scenario group runs. The default invocation retains the original five.

Run only the three debounce failure-ownership scenarios:

```sh
dotnet run --project scripts/navigation-projection-probe/NavigationProjectionProbe.csproj -- --search-only
# An optional path after --search-only writes those three results as JSON.
```

This group links the actual 220 ms debounce method alongside projection/navigation.
It checks an old search failing after newer navigation, a cancelled synchronous
provider later failing after a new empty search, and retained current-search error
feedback. The fixture changes SearchText directly; the real setter, binding,
focus and WinUI dispatcher are still outside the probe. A saved custom navigation
source must include the debounce method to run this group; older extraction files
continue to support their original groups.

The repository boundary implements only `QueryPageAsync` using controlled completion sources.
Query construction and `Task.Run` scheduling are production code. A managed single-thread
synchronization context serializes continuations. The fixture supplies view-model state and
instrumented asset-owner tokens; it does not duplicate navigation algorithms. Quick Action
evaluation, thumbnail/storage loading, property-change bindings, SQLite,
WinUI controls, UI focus, accessibility, and actual asset cleanup remain outside this probe.

For a before/after comparison, extract the same real methods from a fixed prior revision:

```sh
python3 scripts/navigation-projection-probe/prepare-baseline.py 6604f59
dotnet run --project scripts/navigation-projection-probe/NavigationProjectionProbe.csproj \
  -p:NavigationSourcePath=/absolute/repo/artifacts/navigation-projection-probe/before/MainViewModel.Navigation.cs
```

The extractor writes untouched method bodies, their original line positions, and SHA-256
identities under ignored `artifacts/navigation-projection-probe/before`. Only the navigation
source path changes; all five expectations and the fixture remain identical. Running again
without that MSBuild property compiles the current production partial. Default analyzers and
warnings-as-errors remain enabled. These commands are manual and do not expand automatic tests.
The ownership scenarios are compiled only for the default current navigation source, since
historical sources do not expose the navigation ticket API; a custom source rejects
`--ownership-only` rather than running the original scenarios under that label.
