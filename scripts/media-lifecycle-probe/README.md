# Focused media lifecycle probe

Run locally with .NET 10:

```sh
dotnet run --project scripts/media-lifecycle-probe/MediaLifecycleProbe.csproj
# To rerun only one changed scenario:
dotnet run --project scripts/media-lifecycle-probe/MediaLifecycleProbe.csproj -- recovery
```

This links the actual service, native-owner executor, operation lifetime, session owner,
subscription admission and subscription retirement sources. Only the Windows/WinUI API
boundary is replaced. The fake publisher checks one owning thread for every native call,
yields asynchronous metadata reads and records actual command effects separately from
acceptance. Three combined scenarios cover:

- Startup with a transiently unavailable source; recovery from its session event without a
  second manager notification; manager-event discovery and controls after disable/re-enable.
- A disconnected wrapper still selected/enumerated by the OS; failed-command rediscovery
  without replay; stale callbacks and a late metadata read across another disable/re-enable.
- Native await affinity, canceled callers retaining outstanding native operations, queued
  work draining before owner shutdown, idempotent disposal and explicit retry of a failed
  dispatcher creation without replacing an already established native owner.

This does **not** emulate COM marshaling, a Windows STA, the Windows SMTC broker, NetEase,
InfLink or WinUI rendering. Passing proves the managed lifecycle against a controlled
publisher; Windows/NetEase startup and plugin-toggle controls still require real execution.
No package publishing, production data, release builds or remote workflows are involved.

The separate opt-in `image-native` scenario links `ClipboardImageReadLifetime` and
`NativeAsyncLifetime` to check remote image native ownership:

```sh
dotnet run --project scripts/media-lifecycle-probe/MediaLifecycleProbe.csproj --no-restore -- image-native
```

This single combined scenario cancels a waiting caller, keeps one input and its capacity
until controlled native completion and a blocked Cancel both finish, refuses excess work,
and verifies fresh work succeeds after retirement. It uses the existing narrow WinRT
operation shim; no real image decoder, `ImportRemoteAsync`, bridge gate, database commit,
or system clipboard executes. Those production call sites require separate static review
and Windows execution. The original three media scenarios are unchanged and are not
selected by this command. The project retains its existing analyzer configuration.

The separate opt-in `drag-native` scenario links the actual `ItemAssetWorkLifetime`,
`DragStorageItemService`, and native completion helper:

```sh
dotnet run --project scripts/media-lifecycle-probe/MediaLifecycleProbe.csproj --no-restore -- drag-native
```

Eight controlled file lookups occupy all native slots while a ninth old request queues.
Retiring the actual asset owners cancels the callers and queued work, while native lookups
retain their slots until explicit Completed. Fresh work waits, then succeeds once old
native operations complete. This tests managed queue/native ownership, not the Main list
owner dictionary, navigation/clear integration, real Windows Storage/OLE, or thumbnails.
No original media or image scenario is selected. Folder/image payload branches remain
static-only coverage.
