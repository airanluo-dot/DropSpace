using System.Text.Json;
using DropSpace.App.Services.Dlc;
using DropSpace.App.ViewModels;
using DropSpace.Core.Agent;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Downloads;
using DropSpace.Infrastructure.Downloads;

namespace DropSpace.App.Services.Agent;

public sealed class PackagesAgentDomain : AppAgentDomain
{
    public PackagesAgentDomain(MainViewModel main, DownloadManager downloads, DlcManagerService dlc,
        FeatureModuleRuntime modules, AppAgentDesktopControl desktop, IAppStringLocalizer strings)
    {
        DownloadTaskSnapshot Download(JsonElement a) => downloads.Tasks.FirstOrDefault(task=>task.Id==Id(a)) ?? throw new AppAgentCommandException("not_found","Download task not found.");
        string Package(JsonElement a) { var id=String(a,"id",max:128);if(!dlc.Packages.Any(p=>p.Package.Id==id))throw new AppAgentCommandException("not_found","DLC package not found.");return id; }
        string Module(JsonElement a) { var id=String(a,"id",max:128);if(!modules.Snapshots.Any(p=>p.Installation.Id==id))throw new AppAgentCommandException("not_found","Official module not found.");return id; }
        string PackageConfirmation(string id, bool download)
        {
            var p=dlc.Packages.First(x=>x.Package.Id==id).Package;
            var size=p.DownloadBytes is { } bytes ? strings.Format("DlcSizeFormat",(bytes/1_000_000_000d).ToString("0.00",strings.Culture),bytes.ToString("N0",strings.Culture)) : strings.Get("DlcUnknownSize");
            var details=download ? strings.Format("DlcDownloadConfirm",p.Name,size) : strings.Format("DlcDeleteConfirm",p.Name);
            if(download && p.Source is { } source)details+="\n\n"+strings.Format("DlcSource",source);
            if((download?p.DownloadConfirmationResourceKey:p.DeleteConfirmationResourceKey) is { } notice)details+="\n\n"+strings.Get(notice);
            if(!download && id==main.Settings.Lyrics.AiModelId && main.Settings.Lyrics.AiTranslationEnabled)details+="\n\n"+strings.Get("DlcDeleteEnabledModel");
            return details;
        }
        Add("downloads.list","Read ordinary downloads and bounded completed history (limit 0..500).",false,"historyLimit:integer",(a,t)=>Value(new { downloads.RecoveryError,tasks=downloads.GetVisibleTasks(Integer(a,"historyLimit",100,0,500)) }));
        Add("downloads.get","Read an ordinary download by task identifier.",false,"id!:string",(a,t)=>Value(Download(a)));
        Add("downloads.enqueue","Queue an ordinary HTTP(S) download using existing filename/conflict handling and configured output location. Does not claim completion.",true,"url!:string,directory:string,fileName:string",async(a,t)=>
        {
            var url=String(a,"url",max:16384);if(!Uri.TryCreate(url,UriKind.Absolute,out var uri)||uri.Scheme is not ("http" or "https"))throw Invalid("Only HTTP(S) download URLs are supported.");
            var directory=String(a,"directory",main.Settings.DefaultDownloadDirectory);if(string.IsNullOrWhiteSpace(directory))throw Invalid("Choose an output directory.");
            var before=downloads.Tasks.Select(task=>task.Id).ToHashSet();
            await downloads.EnqueueAsync(url,directory,OptionalString(a,"fileName",255));
            return Result(new { status="queued",tasks=downloads.Tasks.Where(task=>!before.Contains(task.Id)).ToArray() });
        });
        Add("downloads.pause","Pause an existing ordinary download.",true,"id!:string",async(a,t)=>{var task=Download(a);await downloads.PauseAsync(task.Id);return Result(Download(a));});
        Add("downloads.resume","Resume an existing paused or failed ordinary download using its existing state policy.",true,"id!:string",async(a,t)=>{var task=Download(a);await downloads.ResumeAsync(task.Id);return Result(Download(a));});
        Add("downloads.cancel","Cancel an existing ordinary download through the downloader's cleanup owner.",true,"id!:string",async(a,t)=>{var task=Download(a);await downloads.CancelAsync(task.Id);return Result(Download(a));});
        Add("downloads.remove_history","Remove completed/cancelled history only; the published file is retained.",true,"id!:string",async(a,t)=>{var task=Download(a);if(task.State is not (DownloadTaskState.Completed or DownloadTaskState.Cancelled))throw new AppAgentCommandException("action_unavailable","Only completed or cancelled history can be removed.");await downloads.RemoveHistoryAsync(task.Id);return Done();});
        Add("dlc.list","Read built-in optional resource package inventory and actual installation/operation state.",false,"",(a,t)=>Value(new { dlc.IsBusy,dlc.Packages }));
        Add("dlc.refresh","Refresh resource package inspection through the existing DLC owner.",true,"",async(a,t)=>{await dlc.RefreshAsync(true);return Result(dlc.Packages);});
        Add("dlc.download","Show native confirmation with the package source and download size, then use the shared resource downloader. Returns actual package state.",true,"id!:string",async(a,t)=>
        {
            var id=Package(a);
            if(!await desktop.ConfirmAsync("dlc.download",PackageConfirmation(id,true),t))return Result(new { status="cancelled" });
            await dlc.DownloadAsync(id,true);return Result(dlc.Packages.First(x=>x.Package.Id==id));
        });
        Add("dlc.delete","Show native confirmation before deleting one app-owned resource package; original user files are unaffected.",true,"id!:string",async(a,t)=>{var id=Package(a);if(!await desktop.ConfirmAsync("dlc.delete",PackageConfirmation(id,false),t))return Result(new { status="cancelled" });await dlc.DeleteAsync(id);return Result(dlc.Packages.First(x=>x.Package.Id==id));});
        Add("dlc.cancel","Request cancellation of a resource download through its existing owner.",true,"id!:string",(a,t)=>{var id=Package(a);dlc.CancelDownload(id);return Value(new { status="requested",id });});
        Add("modules.list","Discover official modules, installed states, declared UI actions and settings. No arbitrary package/assembly loading.",false,"",(a,t)=>Value(new { catalog=modules.AvailablePackages,modules.Snapshots }));
        Add("modules.refresh","Refresh the existing official module catalog.",true,"",async(a,t)=>{await modules.RefreshCatalogAsync(t);return Result(new { catalog=modules.AvailablePackages,modules.Snapshots });});
        Add("modules.install","Install/update a declared official module through verified package staging and compatibility checks.",true,"id!:string",async(a,t)=>{var id=Module(a);await modules.InstallAsync(id);return Result(modules.Snapshots.First(x=>x.Installation.Id==id));});
        Add("modules.enable","Enable an installed official module through its existing runtime.",true,"id!:string",async(a,t)=>{var id=Module(a);await modules.EnableAsync(id);return Result(modules.Snapshots.First(x=>x.Installation.Id==id));});
        Add("modules.disable","Disable a module and retire its worker/UI contributions using the runtime lifecycle owner.",true,"id!:string",async(a,t)=>{var id=Module(a);await modules.DisableAsync(id);return Result(modules.Snapshots.First(x=>x.Installation.Id==id));});
        Add("modules.uninstall","Uninstall a module while retaining module data, using its existing bounded worker retirement and cleanup.",true,"id!:string",async(a,t)=>{var id=Module(a);await modules.UninstallAsync(id);return Result(modules.Snapshots.FirstOrDefault(x=>x.Installation.Id==id));});
        Add("modules.retry_cleanup","Retry pending module cleanup through the scoped package owner.",true,"id!:string",async(a,t)=>{var id=Module(a);await modules.RetryCleanupAsync(id);return Result(modules.Snapshots.First(x=>x.Installation.Id==id));});
        Add("modules.clear_data","Ask in the native app before clearing a disabled module's own data. Never deletes host or other module data.",true,"id!:string",async(a,t)=>{var id=Module(a);if(!await desktop.ConfirmAsync("modules.clear_data",id+"\n\n"+strings.Get("ModuleClearDataConfirm"),t))return Result(new { status="cancelled" });await modules.ClearDataAsync(id);return Result(modules.Snapshots.First(x=>x.Installation.Id==id));});
        Add("modules.settings_get","Read an official module's declared local settings.",false,"id!:string",async(a,t)=>Result(await modules.ReadSettingsAsync(Module(a),t)));
        Add("modules.settings_set","Write a declared module setting through the existing schema and running-generation validator.",true,"id!:string,key!:string,value!:string",async(a,t)=>{var id=Module(a);await modules.WriteSettingAsync(id,String(a,"key",max:128),String(a,"value",max:8192),t);return Result(await modules.ReadSettingsAsync(id,t));});
        Add("modules.invoke","Invoke only an action declared by a running official module's manifest. The runtime validates identity, bounded protocol and generation.",true,"id!:string,action!:string,input!:object",async(a,t)=>Result(await modules.InvokeAsync(Module(a),String(a,"action",max:128),a.GetProperty("input"),t)));
        Add("updates.status","Read current update status, candidate and downloaded package validation state; no network request.",false,"",(a,t)=>Value(main.UpdateStatus));
        Add("updates.check","Run the existing manual update check against the configured update channel.",true,"",async(a,t)=>{if(!main.CanCheckForUpdates)throw new AppAgentCommandException("action_unavailable","An update operation is already active.");await main.CheckForUpdatesManuallyAsync(t);return Result(main.UpdateStatus);});
        Add("updates.download","Download the current update candidate through the existing verified updater.",true,"",async(a,t)=>{if(!main.CanDownloadUpdate)throw new AppAgentCommandException("action_unavailable","No downloadable update candidate is available.");await main.DownloadUpdateAsync(t);return Result(main.UpdateStatus);});
        Add("updates.install","Ask in the native app before requesting normal installation of a ready verified update; the connection may close as the app exits.",true,"",async(a,t)=>{if(!main.CanInstallUpdate)throw new AppAgentCommandException("action_unavailable","No installable update is ready.");if(!await desktop.ConfirmAsync("updates.install","Install the downloaded verified update. DropSpace may close and restart.",t))return Result(new { status="cancelled" });await main.InstallUpdateAsync(t);return Result(main.UpdateStatus);});
        Add("updates.open_location","Reveal the ready update's local download directory through the existing shell action.",true,"",async(a,t)=>Result(new { opened=await main.OpenUpdateLocationAsync(t) }));
        Add("updates.release_notes","Open the current update release notes through the existing validated link action.",true,"",async(a,t)=>Result(new { opened=await main.OpenUpdateReleaseNotesAsync(t) }));
    }
}
