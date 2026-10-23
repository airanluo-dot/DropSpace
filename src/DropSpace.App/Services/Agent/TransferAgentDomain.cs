using System.Globalization;
using System.Text.Json;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Agent;
using DropSpace.Core.Content;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using DropSpace.Core.Transfer;
using DropSpace.Infrastructure.Network;
using Microsoft.UI.Dispatching;

namespace DropSpace.App.Services.Agent;

/// <summary>Uses existing trust, discovery, transfer and share owners; callers cannot supply network endpoints.</summary>
public sealed class TransferAgentDomain : AppAgentDomain
{
    public TransferAgentDomain(DeviceHandoffService devices, DeviceHandoffUseCase handoff, CrossDeviceClipboardService clipboard,
        SharingUseCase sharing, SecureInternetShareService internet, TransferRepository transfers, IItemRepository repository,
        IItemContentResolver content, IRecentItemsService recent, MainViewModel main, AppAgentDesktopControl desktop,
        IAppStringLocalizer strings, DispatcherQueue dispatcher)
    {
        Add("devices.status", "Read device handoff and cross-device clipboard status.", false, "", (_, _) => Value(new
        { devices.IsEnabled, devices.Endpoint, devices.FirewallStatus, devices.UnavailableReason, clipboardEnabled = clipboard.IsEnabled, channels = clipboard.Channels }));
        Add("devices.list", "List known peers without exposing device secrets.", false, "", async (_, token) => Result(await devices.GetPeersAsync(token)));
        Add("devices.discover", "Discover nearby devices using the existing enabled handoff service.", false, "seconds:integer", async (args, token) =>
        { RequireHandoff(); return Result(await devices.DiscoverAsync(TimeSpan.FromSeconds(Integer(args, "seconds", 3, 1, 10)), token)); });
        Add("devices.pair", "Pair a discovered Windows device. The user must compare and confirm its SAS in the native App.", true, "deviceId!:string", async (args, token) =>
        {
            RequireHandoff();
            var id = Id(args, "deviceId");
            var device = (await devices.DiscoverAsync(TimeSpan.FromSeconds(3), token)).FirstOrDefault(candidate => candidate.DeviceId == id)
                ?? throw new AppAgentCommandException("device_unavailable", "The selected device was not discovered.");
            RequireHandoff();
            return Result(await devices.PairAsync(device, (sas, cancellation) => dispatcher.EnqueueAsync(() => desktop.ConfirmAsync(
                strings.Format("PairingSasTitleWithDevice", device.DisplayName), strings.Format("PairingSasContent", sas.ToString("D6", CultureInfo.InvariantCulture)), cancellation)), token));
        });
        Add("devices.unpair", "Remove trust and clipboard selection for a paired device after native confirmation.", true, "peerId!:string", async (args, token) =>
        {
            var id = Id(args, "peerId");
            var peer = (await devices.GetPeersAsync(token)).FirstOrDefault(peer => peer.Id == id)
                ?? throw new AppAgentCommandException("not_found", "Peer not found.");
            if (!await desktop.ConfirmAsync("devices.unpair", peer.DisplayName, token)) return Result(new { status = "cancelled" });
            await clipboard.RemovePeerAsync(id, token); await devices.UnpairAsync(id, token); return Done();
        });
        Add("transfers.list", "Read active outgoing transfers.", false, "", (_, _) => Value(handoff.GetSendSnapshots()));
        Add("transfers.history", "Read persisted send/receive history with bounded pagination.", false, "offset:integer,limit:integer", async (args, token) =>
            Result(await transfers.GetSessionsAsync(Integer(args, "offset", 0, 0, 1_000_000), Integer(args, "limit", 100, 1, 500), token)));
        Add("transfers.cancel", "Request cancellation of an outgoing transfer; inspect the task for its final outcome.", true, "id!:string", (args, _) =>
        { var id = Id(args); return Value(new { taskId = id, cancelRequested = handoff.CancelSend(id), tasks = handoff.GetSendSnapshots() }); });
        Add("transfers.clear_completed", "Clear completed outgoing task cards; does not delete files.", true, "", (_, _) =>
        { handoff.ClearCompletedSends(); return Value(new { status = "completed" }); });
        Add("transfers.send_items", "Start sending selected existing file objects to a discovered trusted peer; returns the task ID.", true, "peerId!:string,itemIds!:array", async (args, token) =>
        {
            var peer = await ResolvePeerAsync(Id(args, "peerId"), token);
            var items = await ItemsAsync(args, token);
            var paths = items.Select(item => content.Resolve(DropItemSnapshot.FromItem(item))).Select(source =>
                source.HasReadablePath ? source.ReadablePath! : throw new AppAgentCommandException("content_unavailable", "A selected object is not a readable file or folder.")).ToArray();
            token.ThrowIfCancellationRequested();
            return Result(new { status = "started", taskId = handoff.StartFileTransfer(peer.Peer, peer.Endpoint, paths, items.Count == 1 ? items[0].Title : "DropSpace") });
        });
        Add("transfers.send_text", "Send explicit text or a URL to a discovered trusted peer.", true, "peerId!:string,text!:string,kind:string,label:string", async (args, token) =>
        {
            var peer = await ResolvePeerAsync(Id(args, "peerId"), token);
            return Result(await handoff.SendTextOrUrlAsync(peer.Peer, peer.Endpoint, EnumValue(args, "kind", HandoffMessageKind.Text),
                String(args, "text", max: 65536), OptionalString(args, "label", 256), token));
        });
        Add("transfers.respond", "Approve or reject a current incoming offer using the existing host validation and native confirmation for acceptance.", true,
            "sessionId!:string,accept!:boolean", async (args, token) =>
        {
            var id = Id(args, "sessionId"); var accept = Boolean(args, "accept");
            var session = await transfers.GetSessionAsync(id, token);
            if (session is null || session.Direction != TransferDirection.Receive || session.State != TransferSessionState.AwaitingApproval)
                throw new AppAgentCommandException("not_pending", "The incoming transfer is no longer waiting for approval.");
            var peer = (await devices.GetPeersAsync(token)).FirstOrDefault(peer => peer.Id == session.PeerId);
            var details = $"{peer?.DisplayName ?? session.PeerId?.ToString("D")}\n{strings.Format("IncomingTransferContent", session.ItemCount, session.TotalBytes)}\n{id:D}";
            if (accept && !await desktop.ConfirmAsync("transfers.respond", details, token)) return Result(new { status = "cancelled" });
            return Result(new { applied = await handoff.ApproveIncomingTransferAsync(id, accept, token) });
        });
        Add("clipboard.peers", "Read selected cross-device clipboard channels.", false, "", (_, _) => Value(clipboard.Channels));
        Add("clipboard.configure_peer", "Set a trusted discovered peer's clipboard mode using the existing settings owner.", true, "peerId!:string,mode!:string", async (args, token) =>
        {
            var peer = await ResolvePeerAsync(Id(args, "peerId"), token);
            await clipboard.ConfigurePeerAsync(peer.Peer, peer.Endpoint, EnumValue<ClipboardSyncMode>(args, "mode"), token); return Done();
        });
        Add("clipboard.remove_peer", "Remove one cross-device clipboard channel without unpairing the device.", true, "peerId!:string", async (args, token) =>
        { await clipboard.RemovePeerAsync(Id(args, "peerId"), token); return Done(); });
        Add("clipboard.send_item", "Send an existing supported text, URL or image object to a trusted discovered peer.", true, "peerId!:string,itemId!:string", async (args, token) =>
        {
            var peer = await ResolvePeerAsync(Id(args, "peerId"), token);
            await recent.RefreshAsync(Id(args, "itemId"), token);
            var item = await repository.GetAsync(Id(args, "itemId"), token) ?? throw new AppAgentCommandException("not_found", "Item not found.");
            if (item.Status != ItemStatus.Available) throw new AppAgentCommandException("content_unavailable", "The item is not currently available.");
            RequireHandoff();
            return Result(await clipboard.SendManualAsync(peer.Peer, peer.Endpoint, item, token));
        });
        Add("shares.list", "Read nearby shares and locally revocable encrypted-share IDs; no backend credentials are returned.", false, "", (_, _) =>
            Value(new { nearby = sharing.GetNearbyShares(), internetConfigured = sharing.IsInternetConfigured, internetShareIds = internet.RevocableShareIds }));
        Add("shares.get_nearby", "Read an existing nearby share descriptor, including its user-facing share URL.", false, "id!:string", (args, _) =>
            Value(sharing.GetNearbyShareDescriptor(Id(args)) ?? throw new AppAgentCommandException("not_found", "Share not found.")));
        Add("shares.create_nearby", "Create a nearby share for existing objects using the current App share settings.", true, "itemIds!:array", async (args, token) =>
            Result(await sharing.CreateNearbyAsync(await ItemsAsync(args, token), main.Settings, token)));
        Add("shares.create_internet", "Create an encrypted Internet share using the already configured backend and App limits.", true, "itemIds!:array,lifetimeMinutes:integer", async (args, token) =>
            Result(await sharing.CreateInternetAsync(await ItemsAsync(args, token), main.Settings,
                args.TryGetProperty("lifetimeMinutes", out _) ? TimeSpan.FromMinutes(Integer(args, "lifetimeMinutes", 60, 1, 10080)) : null, token)));
        Add("shares.revoke_nearby", "Revoke a nearby share without deleting its original files.", true, "id!:string", (args, _) => Value(new { revoked = sharing.RevokeNearby(Id(args)) }));
        Add("shares.revoke_internet", "Revoke an encrypted Internet share using the locally owned revoke handle.", true, "id!:string", async (args, token) =>
            Result(new { revoked = await sharing.RevokeInternetAsync(Id(args), token) }));

        async Task<(PeerDevice Peer, Uri Endpoint)> ResolvePeerAsync(Guid id, CancellationToken token)
        {
            RequireHandoff();
            var matches = await devices.DiscoverTrustedPeersAsync(TimeSpan.FromSeconds(3), token);
            RequireHandoff();
            foreach (var match in matches) if (match.Peer.Id == id) return match;
            throw new AppAgentCommandException("peer_unavailable", "The peer must be paired, trusted and discoverable on the enabled device service.");
        }
        void RequireHandoff()
        {
            if (!devices.IsEnabled) throw new AppAgentCommandException("feature_disabled", "Device handoff is disabled in the App settings.");
        }
        async Task<IReadOnlyList<DropItem>> ItemsAsync(JsonElement args, CancellationToken token)
        {
            var ids = Strings(args, "itemIds", 100, 40).Select(value => Guid.TryParse(value, out var id) ? id : throw Invalid("Invalid item identifier.")).Distinct();
            var result = new List<DropItem>();
            foreach (var id in ids)
            {
                await recent.RefreshAsync(id, token);
                var item = await repository.GetAsync(id, token) ?? throw new AppAgentCommandException("not_found", "Item not found.");
                if (item.Status != ItemStatus.Available) throw new AppAgentCommandException("content_unavailable", "A selected item is not currently available.");
                result.Add(item);
            }
            return result;
        }
    }
}
