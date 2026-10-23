using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DropSpace.App.Services.Diagnostics;

internal static partial class LocalDeliveryCheck
{
    // Real WinUI controls and native clipboard are exercised here. The composition
    // hooks model control callbacks; this is not evidence of physical IME input.
    private static async Task VerifyRetrievalAsync(ServiceProvider services, Window host, List<string> checks)
    {
        void Check(bool passed, string description)
        {
            if (!passed) throw new InvalidOperationException(description);
            checks.Add(description);
        }
        var main = services.GetRequiredService<MainViewModel>();
        var viewModel = services.GetRequiredService<IslandRetrievalViewModel>();
        var repository = services.GetRequiredService<IItemRepository>();
        var recent = services.GetRequiredService<IRecentItemsService>();
        var overlay = services.GetRequiredService<OverlayWindowService>().DiagnosticActiveWindow;
        var experience = services.GetRequiredService<DropSpace.Core.Island.IslandExperienceCoordinator>();
        var state = services.GetRequiredService<OverlayStateMachine>();
        var pageBefore = experience.Current.Page;
        host.Activate();
        var workWindow = WinRT.Interop.WindowNative.GetWindowHandle(host);
        state.OpenQuickPanel(); experience.Open(); await Task.Delay(350);
        Check(overlay.TryActivateQuickPanel(retrieval: true, workWindow), "Actual island opens its retrieval controls from explicit activation");
        var view = overlay.DiagnosticRetrievalView;
        await UntilAsync(() => viewModel.Items.Count > 1, "retrieval initial items");
        Check(view.DiagnosticSearchBox.FocusState != FocusState.Unfocused && experience.Current.Page == pageBefore,
            "Retrieval focuses its real search TextBox without selecting a different island page");

        view.DiagnosticSearchBox.Text = "接力图像";
        await UntilAsync(() => viewModel.Items.Count > 0 && viewModel.Items.All(card => card.Title.Contains("接力图像", StringComparison.Ordinal)), "retrieval search");
        var image = viewModel.Items.First(card => card.Item.File?.OriginalPath.EndsWith("接力图像.png", StringComparison.Ordinal) == true);
        viewModel.SelectedItem = image;
        var dragData = view.DiagnosticBuildDragData()?.GetView();
        Check(dragData is not null && dragData.Contains(StandardDataFormats.StorageItems) &&
            (await dragData.GetStorageItemsAsync()).Single().Path == image.Item.File?.OriginalPath,
            "Island drag-data builder supplies the selected real storage item, not its path text");
        using (var heldWriter = new FileStream(image.Item.File!.OriginalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            Check(view.DiagnosticBuildDragData() is null,
                "Drag rejects an active writer even when size and metadata are unchanged and no watcher event has arrived");
        await view.DiagnosticCopyAsync();
        var clipboard = Clipboard.GetContent();
        Check(clipboard.Contains(StandardDataFormats.StorageItems) && (await clipboard.GetStorageItemsAsync()).Single().Path == image.Item.File?.OriginalPath,
            "Island primary copy places a real file object on the Windows clipboard");
        await Task.Delay(150);
        Check(GetForegroundWindow() == workWindow, "Successful island copy restores the prior work window");

        state.OpenQuickPanel(); experience.Open(); await Task.Delay(350);
        Check(overlay.TryActivateQuickPanel(retrieval: true, workWindow), "Retrieval can reopen after taking a result");
        view.DiagnosticSearchBox.Text = string.Empty;
        await Task.Delay(600);
        viewModel.SelectedItem = viewModel.Items.First(card => card.Id == image.Id);
        var originalRow = viewModel.SelectedItem;
        view.DiagnosticSetComposition(true);
        view.DiagnosticSearchBox.Text = "正在合成";
        view.DiagnosticMoveSelection(1);
        await Task.Delay(300);
        Check(ReferenceEquals(viewModel.SelectedItem, originalRow), "Simulated IME composition callbacks preserve selection and defer query/navigation");
        view.DiagnosticSetComposition(false);
        view.DiagnosticSearchBox.Text = string.Empty;
        await Task.Delay(600);
        viewModel.SelectedItem = viewModel.Items.First(card => card.Id == image.Id);

        viewModel.BeginInteraction();
        var beforeArrival = viewModel.Items.ToArray();
        for (var index = 0; index < 300; index++)
        {
            var text = $"Retrieval arrival fixture {index:000}";
            await repository.AddSpaceTextAsync(new TextCandidate(text, Guid.NewGuid().ToString("N"), ItemKind.Text,
                text, DetectedSubtype.Plain, DetectionConfidence.High, null));
        }
        viewModel.RequestRefresh(); await Task.Delay(300);
        Check(beforeArrival.SequenceEqual(viewModel.Items) && ReferenceEquals(viewModel.SelectedItem, originalRow),
            "The projection lock used during drag keeps row objects and selection unchanged on passive arrivals");
        viewModel.EndInteraction();
        await UntilAsync(() => viewModel.Items.Count == 101, "selected overflow row");
        Check(ReferenceEquals(viewModel.SelectedItem, originalRow) && viewModel.Items.Any(card => card.Id == image.Id),
            "Three hundred new entries cannot evict or change the selected result beyond the newest-100 query bound");

        var selectedIndex = viewModel.Items.IndexOf(viewModel.SelectedItem!);
        view.DiagnosticMoveSelection(1);
        Check(viewModel.Items.IndexOf(viewModel.SelectedItem!) == Math.Min(selectedIndex + 1, viewModel.Items.Count - 1),
            "Shared arrow-navigation handler advances exactly one actual result row");
        view.DiagnosticSearchBox.Text = "接力图像";
        await UntilAsync(() => viewModel.Items.Count > 0 && viewModel.Items.All(card => card.Title.Contains("接力图像", StringComparison.Ordinal)), "restore screenshot query");
        viewModel.SelectedItem = viewModel.Items.First(card => card.Id == image.Id);
        Check((await repository.GetAsync(image.Id))?.Source == ItemSource.Space && recent.Directories.Count == 1,
            "Retrieval and relay preserve the retained shared object and configured folder");
        await main.RefreshRetainedRetrievalAsync();
        await viewModel.OpenReferenceAsync();
        overlay.TryActivateQuickPanel();
    }

    private static async Task ApplyRetrievalCommandAsync(ServiceProvider services, Window host, string command)
    {
        var experience = services.GetRequiredService<DropSpace.Core.Island.IslandExperienceCoordinator>();
        var state = services.GetRequiredService<OverlayStateMachine>();
        if (command == "collapse") { experience.Collapse(); state.Collapse(); return; }
        if (command is not ("image" or "text") && !command.StartsWith("search:", StringComparison.Ordinal)) return;
        var overlay = services.GetRequiredService<OverlayWindowService>().DiagnosticActiveWindow;
        var viewModel = services.GetRequiredService<IslandRetrievalViewModel>();
        state.OpenQuickPanel(); experience.Open(); await Task.Delay(350);
        if (!overlay.TryActivateQuickPanel(retrieval: true, WinRT.Interop.WindowNative.GetWindowHandle(host))) return;
        var query = command == "image" ? "接力图像" : command == "text" ? "长文本参照" : command[7..];
        overlay.DiagnosticRetrievalView.DiagnosticSearchBox.Text = query;
        await Task.Delay(800);
        if (command.StartsWith("search:", StringComparison.Ordinal)) return;
        viewModel.SelectedItem = viewModel.Items.FirstOrDefault(card => command == "image"
            ? card.Item.File?.OriginalPath.EndsWith("接力图像.png", StringComparison.Ordinal) == true
            : card.Title.Contains("长文本参照", StringComparison.Ordinal));
        await viewModel.OpenReferenceAsync();
    }

    private static async Task UntilAsync(Func<bool> condition, string operation)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(80);
        if (!condition()) throw new TimeoutException(operation);
    }
}
