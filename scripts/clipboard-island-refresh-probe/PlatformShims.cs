using System.Threading.Channels;
using CommunityToolkit.Mvvm.ComponentModel;
using ClipboardIslandRefreshProbe;
using DropSpace.Core.Models;

// Narrow identity/dispatch boundaries only. No Windows or WinUI runtime is emulated.
namespace Microsoft.UI.Xaml.Media.Imaging { public sealed class BitmapImage; }
namespace Windows.Storage { public interface IStorageItem; }

namespace Microsoft.UI.Dispatching
{
    public delegate void DispatcherQueueHandler();

    public sealed class DispatcherQueue
    {
        private readonly Channel<DispatcherQueueHandler> _pending = Channel.CreateUnbounded<DispatcherQueueHandler>();
        private bool _shutdown;

        public bool TryEnqueue(DispatcherQueueHandler callback)
        {
            if (_shutdown) { callback(); return true; }
            return _pending.Writer.TryWrite(callback);
        }

        internal Task<DispatcherQueueHandler> NextAsync() =>
            _pending.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        internal void ReleaseForShutdown()
        {
            _shutdown = true;
            while (_pending.Reader.TryRead(out var callback)) callback();
        }
    }
}

namespace DropSpace.App.Services
{
    public sealed class ClipboardCaptureService
    {
        public event EventHandler<DropItem>? ItemCaptured;
        public event EventHandler<DropItem>? ItemImported;
        internal void Publish(DropItem item) => ItemCaptured?.Invoke(this, item);
        internal void PublishImported(DropItem item) => ItemImported?.Invoke(this, item);
    }
}

namespace DropSpace.App.ViewModels
{
    // The actual clipboard island owns its worker, visibility, publication and error logic.
    // Only the upstream main-view-model operations are controlled here.
    public sealed class MainViewModel : ObservableObject
    {
        internal ControlledBoundary<IReadOnlyList<ItemCardViewModel>> Reads { get; } = new();
        internal ControlledBoundary<bool> Copies { get; } = new();
        internal ControlledBoundary<DropItem> Pins { get; } = new();
        public AppSettings Settings { get; } = new();
        public AppLanguagePreference Language => Settings.Language;
        public string StatusMessage => string.Empty;
        public bool IsClipboardPaused => Settings.ClipboardPaused;

        public Task<IReadOnlyList<ItemCardViewModel>> GetRecentSourceItemsAsync(
            ItemSource source, int limit, CancellationToken cancellationToken = default)
        {
            if (source != ItemSource.Clipboard || limit != 20)
                throw new InvalidOperationException("Unexpected recent-items request.");
            return Reads.Start(cancellationToken);
        }

        public async Task CopyAsync(ItemCardViewModel card, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(card);
            await Copies.Start(cancellationToken);
        }
        public async Task TogglePinAsync(ItemCardViewModel card)
        {
            ArgumentNullException.ThrowIfNull(card);
            // The fixture supplies the state returned by a completed upstream pin action.
            // Pin admission, reentry prevention and state notifications belong to linked code.
            card.Update(await Pins.Start(CancellationToken.None));
        }
        public Task RemoveAsync(ItemCardViewModel card) => throw new NotSupportedException("Remove is outside this probe.");
        public Task SetClipboardPausedAsync(bool paused) => throw new NotSupportedException("Pause is outside this probe.");
        internal long ReserveNavigation() => throw new NotSupportedException("Navigation is outside this probe.");
        internal bool IsNavigationCurrent(long revision) => throw new NotSupportedException("Navigation is outside this probe.");
        public Task NavigateAsync(string section, long navigationRevision) => throw new NotSupportedException("Navigation is outside this probe.");
    }
}
