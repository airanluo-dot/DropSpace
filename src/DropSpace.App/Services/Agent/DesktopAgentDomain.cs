using System.Text.Json;
using System.Text.Json.Nodes;
using DropSpace.App.ViewModels;
using DropSpace.App.Services.Dlc;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Agent;
using DropSpace.Core.Island;
using DropSpace.Core.Models;
using DropSpace.Core.Widgets;

namespace DropSpace.App.Services.Agent;

public sealed class DesktopAgentDomain : AppAgentDomain
{
    private readonly SemaphoreSlim _settingsSave = new(1,1);
    private static readonly HashSet<string> WritableSettings = new(StringComparer.Ordinal)
    {
        "islandActivity","lyrics","islandAppearance","islandContentPriority","systemActivities","widgets",
        "maxDownloadConnections","maxConcurrentDownloads","downloadSpeedLimitBytesPerSecond","defaultDownloadDirectory",
        "clipboardPaused","captureImages","captureFiles","captureFolders","startWithWindows","retentionDays","retentionItemCount",
        "maxImageBytes","maxImagePixels","maxClipboardFileBytes","maxClipboardFileTotalBytes","maxClipboardFileItems","maxTextCharacters",
        "theme","language","lyricsTranslationTarget","closeBehavior","launchPage","overlayMotion","overlayMonitor","fileDragWakeMode",
        "overlayPlacements","quickActionPreferences","quickPanelHotkey","musicWindowHotkey","keyboardShortcuts","smartDragExcludedProcesses",
        "autoCheckForUpdates","autoDownloadUpdates","autoInstallUpdates","updateChannel","enableDeviceHandoff","enableCrossDeviceClipboard",
        "enableNearbySharing","enableInternetSharing","defaultClipboardSyncMode","clipboardPeerModes",
    };
    public DesktopAgentDomain(MainViewModel main, AppAgentDesktopControl desktop, OverlayWindowService overlays,
        IslandExperienceCoordinator island, IslandRetrievalViewModel retrieval, WidgetViewModel widgets,
        NativeSettingsEditor editor, DlcManagerService dlc, IAppStringLocalizer strings, SystemActivityViewModel activities)
    {
        Add("app.state","Read the current app version, main section, status and undo availability without showing windows.",false,"",(a,t)=>Value(new { main.CurrentVersionText,main.CurrentSection,main.StatusMessage,main.HasUndo,desktop.IsAvailable }));
        Add("windows.show","Explicitly show the existing main window.",true,"",(a,t)=>{desktop.Show();return Value(new { status="requested" });});
        Add("windows.hide","Hide the main window using its existing tray behavior.",true,"",(a,t)=>{desktop.Hide();return Value(new { status="requested" });});
        Add("windows.navigate","Show the main app at Space, Clipboard, Pinned, Music or Settings.",true,"section!:string",async(a,t)=>{var section=String(a,"section",max:40);if(section is not ("Space" or "Clipboard" or "Pinned" or "Music" or "Settings"))throw Invalid("Unknown main section.");desktop.Show();await main.NavigateAsync(section,t);return Done();});
        Add("windows.exit","Request normal application shutdown. The connection can close as shutdown completes.",true,"",(a,t)=>{desktop.RequestExit();return Value(new { status="requested" });});
        Add("settings.open","Open a native settings page: General, Island, Shortcuts, Relay, Widgets, DLC, Downloads, SystemActivities, Devices, Updates or About.",true,"page!:string",async(a,t)=>{var page=String(a,"page",max:40);if(page is not ("General" or "Island" or "Shortcuts" or "Relay" or "Widgets" or "DLC" or "Downloads" or "SystemActivities" or "Devices" or "Updates" or "About"))throw Invalid("Unknown or local-only settings page.");await desktop.OpenSettingsAsync(page);return Done();});
        Add("settings.get","Read app preferences and writable top-level fields. Agent credentials, bindings and account session secrets are never included. Relay directories use recent commands.",false,"",(a,t)=>Value(new { settings=main.Settings, writableFields=WritableSettings.OrderBy(x=>x).ToArray() }));
        Add("settings.patch","Merge camelCase preferences through the existing settings coordinator. Unknown/internal fields fail. No credentials or authorization settings. Native privacy prompts are retained; missing AI resources open DLC and apply no part of the patch.",true,"patch!:object",async(a,t)=>
        {
            await _settingsSave.WaitAsync(t);
            try
            {
            var previous=main.Settings;
            var patch=a.GetProperty("patch");
            foreach(var field in patch.EnumerateObject())if(!WritableSettings.Contains(field.Name))throw Invalid("A setting is read-only, unknown or managed by a dedicated command.");
            var node=JsonSerializer.SerializeToNode(previous,JsonOptions)!.AsObject();
            Merge(node,patch,"");
            AppSettings next;
            try { next=(node.Deserialize<AppSettings>(JsonOptions) ?? throw Invalid("Invalid settings.")).Validate(); }
            catch(Exception error) when(error is JsonException or ArgumentException) { throw Invalid("Invalid settings value."); }
            if(next.Lyrics.AiTranslationEnabled && (!previous.Lyrics.AiTranslationEnabled || next.Lyrics.AiModelId!=previous.Lyrics.AiModelId))
            {
                if(!dlc.Packages.Any(p=>p.Package.Id==next.Lyrics.AiModelId && p.State==DlcPackageState.Installed))
                    return await PendingAsync(desktop,"DLC","install_ai_lyrics_model",new { command="dlc.download",id=next.Lyrics.AiModelId });
                if(!previous.Lyrics.Enabled && next.Lyrics.Mode==LyricsMode.Online &&
                    !await desktop.ConfirmAsync("settings.ai_online_lyrics",strings.Get("LyricsOnlinePrivacyHelp"),t))return Result(new { status="cancelled" });
                next=next with { Lyrics=next.Lyrics with { Enabled=true,SecondaryLyrics=true } };
            }
            if(!previous.SystemActivities.ShowWindowsNotifications && next.SystemActivities.ShowWindowsNotifications)
            {
                desktop.Show();
                if(!await editor.CheckNotificationAccessAsync(true))return Result(new { status="permission_denied",operation="enable_windows_notifications" });
            }
            if(!ReferenceEquals(previous,main.Settings))throw new AppAgentCommandException("settings_changed","Settings changed during confirmation; read current preferences and retry the patch.");
            await main.UpdateSettingsAsync(next,t);
            return Result(new { status="completed",settings=main.Settings });
            }
            finally { _settingsSave.Release(); }
        });
        Add("island.state","Read the shared island state, retrieval query, filter and selected content identity without activating it.",false,"",(a,t)=>Value(new { island.Current,retrieval.Query,retrieval.Filter,selectedId=retrieval.SelectedItem?.Item.Id }));
        Add("island.search","Explicitly open unified retrieval through the same global-hotkey path. Filter: 0 all, 1 recent, 2 pinned.",true,"query:string,filter:integer",(a,t)=>{retrieval.Query=String(a,"query",string.Empty,4096);retrieval.Filter=Integer(a,"filter",0,0,2);overlays.OpenRetrieval();return Value(new { status="requested" });});
        Add("island.open","Open the island on Widgets, Music, Files or Clipboard using its existing state owner.",true,"page:string",(a,t)=>{island.Open(a.TryGetProperty("page",out _) ? EnumValue<IslandPage>(a,"page") : null);return Value(island.Current);});
        Add("island.collapse","Collapse the island without closing an independent reference window.",true,"",(a,t)=>{island.Collapse();return Value(island.Current);});
        Add("island.dismiss","Dismiss the island using its existing state policy.",true,"",(a,t)=>{island.DismissNow();return Value(island.Current);});
        Add("widgets.state","Read currently sampled widgets and timer states. This read does not start background sampling.",false,"",(a,t)=>Value(new { widgets.Enabled,widgets.Layout,widgets.Snapshot,widgets.StopwatchText,widgets.FocusTimer,widgets.Countdown }));
        Add("widgets.timer","Operate FocusTimer or Countdown: toggle, reset or duration (minutes 1..1440).",true,"timer!:string,operation!:string,minutes:integer",(a,t)=>{var id=EnumValue<NativeWidgetId>(a,"timer");if(id is not (NativeWidgetId.FocusTimer or NativeWidgetId.Countdown))throw Invalid("Unknown timer.");switch(String(a,"operation",max:32)){case "toggle":widgets.ToggleTimer(id);break;case "reset":widgets.ResetTimer(id);break;case "duration":widgets.SetTimerDuration(id,Integer(a,"minutes",5,1,1440));break;default:throw Invalid("Unknown timer operation.");}return Value(new { widgets.FocusTimer,widgets.Countdown });});
        Add("widgets.stopwatch","Toggle or reset the existing stopwatch.",true,"operation!:string",(a,t)=>{switch(String(a,"operation",max:32)){case "toggle":widgets.StopwatchToggleCommand.Execute(null);break;case "reset":widgets.StopwatchResetCommand.Execute(null);break;default:throw Invalid("Unknown stopwatch operation.");}return Value(new { widgets.StopwatchText });});
        Add("widgets.calculator","Open Windows Calculator through the same fixed URI used by the calculator widget.",true,"",async(a,t)=>Result(new { opened=await Windows.System.Launcher.LaunchUriAsync(new Uri("calculator:")) }));
        Add("activities.state","Read the current island system activity presentation without enabling notification access or waking the island.",false,"",(a,t)=>Value(new { activities.Title,activities.Detail,activities.IsVolume,activities.Percent }));
        Add("activities.dismiss","Dismiss the current notification/volume island presentation. Does not delete Windows notifications.",true,"",(a,t)=>{activities.Clear();island.ClearNotifications();island.ClearVolume();return Value(new { status="completed" });});
        Add("storage.status","Read the app storage location and refreshed storage summary.",false,"",async(a,t)=>{await main.RefreshStorageSummaryAsync(t);return Result(new { main.StoragePath,main.StorageSummary });});
    }

    private static void Merge(JsonObject target, JsonElement patch, string path)
    {
        foreach(var value in patch.EnumerateObject())
        {
            var full=path.Length==0?value.Name:path+"."+value.Name;
            if(full is "lyrics.originalFontSize" or "lyrics.translationFontSize")throw Invalid("This setting is a computed read-only value.");
            var map=path is "keyboardShortcuts" or "overlayPlacements" or "clipboardPeerModes";
            if(!target.ContainsKey(value.Name) && !map)throw Invalid("An unknown nested setting was supplied.");
            if(value.Value.ValueKind==JsonValueKind.Object && target[value.Name] is JsonObject child && !map)Merge(child,value.Value,full);
            else target[value.Name]=JsonNode.Parse(value.Value.GetRawText());
        }
    }
}
