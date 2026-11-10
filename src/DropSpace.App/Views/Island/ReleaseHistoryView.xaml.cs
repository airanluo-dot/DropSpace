using System.Globalization;
using System.Text.Json;
using DropSpace.Core.Abstractions;
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
    private string? _language;

    public ReleaseHistoryView() => InitializeComponent();
    public event RoutedEventHandler? CloseRequested;

    internal void Open(IAppStringLocalizer strings)
    {
        var culture = strings.Culture;
        if (_language != culture.Name)
        {
            Heading.Text = strings.Get("ReleaseHistoryTitle");
            AutomationProperties.SetName(CloseButton, strings.Get("CommonClose"));
            ToolTipService.SetToolTip(CloseButton, strings.Get("CommonClose"));
            var language = culture.Name is "zh-CN" or "zh-TW" ? culture.Name : "en";
            var dateFormat = new Windows.Globalization.DateTimeFormatting.DateTimeFormatter("year month.full day", [culture.Name]);
            Entries.ItemsSource = Archive.Value.EnumerateArray().Select(entry =>
            {
                var tag = entry.GetProperty("tag").GetString()!;
                var channel = tag.Contains("-beta.") ? strings.Get("UpdateChannelBeta.Content")
                    : tag.Contains("-preview.") ? "Preview" : strings.Get("UpdateChannelStable.Content");
                var date = entry.GetProperty("date").GetString();
                return new ReleaseHistoryRow(tag.Replace("macos-", "macOS "), channel,
                    date is null ? null : dateFormat.Format(DateTime.ParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture)),
                    entry.GetProperty(language).EnumerateArray().Select(line => line.GetString()!).ToArray());
            }).ToArray();
            _language = culture.Name;
        }
        HistoryScroll.ChangeView(null, 0, null, true);
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
