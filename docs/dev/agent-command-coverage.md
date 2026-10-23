# Local Agent command coverage

This is an implementation inventory, not a claim that every command has been executed against external services. The runtime command catalog is authoritative for the exact available names, schemas and mutation flags. This local delivery does not publish packages or update public feeds.

## Existing UI to command mapping

| Existing surface | Commands / implementation owner | Boundary |
| --- | --- | --- |
| Space, Clipboard, Pinned and unified retrieval | `items.*`, `space.*`, `clipboard.status/pause/clear*`; MainViewModel, repository, PinItemsUseCase and existing undo | File objects use native StorageItems for copy. Record removal never deletes originals. Inline text and original item bytes have bounded page reads. |
| Selected-folder relay and successful outputs | `recent.*`; IRecentItemsService | Add/pause/remove use the same selected-folder service. No full-disk scan or import of old files. Reads that consume recent content check stability. |
| Built-in open/copy/path/preview/hash/ZIP/resize/convert/strip-metadata/QR actions | `actions.list/execute`; existing action registry and MainViewModel | `actions.execute` accepts `id` or up to 100 `ids`. Transfer/share actions have dedicated commands. Failures and partial success remain actual service results. |
| Reference window | `reference.*`; ReferenceWindowService and the existing single window | Same content identity; explicit opening only. Close does not remove data. Topmost, zoom, scroll and physical bounds do not activate the window. |
| Main window, settings pages and island | `app.state`, `windows.*`, `settings.*`, `island.*`; existing desktop callbacks/state owner | Only an explicit command opens a window or changes island selection. Background events keep existing no-focus-steal behavior. |
| Appearance, behavior, shortcuts, language, music/lyrics preferences, download limits, device switches, widgets and update preferences | `settings.get/patch`; MainViewModel settings coordinator | Camel-case finite top-level allowlist, known nested fields, normal validation/preflight. Returns actual normalized settings. Separate authorization store is not included. Relay directories have dedicated commands. |
| Widgets and system-activity display | `widgets.*`, `activities.*`; WidgetViewModel and activity/island owners | Passive state reads do not enable sampling or notification access. Calculator uses its existing fixed Windows URI. |
| Media, lyrics, AI status, QQ login and managed NetEase support | `music.*`, `lyrics.*`, `ai.*`; existing media/AI/login/enhancement owners | Publisher capabilities checked. Requests are reported as requested rather than guaranteed playback. QQ cookies and credentials never serialized. |
| Independent music window and Windows system volume | `music.window_*`, `volume.*`; MusicWindowService and WindowsVolumeActivityService | Explicit opening only; shares the existing media session. Volume operates the Windows default output endpoint, not an individual player. |
| Ordinary downloads, resource DLC, feature modules and updates | `downloads.*`, `dlc.*`, `modules.*`, `updates.*`; existing download/package/runtime/updater owners | Official manifest allowlist remains in force. No arbitrary assemblies, process commands, package URLs or module actions. |
| Devices, transfers, cross-device clipboard and sharing | `devices.*`, `transfers.*`, `clipboard.peers/configure_peer/remove_peer/send_item`, `shares.*`; TransferAgentDomain | Existing trust/discovery/service checks apply. Caller cannot provide arbitrary peer endpoints. Source paths resolve from existing content objects. |

## Deliberate interaction boundaries and unverified behavior

- Agent binding creation, pairing-key display, revocation and reauthorization are local native UI operations only. They are absent from the callable catalog and generic settings patch. A paired agent cannot create a replacement grant after revocation.
- QQ login opens its existing user login window and returns `pending_confirmation`. Credentials, browser cookies and tokens are never accepted/read through these commands. Completion must be inspected with `music.qq_status`.
- Notification enablement invokes the existing Windows permission request. Denial returns `permission_denied`; the API cannot approve the OS prompt.
- Cleanup, overwriting an existing image-export destination, resource-package download/deletion, module data deletion, NetEase initial installation/reinstallation and update installation use native confirmation before calling the existing service. Cancellation is explicit. The DLC confirmation includes original package notices, source and the warning for an active AI model.
- AI enablement/model replacement requires the selected resource package to be installed. If missing, `settings.patch` opens DLC, returns `pending_confirmation` with a usable `dlc.download` command and applies no part of the patch. The caller can finish the resource step and retry. Existing online-lyrics privacy consent is retained.
- Native drag initiation/drop into another app, IME keystroke composition and OS file-picker gestures are not synthesized by this API. File-object copy, source-object reads, explicit directory/path arguments and the existing native UI cover those workflows without claiming physical input automation.
- This is an application interface, not arbitrary file-system or shell execution. `items.read_bytes` reads only a resolved existing content object. Raw account secret stores, authorization management, arbitrary code execution and arbitrary module packages are intentionally not exposed.
- Existing media publishers, paired-device connectivity, remote sharing backends, external login and actual update installations require their real environments. Catalog inclusion is not evidence that those external operations were run in local validation. Build/runtime evidence belongs in the delivery report.

