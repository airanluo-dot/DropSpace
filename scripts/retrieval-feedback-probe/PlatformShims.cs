using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Preview;
using RetrievalFeedbackProbe;

namespace Microsoft.UI.Xaml.Media.Imaging { public sealed class BitmapImage; }
namespace Windows.Storage { public interface IStorageItem; }
namespace Microsoft.UI.Dispatching
{
    public sealed class DispatcherQueue
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action callback) { callback(); return true; }
    }
}

namespace DropSpace.App.Services
{
    public sealed class ClipboardCaptureService
    {
        public event EventHandler<DropItem>? ItemCaptured { add { } remove { } }
        public event EventHandler<DropItem>? ItemImported { add { } remove { } }
        public Task CopyTextAsync(string text) => throw new NotSupportedException();
    }
    public sealed class QuickPreviewService
    {
        public Task<PreviewDescriptor> LoadAsync(DropItem item, bool inline) => throw new NotSupportedException();
    }
    public sealed class ReferenceWindowService
    {
        public static bool CanOpen(DropItem item) => true;
        public Task OpenAsync(DropItem item) => throw new NotSupportedException();
    }
}

namespace DropSpace.App.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        internal ReadBoundary Reads { get; } = new();
        internal bool FailCopy { get; set; }
        public AppSettings Settings { get; } = new();
        public AppLanguagePreference Language => Settings.Language;
        public event EventHandler<SpaceProjectionChangedEventArgs>? SpaceProjectionChanged { add { } remove { } }
        internal Task<IReadOnlyList<ItemCardViewModel>> QueryRetrievalItemsAsync(string query, ItemSource? source,
            bool pinnedOnly, CancellationToken token) => Reads.Start(token);
        internal Task<ItemCardViewModel?> ReadRetrievalItemAsync(Guid id, CancellationToken token) =>
            Task.FromResult<ItemCardViewModel?>(null);
        internal Task<DropItem?> ValidateRetrievalItemAsync(ItemCardViewModel card, CancellationToken token) =>
            Task.FromResult<DropItem?>(card.Item);
        public Task CopyAsync(ItemCardViewModel card) => FailCopy
            ? Task.FromException(new IOException("Fixture copy failure.")) : Task.CompletedTask;
        public Task RemoveAsync(ItemCardViewModel card) => throw new NotSupportedException();
        internal Task RefreshRetainedRetrievalAsync() => throw new NotSupportedException();
    }
    public sealed record SpaceProjectionChangedEventArgs(long Revision, int ItemCount);
}

namespace RetrievalFeedbackProbe
{
    internal sealed class UnusedRecent : IRecentItemsService
    {
        public event EventHandler? Changed { add { } remove { } }
        public IReadOnlyList<RelayDirectoryStatus> Directories => [];
        public Task StartAsync(CancellationToken token = default) => throw new NotSupportedException();
        public Task AddDirectoryAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
        public Task SetDirectoryPausedAsync(string path, bool paused, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemoveDirectoryAsync(string path, CancellationToken token = default) => throw new NotSupportedException();
        public Task<DropItem?> RegisterOutputAsync(string path, string operation, bool partial = false, CancellationToken token = default) => throw new NotSupportedException();
        public Task RetainAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task RefreshAsync(Guid id, CancellationToken token = default) => throw new NotSupportedException();
        public Task ApplyRetentionAsync(CancellationToken token = default) => throw new NotSupportedException();
        public IDisposable Protect(Guid id) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
