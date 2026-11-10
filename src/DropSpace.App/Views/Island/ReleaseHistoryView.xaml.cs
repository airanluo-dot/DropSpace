using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Updates;
using Windows.Globalization.DateTimeFormatting;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views.Island;

public sealed partial class ReleaseHistoryView : UserControl
{
    private static readonly Lazy<JsonElement> Archive = new(() =>
    {
        using var stream = typeof(ReleaseHistoryView).Assembly.GetManifestResourceStream("DropSpace.ReleaseHistory.json")!;
        return JsonSerializer.Deserialize<JsonElement>(stream);
    });
    private const int PageSize = 5;
    private readonly ObservableCollection<ReleaseHistoryRow> _rows = [];
    private JsonElement.ArrayEnumerator _remaining;
    private bool _hasNext;
    private string _language = "en";
    private string _beta = "", _stable = "";
    private DateTimeFormatter? _dateFormat;
    internal UpdateChannel Channel { get; private set; }

    public ReleaseHistoryView()
    {
        InitializeComponent();
        Entries.ItemsSource = _rows;
    }

    public event RoutedEventHandler? CloseRequested;

    internal void Open(IAppStringLocalizer strings, UpdateChannel channel)
    {
        Channel = channel;
        var culture = strings.Culture.Name;
        _language = culture is "zh-CN" or "zh-TW" ? culture : "en";
        _beta = strings.Get("UpdateChannelBeta.Content");
        _stable = strings.Get("UpdateChannelStable.Content");
        _dateFormat = new DateTimeFormatter("year month.full day", [culture]);
        Heading.Text = strings.Get("ReleaseHistoryTitle");
        LoadMoreLabel.Text = strings.Get("ReleaseHistoryLoadMore");
        AutomationProperties.SetName(LoadMore, LoadMoreLabel.Text);
        AutomationProperties.SetName(CloseButton, strings.Get("CommonClose"));
        ToolTipService.SetToolTip(CloseButton, strings.Get("CommonClose"));
        _rows.Clear();
        _remaining = Archive.Value.EnumerateArray();
        _hasNext = AdvanceToNextRelease();
        AppendPage();
        HistoryScroll.ChangeView(null, 0, null, true);
    }

    private bool AdvanceToNextRelease()
    {
        while (_remaining.MoveNext())
        {
            var tag = _remaining.Current.GetProperty("tag").GetString();
            // Windows tags follow ReleaseVersion. Mac tags have a separate prefix.
            if (ReleaseVersion.TryParse(tag, out var version) && (Channel == UpdateChannel.Beta || !version.IsPrerelease))
                return true;
        }
        return false;
    }

    private void AppendPage()
    {
        for (var i = 0; i < PageSize && _hasNext; i++)
        {
            var entry = _remaining.Current;
            var tag = entry.GetProperty("tag").GetString()!;
            var channel = tag.Contains("-beta.") ? _beta : tag.Contains("-preview.") ? "Preview" : _stable;
            var date = entry.GetProperty("date").GetString();
            _rows.Add(new ReleaseHistoryRow(tag, channel,
                date is null ? null : _dateFormat!.Format(DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                entry.GetProperty(_language).EnumerateArray().Select(line => line.GetString()!).ToArray()));
            _hasNext = AdvanceToNextRelease();
        }
        LoadMore.Visibility = _hasNext ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLoadMore(object sender, RoutedEventArgs args)
    {
        FocusHistory();
        AppendPage();
    }

    internal bool FocusHistory() => HistoryScroll.Focus(FocusState.Programmatic);
    private void OnCloseClick(object sender, RoutedEventArgs args) => CloseRequested?.Invoke(this, args);

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs args) =>
        PageLayout.Padding = args.NewSize.Width < 540 ? new(18, 12, 18, 12) : new(32, 20, 32, 18);

    private void OnEntrySizeChanged(object sender, SizeChangedEventArgs args)
    {
        var grid = (Grid)sender;
        var narrow = args.NewSize.Width < 520;
        var notes = (ItemsControl)grid.Children[1];
        Grid.SetColumn(notes, narrow ? 0 : 1);
        Grid.SetColumnSpan(notes, narrow ? 2 : 1);
        Grid.SetRow(notes, narrow ? 1 : 0);
        grid.RowSpacing = narrow ? 8 : 0;
    }
}

public sealed record ReleaseHistoryRow(string Version, string Channel, string? Date, string[] Notes)
{
    public Visibility DateVisibility => Date is null ? Visibility.Collapsed : Visibility.Visible;
}