## Implemented catalog

Parameters marked `!` are required. Parameters use JSON types; descriptions identify enum values and bounds. Mutation includes UI changes, copies, network requests or explicit refreshes, not only database writes.

### ContentAgentDomain (37 commands)

| Command | Parameters | Mutates | Behavior |
| --- | --- | --- | --- |
| `items.query` | `search:string,source:string,pinnedOnly:boolean,kind:string,status:string,limit:integer,offset:integer` | false | Search stored names and text across Space, Clipboard, pinned, Recent and successful outputs; no disk indexing. Sources: Space, Clipboard, Recent. Limit 1..250, offset 0..100000. |
| `items.get` | `id!:string` | false | Read a stored content object and metadata without activating windows. Inline text is capped at 65536 characters; text.characterCount reports total. Use items.read_text/read_bytes for complete paginated content. |
| `items.read_text` | `id!:string,offset:integer,count:integer` | false | Read a page of original stored plain text by UTF-16 character offset (count 1..65536). File objects use items.read_bytes or items.preview. |
| `items.read_bytes` | `id!:string,offset:integer,count:integer` | false | Read original content bytes from an existing item only. count 1..524288, offset in bytes. Returns base64; no arbitrary path reads. External active writers are rejected. |
| `space.add_text` | `text!:string` | true | Add text to Space through its existing content classifier. Maximum follows the configured text limit. |
| `space.add_paths` | `paths!:array` | true | Add existing file or directory references to Space. Originals remain in place. Returns the accepted count. |
| `items.copy` | `id!:string` | true | Copy text as text or files as native file objects. Availability and recent-file stability are checked. |
| `items.copy_path` | `id!:string` | true | Explicitly copy a file path as plain text; separate from copying a file object. |
| `items.open` | `id!:string` | true | Open available content through its existing Windows default application action. |
| `items.reveal` | `id!:string` | true | Reveal an available file in Explorer using the existing action. |
| `items.relocate` | `id!:string,path!:string` | true | Replace a missing file reference using the existing relocation validator; does not move external files. |
| `items.pin` | `id!:string,pinned!:boolean` | true | Set an object's pinned state using shared undo and mutation ownership. |
| `items.remove` | `id!:string` | true | Remove a record through the existing undo flow. Never deletes the external original. |
| `items.undo` | `` | true | Undo the current reversible workspace operation. |
| `items.export_image` | `id!:string,path!:string` | true | Export a stored clipboard image using the existing payload exporter. An existing destination requires native overwrite confirmation. |
| `items.preview` | `id!:string,page:integer,width:integer,includeBytes:boolean` | false | Read a bounded existing preview. Image bytes are omitted unless includeBytes is true; width 64..2048, page 1..10000. |
| `clipboard.status` | `` | false | Read capture pause state and clear undo status. |
| `clipboard.pause` | `paused!:boolean` | true | Pause or resume existing clipboard capture. |
| `clipboard.clear_preview` | `range!:string` | false | Count records affected by LastHour, Today or All clipboard cleanup. |
| `clipboard.clear` | `range!:string` | true | Show the existing cleanup warning in the native app, then clear clipboard history with undo. External files are unaffected. |
| `recent.directories` | `` | false | List explicitly selected relay folders and availability reasons. No directory is enabled by this read. |
| `recent.directory_add` | `path!:string` | true | Enable relay for one selected local folder's current level. Existing files are not imported. |
| `recent.directory_pause` | `path!:string,paused!:boolean` | true | Pause or resume a configured relay folder. |
| `recent.directory_remove` | `path!:string` | true | Stop watching a folder. Retained content and original files are unaffected. |
| `recent.retain` | `id!:string` | true | Keep the same Recent object in Space, preserving identity. |
| `recent.remove` | `id!:string` | true | Remove a recent candidate record without deleting its original. |
| `recent.refresh` | `id!:string` | true | Recheck a recent or retained file's stability and availability. |
| `recent.cleanup` | `` | true | Apply configured age/count limits to unprotected Recent candidates; never deletes external originals. |
| `reference.open` | `id!:string` | true | Explicitly open the single image/text reference window for the same stored object. Missing or waiting content shows its current availability state. |
| `reference.state` | `` | false | Read reference-window identity and presentation state without activating it. |
| `reference.close` | `` | true | Close reference viewing; the content record remains. |
| `reference.topmost` | `enabled!:boolean` | true | Set whether the existing reference window stays above other windows; does not activate it. |
| `reference.zoom` | `scale:number,fit:boolean` | true | Set image scale 0.05..8 or fit to window. Does not activate the reference. |
| `reference.move_resize` | `x!:integer,y!:integer,width!:integer,height!:integer` | true | Move/resize the existing reference window in physical screen pixels without activation. Width 320..8192, height 180..8192. |
| `reference.scroll` | `horizontal:integer,vertical!:integer` | true | Scroll long text or a zoomed image in the existing reference window (DIPs, 0..100000000). Does not activate it. |
| `actions.list` | `id!:string` | false | Discover existing action descriptors and per-item availability. |
| `actions.execute` | `id:string,ids:array,action!:string,destinationDirectory:string,outputFormat:string,width:integer,height:integer,keepAspectRatio:boolean` | true | Execute a declared built-in item action (including ZIP and image processing) with shared validation/output registration. Transfer/share actions use their dedicated commands. |

