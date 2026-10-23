using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Actions;
using DropSpace.Core.Agent;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;

namespace DropSpace.App.Services.Agent;

public sealed class ContentAgentDomain : AppAgentDomain
{
    public ContentAgentDomain(MainViewModel main, IItemRepository repository, IAppStringLocalizer strings,
        IRecentItemsService recent, IItemActionRegistry actions, PinItemsUseCase pins, QuickPreviewService previews,
        ReferenceWindowService reference, AppAgentDesktopControl desktop)
    {
        async Task<DropItem> Read(JsonElement args, CancellationToken token, bool validate = false)
        {
            var item = await repository.GetAsync(Id(args), token) ?? throw new AppAgentCommandException("not_found", "The content record does not exist.");
            if (!validate) return item;
            if (item.Source == ItemSource.Recent || RecentItemMetadata.Read(item.MetadataJson) is not null)
            { await recent.RefreshAsync(item.Id, token); item = await repository.GetAsync(item.Id, token) ?? throw new AppAgentCommandException("not_found", "The content record no longer exists."); }
            var card = new ItemCardViewModel(item, strings);
            var current=await main.ValidateRetrievalItemAsync(card, token);
            return current?.Status==ItemStatus.Available ? current : throw new AppAgentCommandException("content_unavailable", "The content is waiting, missing or unavailable.");
        }
        ItemCardViewModel Card(DropItem item) => new(item, strings);

        Add("items.query", "Search stored names and text across Space, Clipboard, pinned, Recent and successful outputs; no disk indexing. Sources: Space, Clipboard, Recent. Limit 1..250, offset 0..100000.", false,
            "search:string,source:string,pinnedOnly:boolean,kind:string,status:string,limit:integer,offset:integer", async (a,t) =>
            Result((await repository.QueryAsync(new ItemQuery(
                Source: a.TryGetProperty("source", out _) ? EnumValue<ItemSource>(a,"source") : null,
                PinnedOnly: Boolean(a,"pinnedOnly"), Kind: a.TryGetProperty("kind", out _) ? EnumValue<ItemKind>(a,"kind") : null,
                Status: a.TryGetProperty("status", out _) ? EnumValue<ItemStatus>(a,"status") : null,
                Search: OptionalString(a,"search", 4096), Limit: Integer(a,"limit",100,1,250), Offset: Integer(a,"offset",0,0,100000)),t)).Select(item => new
                { item.Id,item.Source,item.Kind,item.Title,item.CreatedAtUtc,item.LastUsedAtUtc,item.IsPinned,item.Status,item.Revision, item.File,
                    textSummary = item.Text?.InlineText is { } text ? text[..Math.Min(text.Length,500)] : null, recent = RecentItemMetadata.Read(item.MetadataJson) })));
        Add("items.get", "Read a stored content object and metadata without activating windows. Inline text is capped at 65536 characters; text.characterCount reports total. Use items.read_text/read_bytes for complete paginated content.",false,"id!:string",async(a,t)=>{var item=await Read(a,t);if(item.Text?.InlineText is { Length:>65536 } text)item=item with { Text=item.Text with { InlineText=text[..65536] } };return Result(item);});
        Add("items.read_text", "Read a page of original stored plain text by UTF-16 character offset (count 1..65536). File objects use items.read_bytes or items.preview.",false,"id!:string,offset:integer,count:integer",async(a,t)=>{var item=await Read(a,t,true);var text=item.Text?.InlineText??throw Invalid("The object has no inline text.");var offset=Integer(a,"offset",0,0,text.Length);var count=Math.Min(Integer(a,"count",65536,1,65536),text.Length-offset);return Result(new { item.Id,item.Revision,offset,totalCharacters=text.Length,text=text.Substring(offset,count),eof=offset+count==text.Length });});
        Add("items.read_bytes", "Read original content bytes from an existing item only. count 1..524288, offset in bytes. Returns base64; no arbitrary path reads. External active writers are rejected.",false,"id!:string,offset:integer,count:integer",async(a,t)=>
        {
            var item=await Read(a,t,true);var count=Integer(a,"count",65536,1,524288);
            var offset=a.TryGetProperty("offset",out var offsetValue)?offsetValue.GetInt64():0;
            if(offset<0)throw Invalid("Offset must be nonnegative.");
            if(item.Text?.InlineText is { } text && item.File is null)
            {
                var content=System.Text.Encoding.UTF8.GetBytes(text);
                if(offset>content.LongLength)throw Invalid("Offset exceeds content length.");
                var segment=content.AsSpan((int)offset,Math.Min(count,content.Length-(int)offset)).ToArray();
                return Result(new { item.Id,item.Revision,offset,totalBytes=content.LongLength,dataBase64=Convert.ToBase64String(segment),eof=offset+segment.Length==content.LongLength });
            }
            var path=previews.ResolveSourcePath(item) ?? throw Invalid("The item has no byte content.");
            if(item.File?.EntryKind==FileEntryKind.Folder)throw Invalid("A folder is not a byte stream.");
            await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.RandomAccess);
            var length=input.Length;var modified=File.GetLastWriteTimeUtc(path);
            if(item.File is { } file && (file.KnownSize is { } size && size!=length || file.KnownModifiedAtUtc is { } time && time.UtcDateTime!=modified))
                throw new AppAgentCommandException("content_changed","The source changed; refresh and read the current object again.");
            if(offset>length)throw Invalid("Offset exceeds content length.");
            input.Position=offset;var bytes=new byte[(int)Math.Min(count,length-offset)];var read=0;
            while(read<bytes.Length){var n=await input.ReadAsync(bytes.AsMemory(read),t);if(n==0)break;read+=n;}
            if(File.GetLastWriteTimeUtc(path)!=modified || input.Length!=length)throw new AppAgentCommandException("content_changed","The source changed while reading.");
            return Result(new { item.Id,item.Revision,offset,totalBytes=length,modifiedAtUtc=modified,dataBase64=Convert.ToBase64String(bytes,0,read),eof=offset+read==length });
        });
        Add("space.add_text", "Add text to Space through its existing content classifier. Maximum follows the configured text limit.",true,"text!:string",async(a,t)=>Result(await main.AddTextToSpaceAsync(String(a,"text",max:2*1024*1024),"agent",cancellationToken:t)));
        Add("space.add_paths", "Add existing file or directory references to Space. Originals remain in place. Returns the accepted count.",true,"paths!:array",async(a,t)=>Result(new { accepted = await main.AddPathsAsync(Strings(a,"paths"),t) }));
        Add("items.copy", "Copy text as text or files as native file objects. Availability and recent-file stability are checked.",true,"id!:string",async(a,t)=>{await main.CopyAsync(Card(await Read(a,t,true)),t);return Done();});
        Add("items.copy_path", "Explicitly copy a file path as plain text; separate from copying a file object.",true,"id!:string",async(a,t)=>{var item=await Read(a,t,true);await main.CopyTextAsync(item.File?.OriginalPath ?? throw Invalid("The item is not a file reference."),t);return Done();});
        Add("items.open", "Open available content through its existing Windows default application action.",true,"id!:string",async(a,t)=>Result(new { opened = await main.OpenAsync(Card(await Read(a,t,true)),t) }));
        Add("items.reveal", "Reveal an available file in Explorer using the existing action.",true,"id!:string",async(a,t)=>{var item=await Read(a,t,true);if(item.File is null)throw Invalid("The item has no external file.");await main.ShowInFolderAsync(Card(item),t);return Result(new { status="requested" });});
        Add("items.relocate", "Replace a missing file reference using the existing relocation validator; does not move external files.",true,"id!:string,path!:string",async(a,t)=>{await main.ReplaceFileReferenceAsync(Card(await Read(a,t)),String(a,"path"),t);return Result(await Read(a,t));});
        Add("items.pin", "Set an object's pinned state using shared undo and mutation ownership.",true,"id!:string,pinned!:boolean",async(a,t)=>{var item=await Read(a,t);var result=await pins.SetAsync([item.Id],Boolean(a,"pinned"),t);await main.RefreshRetainedRetrievalAsync();return Result(result);});
        Add("items.remove", "Remove a record through the existing undo flow. Never deletes the external original.",true,"id!:string",async(a,t)=>{await main.RemoveAsync(Card(await Read(a,t)),t);return Result(new { status="completed",undoAvailable=main.HasUndo });});
        Add("items.undo", "Undo the current reversible workspace operation.",true,"",async(a,t)=>Result(new { restored=await main.UndoAsync(t) }));
        Add("items.export_image", "Export a stored clipboard image using the existing payload exporter. An existing destination requires native overwrite confirmation.",true,"id!:string,path!:string",async(a,t)=>{var item=await Read(a,t,true);var path=Path.GetFullPath(String(a,"path"));if(File.Exists(path)&&!await desktop.ConfirmAsync("items.export_image.overwrite",path,t))return Result(new { status="cancelled" });await main.ExportImageAsync(Card(item),path,t);return Done();});
        Add("items.preview", "Read a bounded existing preview. Image bytes are omitted unless includeBytes is true; width 64..2048, page 1..10000.",false,"id!:string,page:integer,width:integer,includeBytes:boolean",async(a,t)=>{var p=await previews.LoadAsync(await Read(a,t,true),Integer(a,"page",1,1,10000),Integer(a,"width",512,64,2048),false,t);return Result(p with { Bytes = Boolean(a,"includeBytes") ? p.Bytes : null });});
        Add("clipboard.status", "Read capture pause state and clear undo status.",false,"",(a,t)=>Value(new { paused=main.IsClipboardPaused,main.HasUndo,main.UndoState }));
        Add("clipboard.pause", "Pause or resume existing clipboard capture.",true,"paused!:boolean",async(a,t)=>{await main.SetClipboardPausedAsync(Boolean(a,"paused"),t);return Done();});
        Add("clipboard.clear_preview", "Count records affected by LastHour, Today or All clipboard cleanup.",false,"range!:string",async(a,t)=>Result(new { count=await main.GetClearPreviewCountAsync(EnumValue<ClearRange>(a,"range"),t) }));
        Add("clipboard.clear", "Show the existing cleanup warning in the native app, then clear clipboard history with undo. External files are unaffected.",true,"range!:string",async(a,t)=>{var range=EnumValue<ClearRange>(a,"range");var count=await main.GetClearPreviewCountAsync(range,t);if(count==0)return Result(new { removedCount=0 });var rangeLabel=strings.Get(range switch { ClearRange.LastHour=>"ClearRangeLastHour",ClearRange.Today=>"ClearRangeToday",_=>"ClearRangeAll" });var warning=strings.Format("ClearConfirmTitle",rangeLabel)+"\n\n"+strings.Format("ClearConfirmContent",count);if(!await desktop.ConfirmAsync("clipboard.clear",warning,t))return Result(new { status="cancelled" });return Result(await main.ClearClipboardAsync(range,t));});
        Add("recent.directories", "List explicitly selected relay folders and availability reasons. No directory is enabled by this read.",false,"",(a,t)=>Value(recent.Directories));
        Add("recent.directory_add", "Enable relay for one selected local folder's current level. Existing files are not imported.",true,"path!:string",async(a,t)=>{await recent.AddDirectoryAsync(String(a,"path"),t);return Result(recent.Directories);});
        Add("recent.directory_pause", "Pause or resume a configured relay folder.",true,"path!:string,paused!:boolean",async(a,t)=>{await recent.SetDirectoryPausedAsync(String(a,"path"),Boolean(a,"paused"),t);return Result(recent.Directories);});
        Add("recent.directory_remove", "Stop watching a folder. Retained content and original files are unaffected.",true,"path!:string",async(a,t)=>{await recent.RemoveDirectoryAsync(String(a,"path"),t);return Result(recent.Directories);});
        Add("recent.retain", "Keep the same Recent object in Space, preserving identity.",true,"id!:string",async(a,t)=>{await Read(a,t);await recent.RetainAsync(Id(a),t);await main.RefreshRetainedRetrievalAsync();return Result(await Read(a,t));});
        Add("recent.remove", "Remove a recent candidate record without deleting its original.",true,"id!:string",async(a,t)=>{var item=await Read(a,t);if(item.Source!=ItemSource.Recent)throw Invalid("Use items.remove for retained content.");await recent.RemoveAsync(item.Id,t);return Done();});
        Add("recent.refresh", "Recheck a recent or retained file's stability and availability.",true,"id!:string",async(a,t)=>{await Read(a,t);await recent.RefreshAsync(Id(a),t);return Result(await Read(a,t));});
        Add("recent.cleanup", "Apply configured age/count limits to unprotected Recent candidates; never deletes external originals.",true,"",async(a,t)=>{await recent.ApplyRetentionAsync(t);return Done();});
        Add("reference.open", "Explicitly open the single image/text reference window for the same stored object. Missing or waiting content shows its current availability state.",true,"id!:string",async(a,t)=>{var item=await Read(a,t);if(!ReferenceWindowService.CanOpen(item))throw Invalid("Only supported image and text content can be referenced.");await reference.OpenAsync(item,t);return Result(reference.GetState());});
        Add("reference.state", "Read reference-window identity and presentation state without activating it.",false,"",(a,t)=>Value(reference.GetState()));
        Add("reference.close", "Close reference viewing; the content record remains.",true,"",(a,t)=>{reference.Close();return Value(new { status="completed" });});
        Add("reference.topmost", "Set whether the existing reference window stays above other windows; does not activate it.",true,"enabled!:boolean",(a,t)=>{reference.SetAlwaysOnTop(Boolean(a,"enabled"));return Value(reference.GetState());});
        Add("reference.zoom", "Set image scale 0.05..8 or fit to window. Does not activate the reference.",true,"scale:number,fit:boolean",(a,t)=>{if(Boolean(a,"fit"))reference.FitImage();else {if(!a.TryGetProperty("scale",out var scale)||!scale.TryGetDouble(out var n)||!double.IsFinite(n)||n<.05||n>8)throw Invalid("Scale must be between 0.05 and 8.");reference.SetZoom(n);}return Value(reference.GetState());});
        Add("reference.move_resize", "Move/resize the existing reference window in physical screen pixels without activation. Width 320..8192, height 180..8192.",true,"x!:integer,y!:integer,width!:integer,height!:integer",(a,t)=>{reference.SetBounds(Integer(a,"x",0,-100000,100000),Integer(a,"y",0,-100000,100000),Integer(a,"width",640,320,8192),Integer(a,"height",480,180,8192));return Value(reference.GetState());});
        Add("reference.scroll", "Scroll long text or a zoomed image in the existing reference window (DIPs, 0..100000000). Does not activate it.",true,"horizontal:integer,vertical!:integer",(a,t)=>{reference.ScrollTo(Integer(a,"horizontal",0,0,100000000),Integer(a,"vertical",0,0,100000000));return Value(reference.GetState());});
        Add("actions.list", "Discover existing action descriptors and per-item availability.",false,"id!:string",async(a,t)=>Result(actions.Evaluate(new([DropItemSnapshot.FromItem(await Read(a,t))]))));
        Add("actions.execute", "Execute a declared built-in item action (including ZIP and image processing) with shared validation/output registration. Transfer/share actions use their dedicated commands.",true,
            "id:string,ids:array,action!:string,destinationDirectory:string,outputFormat:string,width:integer,height:integer,keepAspectRatio:boolean",async(a,t)=>
        {
            var action=EnumValue<ItemActionId>(a,"action");
            if(action is ItemActionId.SendToDevice or ItemActionId.CreateNearbyLink or ItemActionId.CreateSecureInternetLink)
                throw new AppAgentCommandException("use_dedicated_command","Use devices or shares commands for transfer actions.");
            var selected=new List<DropItem>();
            if(a.TryGetProperty("ids",out _))
            {
                if(a.TryGetProperty("id",out _))throw Invalid("Supply id or ids, not both.");
                foreach(var id in Strings(a,"ids",100,40).Distinct())selected.Add(await Read(Result(new { id }),t,true));
            }
            else selected.Add(await Read(a,t,true));
            var item=selected[0];var selection=new ItemSelectionSnapshot(selected.Select(DropItemSnapshot.FromItem).ToArray());
            var context=new ItemActionContext(selection,OptionalString(a,"destinationDirectory"),OptionalString(a,"outputFormat",32),
                a.TryGetProperty("width",out _) ? Integer(a,"width",1,1,32768) : null,a.TryGetProperty("height",out _) ? Integer(a,"height",1,1,32768) : null,Boolean(a,"keepAspectRatio",true),t);
            return Result(await main.ExecuteQuickActionAsync(Card(item),action,context,selection,t));
        });
    }
}
