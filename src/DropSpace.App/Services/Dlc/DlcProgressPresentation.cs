using DropSpace.Core.Abstractions;
using DropSpace.Core.Downloads;

namespace DropSpace.App.Services.Dlc;

internal static class DlcProgressPresentation
{
    internal static string Describe(TrackProgress progress, IAppStringLocalizer strings)
    {
        var text = strings.Get("DownloadStage" + progress.Stage);
        if (progress.Fraction is { } fraction) return (fraction * 100).ToString("0", strings.Culture) + "%";
        if (progress.DownloadedBytes > 0 || progress.Stage == DownloadStage.Transferring)
            text += "\n" + strings.Format("DownloadTransferDetail",
                (progress.DownloadedBytes / 1_048_576d).ToString("0.00", strings.Culture) + " MiB",
                progress.TotalBytes is { } total ? (total / 1_048_576d).ToString("0.00", strings.Culture) + " MiB" : strings.Get("DownloadUnknownSize"),
                (progress.BytesPerSecond / 1_048_576d).ToString("0.00", strings.Culture), progress.ActiveConnections);
        if (progress.Attempt > 0) text += " · " + strings.Format("DownloadRetryDetail", progress.Attempt);
        return text;
    }
}
