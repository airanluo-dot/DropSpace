# Item card lifecycle probe

```sh
dotnet run --project scripts/item-card-lifecycle-probe/ItemCardLifecycleProbe.csproj
```

One combined scenario links the actual `ItemCardViewModel`, `QuickActionButtonViewModel`
and Core project, with the pinned CommunityToolkit dependency. Delayed asynchronous asset
results are rejected after relink or retirement; older overlay records are rejected;
pinning and last-used metadata preserve current assets and pending preparations. A newer
authoritative revision can restore a retired card while old generations remain rejected.

`BitmapImage` and `IStorageItem` are identity-only platform shims. This verifies the card's
managed publication gates, not Windows drag/drop, thumbnail decoding, WinUI rendering or
the full `MainViewModel`/overlay integration. Production callers require separate code
review and the Windows interaction path remains unexecuted on Linux.