### DesktopAgentDomain (20 commands)

| Command | Parameters | Mutates | Behavior |
| --- | --- | --- | --- |
| `app.state` | `` | false | Read the current app version, main section, status and undo availability without showing windows. |
| `windows.show` | `` | true | Explicitly show the existing main window. |
| `windows.hide` | `` | true | Hide the main window using its existing tray behavior. |
| `windows.navigate` | `section!:string` | true | Show the main app at Space, Clipboard, Pinned, Music or Settings. |
| `windows.exit` | `` | true | Request normal application shutdown. The connection can close as shutdown completes. |
| `settings.open` | `page!:string` | true | Open a native settings page: General, Island, Shortcuts, Relay, Widgets, DLC, Downloads, SystemActivities, Devices, Updates or About. |
| `settings.get` | `` | false | Read app preferences and writable top-level fields. Agent credentials, bindings and account session secrets are never included. Relay directories use recent commands. |
| `settings.patch` | `patch!:object` | true | Merge camelCase preferences through the existing settings coordinator. Unknown/internal fields fail. No credentials or authorization settings. Native privacy prompts are retained; missing AI resources open DLC and apply no part of the patch. |
| `island.state` | `` | false | Read the shared island state, retrieval query, filter and selected content identity without activating it. |
| `island.search` | `query:string,filter:integer` | true | Explicitly open unified retrieval through the same global-hotkey path. Filter: 0 all, 1 recent, 2 pinned. |
| `island.open` | `page:string` | true | Open the island on Widgets, Music, Files or Clipboard using its existing state owner. |
| `island.collapse` | `` | true | Collapse the island without closing an independent reference window. |
| `island.dismiss` | `` | true | Dismiss the island using its existing state policy. |
| `widgets.state` | `` | false | Read currently sampled widgets and timer states. This read does not start background sampling. |
| `widgets.timer` | `timer!:string,operation!:string,minutes:integer` | true | Operate FocusTimer or Countdown: toggle, reset or duration (minutes 1..1440). |
| `widgets.stopwatch` | `operation!:string` | true | Toggle or reset the existing stopwatch. |
| `widgets.calculator` | `` | true | Open Windows Calculator through the same fixed URI used by the calculator widget. |
| `activities.state` | `` | false | Read the current island system activity presentation without enabling notification access or waking the island. |
| `activities.dismiss` | `` | true | Dismiss the current notification/volume island presentation. Does not delete Windows notifications. |
| `storage.status` | `` | false | Read the app storage location and refreshed storage summary. |

### MediaAgentDomain (28 commands)

