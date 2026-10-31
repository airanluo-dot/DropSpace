using System.Collections.ObjectModel;
using DropSpace.App.Services;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Microsoft.UI.Xaml.Media.Imaging
{
    public sealed class BitmapImage;
}

namespace Windows.Storage
{
    public interface IStorageItem;
}

namespace DropSpace.App.ViewModels
{
    // State and native-work boundaries only. Navigation, query construction, projection,
    // paging, failure publication and stale-result decisions are source-linked production code.
    public sealed partial class MainViewModel : IDisposable
    {
        private const int MaximumLiveClipboardItems = 250;
        private readonly ItemProjectionService _projection;
        private readonly IAppStringLocalizer _strings = IdentityAppStringLocalizer.Instance;
        private readonly SemaphoreSlim _projectionLoadGate = new(1, 1);
        private readonly Dictionary<Guid, bool> _batchExpansion = [];
        private readonly HashSet<ProjectionLoadJournal> _projectionLoads = [];
        private readonly Dictionary<ItemCardViewModel, AssetOwner> _assetOwners = [];
        private ProjectionLoadJournal? _clipboardReloadCaptures;
        private ItemQueryCursor? _projectionCursor;
        private readonly ILogger<MainViewModel> _logger = NullLogger<MainViewModel>.Instance;
        private CancellationTokenSource? _queryCancellation;
        private long _reloadRevision;
        private bool _disposed;

        public MainViewModel(IItemRepository repository) => _projection = new(repository);

        public ObservableCollection<ItemCardViewModel> Items { get; } = [];
        public string CurrentSection { get; private set; } = "Space";
        public string SearchText { get; set; } = string.Empty;
        public string PageTitle { get; private set; } = string.Empty;
        public string PageDescription { get; private set; } = string.Empty;
        public bool IsSettingsVisible { get; private set; }
        public bool IsCollectionVisible => !IsSettingsVisible && CurrentSection != "Music";
        public bool IsBusy { get; private set; }
        public bool IsEmpty { get; private set; } = true;
        public bool HasMoreItems { get; private set; }
        public int ItemCount { get; private set; }
        public ItemCardViewModel? SelectedItem { get; set; }
        private AppUiMessage Status { get; set; } = AppUiMessage.Empty;

        internal Task DebouncedReloadForProbeAsync()
        {
            // Historical navigation sources did not include the debounce method.
            // Keep their original scenario groups compilable without copying it.
            var debounce = typeof(MainViewModel).GetMethod("DebouncedReloadAsync",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?? throw new NotSupportedException("Search scenarios require a navigation source containing the production debounce method.");
            return (Task)debounce.Invoke(this, null)!;
        }
        internal AppUiMessage ObservedStatus => Status;
        internal ItemQueryCursor? ObservedCursor => _projectionCursor;
        internal AssetOwner AssetFor(ItemCardViewModel card) => _assetOwners[card];
        internal int ActiveAssetOwners => _assetOwners.Count;

        // The probe never decodes thumbnails, resolves native storage, or evaluates actions.
        private static void RefreshPrimaryQuickActions(ItemCardViewModel card) =>
            ArgumentNullException.ThrowIfNull(card);

        private Task LoadMainCardAssetsAsync(ItemCardViewModel card)
        {
            _assetOwners.Add(card, new AssetOwner());
            return Task.CompletedTask;
        }

        private void CancelAllMainCardAssets()
        {
            foreach (var owner in _assetOwners.Values) owner.Dispose();
            _assetOwners.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _queryCancellation?.Cancel();
            _queryCancellation?.Dispose();
            CancelAllMainCardAssets();
            _projectionLoadGate.Dispose();
        }
    }

    internal sealed class AssetOwner : IDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        internal CancellationToken Token { get; }

        internal AssetOwner() => Token = _stop.Token;

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
