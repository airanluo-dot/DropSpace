using System.Collections.ObjectModel;

// Identity-only platform shims. No WinUI rendering, binding, or drag/drop is simulated.
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
    // This host supplies only state; every projection/selection algorithm comes from
    // the source-linked production partial. MainViewModel's wider lifecycle is not exercised.
    public sealed partial class MainViewModel
    {
        private readonly Dictionary<Guid, bool> _batchExpansion = [];

        public ObservableCollection<ItemCardViewModel> Items { get; } = [];
        public bool IsCollectionVisible { get; set; } = true;

        internal void ApplyProjectionForProbe() => ApplyBatchProjectionState();
    }
}