| Command | Parameters | Mutates | Behavior |
| --- | --- | --- | --- |
| `music.state` | `` | false | Read current media capabilities, source, position and availability; album image bytes are excluded. |
| `music.window_state` | `` | false | Read independent music-window state without opening it. |
| `music.window_open` | `` | true | Explicitly open or activate the single shared-session music window. |
| `music.window_close` | `` | true | Close the independent window while playback and the island continue. |
| `music.window_lyrics` | `visible!:boolean` | true | Show/hide the lyrics column in an already open window. |
| `music.window_translation` | `enabled!:boolean` | true | Change the shared secondary-lyrics preference from an open window. |
| `volume.state` | `` | false | Sample the actual Windows default output endpoint without an island notification. |
| `volume.set` | `percent!:integer` | true | Set Windows system volume to 0..100; nonzero volume also unmutes. |
| `volume.mute` | `muted!:boolean` | true | Mute/unmute Windows system output. |
| `music.play_pause` | `` | true | Request play/pause from the existing Windows media-session owner. Fails if the publisher does not expose the required capability. |
| `music.next` | `` | true | Request the next track from the current media publisher. |
| `music.previous` | `` | true | Request the previous track from the current media publisher. |
| `music.seek` | `seconds!:number` | true | Seek to seconds from the start of the current track within its known duration. |
| `music.restart` | `` | true | Run the existing bounded media reconnect operation. |
| `lyrics.get` | `` | false | Read current lyrics, translated lines, status and active line. Settings use settings.patch. |
| `lyrics.clear_cache` | `` | true | Ask in the native app before clearing the existing lyrics/translation cache. |
| `ai.status` | `` | false | Read actual AI execution/backend state and cache status; this is not a capability claim about untested hardware. |
| `ai.resume` | `` | true | Resume translation after its existing circuit pause. |
| `ai.retry_gpu` | `` | true | Run the existing GPU retry path and report resulting execution state. |
| `ai.model_inspect` | `id!:string` | false | Inspect a declared local lyric model's installed resources. Model downloads/deletion use dlc commands. |
| `music.qq_status` | `` | false | Read QQ Music sign-in state without cookies, credentials or browser data. |
| `music.qq_login` | `` | true | Open the existing native QQ Music login window. The user completes sign-in; no credentials are accepted over this API. |
| `music.qq_verify` | `` | true | Verify the currently saved QQ Music session without exposing credentials. |
| `music.qq_sign_out` | `` | true | Sign out of the existing QQ Music session. |
| `music.netease_inspect` | `` | false | Inspect the existing managed NetEase enhancement. |
| `music.netease_install` | `reinstall:boolean` | true | Ask in the native app before deploying or reinstalling the managed NetEase enhancement; returns actual state. |
| `music.netease_update` | `` | true | Update an existing managed NetEase enhancement using its normal action. |
| `music.netease_remove` | `` | true | Remove the managed enhancement through its existing removal action. |

### PackagesAgentDomain (29 commands)

| Command | Parameters | Mutates | Behavior |
| --- | --- | --- | --- |
| `downloads.list` | `historyLimit:integer` | false | Read ordinary downloads and bounded completed history (limit 0..500). |
| `downloads.get` | `id!:string` | false | Read an ordinary download by task identifier. |
| `downloads.enqueue` | `url!:string,directory:string,fileName:string` | true | Queue an ordinary HTTP(S) download using existing filename/conflict handling and configured output location. Does not claim completion. |
| `downloads.pause` | `id!:string` | true | Pause an existing ordinary download. |
| `downloads.resume` | `id!:string` | true | Resume an existing paused or failed ordinary download using its existing state policy. |
| `downloads.cancel` | `id!:string` | true | Cancel an existing ordinary download through the downloader's cleanup owner. |
| `downloads.remove_history` | `id!:string` | true | Remove completed/cancelled history only; the published file is retained. |
| `dlc.list` | `` | false | Read built-in optional resource package inventory and actual installation/operation state. |
| `dlc.refresh` | `` | true | Refresh resource package inspection through the existing DLC owner. |
| `dlc.download` | `id!:string` | true | Show native confirmation with the package source and download size, then use the shared resource downloader. Returns actual package state. |
| `dlc.delete` | `id!:string` | true | Show native confirmation before deleting one app-owned resource package; original user files are unaffected. |
| `dlc.cancel` | `id!:string` | true | Request cancellation of a resource download through its existing owner. |
| `modules.list` | `` | false | Discover official modules, installed states, declared UI actions and settings. No arbitrary package/assembly loading. |
| `modules.refresh` | `` | true | Refresh the existing official module catalog. |
| `modules.install` | `id!:string` | true | Install/update a declared official module through verified package staging and compatibility checks. |
| `modules.enable` | `id!:string` | true | Enable an installed official module through its existing runtime. |
| `modules.disable` | `id!:string` | true | Disable a module and retire its worker/UI contributions using the runtime lifecycle owner. |
| `modules.uninstall` | `id!:string` | true | Uninstall a module while retaining module data, using its existing bounded worker retirement and cleanup. |
| `modules.retry_cleanup` | `id!:string` | true | Retry pending module cleanup through the scoped package owner. |
| `modules.clear_data` | `id!:string` | true | Ask in the native app before clearing a disabled module's own data. Never deletes host or other module data. |
| `modules.settings_get` | `id!:string` | false | Read an official module's declared local settings. |
| `modules.settings_set` | `id!:string,key!:string,value!:string` | true | Write a declared module setting through the existing schema and running-generation validator. |
| `modules.invoke` | `id!:string,action!:string,input!:object` | true | Invoke only an action declared by a running official module's manifest. The runtime validates identity, bounded protocol and generation. |
| `updates.status` | `` | false | Read current update status, candidate and downloaded package validation state; no network request. |
| `updates.check` | `` | true | Run the existing manual update check against the configured update channel. |
| `updates.download` | `` | true | Download the current update candidate through the existing verified updater. |
| `updates.install` | `` | true | Ask in the native app before requesting normal installation of a ready verified update; the connection may close as the app exits. |
| `updates.open_location` | `` | true | Reveal the ready update's local download directory through the existing shell action. |
| `updates.release_notes` | `` | true | Open the current update release notes through the existing validated link action. |

### TransferAgentDomain (22 commands)

| Command | Parameters | Mutates | Behavior |
| --- | --- | --- | --- |
| `devices.status` | `` | false | Read device handoff and cross-device clipboard status. |
| `devices.list` | `` | false | List known peers without exposing device secrets. |
| `devices.discover` | `seconds:integer` | false | Discover nearby devices using the existing enabled handoff service. |
| `devices.pair` | `deviceId!:string` | true | Pair a discovered Windows device. The user must compare and confirm its SAS in the native App. |
| `devices.unpair` | `peerId!:string` | true | Remove trust and clipboard selection for a paired device after native confirmation. |
| `transfers.list` | `` | false | Read active outgoing transfers. |
| `transfers.history` | `offset:integer,limit:integer` | false | Read persisted send/receive history with bounded pagination. |
| `transfers.cancel` | `id!:string` | true | Request cancellation and return cancelRequested plus current task snapshots; final cancellation/outcome is determined by the task owner. |
| `transfers.clear_completed` | `` | true | Clear completed outgoing task cards; does not delete files. |
| `transfers.send_items` | `peerId!:string,itemIds!:array` | true | Start sending selected existing file objects to a discovered trusted peer; returns the task ID. |
| `transfers.send_text` | `peerId!:string,text!:string,kind:string,label:string` | true | Send explicit text or a URL to a discovered trusted peer. |
| `transfers.respond` | `sessionId!:string,accept!:boolean` | true | Require a persisted incoming AwaitingApproval session. Acceptance shows its peer, item count, bytes and session ID in the native confirmation before existing host validation. |
| `clipboard.peers` | `` | false | Read selected cross-device clipboard channels. |
| `clipboard.configure_peer` | `peerId!:string,mode!:string` | true | Set a trusted discovered peer's clipboard mode using the existing settings owner. |
| `clipboard.remove_peer` | `peerId!:string` | true | Remove one cross-device clipboard channel without unpairing the device. |
| `clipboard.send_item` | `peerId!:string,itemId!:string` | true | Send an existing supported text, URL or image object to a trusted discovered peer. |
| `shares.list` | `` | false | Read nearby shares and locally revocable encrypted-share IDs; no backend credentials are returned. |
| `shares.get_nearby` | `id!:string` | false | Read an existing nearby share descriptor, including its user-facing share URL. |
| `shares.create_nearby` | `itemIds!:array` | true | Create a nearby share for existing objects using the current App share settings. |
| `shares.create_internet` | `itemIds!:array,lifetimeMinutes:integer` | true | Create an encrypted Internet share using the already configured backend and App limits. |
| `shares.revoke_nearby` | `id!:string` | true | Revoke a nearby share without deleting its original files. |
| `shares.revoke_internet` | `id!:string` | true | Revoke an encrypted Internet share using the locally owned revoke handle. |

Total explicitly registered commands in this source inventory: **136**. The HTTP host validates against the live catalog before dispatch, and the dispatcher independently validates names, parameter types, required fields and unknown arguments.
