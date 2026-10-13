using DropSpace.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace DropSpace.App.Views;

public sealed partial class MainPage
{
    private readonly Dictionary<Guid, FileTransferRow> _fileTransferRows = [];
    private TransferPresentation? _transferPresentation;

    private void AttachTransferPresentation()
    {
        if (_transferPresentation is not null) return;
        var presentation = new TransferPresentation();
        presentation.Changed = (_, _) => QueueTransferRefresh(presentation);
        Volatile.Write(ref _transferPresentation, presentation);
        // Subscribe before reading: a change between these steps is covered by
        // the read or the queued refresh. Neither carries an old progress value.
        _deviceHandoff.SendsChanged += presentation.Changed;
        FileTransfersTitleText.Text = _strings.Get("FileTransfersTitle");
        FileTransfersEmptyText.Text = _strings.Get("FileTransfersEmpty");
        FileTransfersContinueText.Text = _strings.Get("FileTransfersContinue");
        ClearCompletedTransfersText.Text = _strings.Get("FileTransfersClearCompleted");
        AutomationProperties.SetName(ClearCompletedTransfersButton, _strings.Get("FileTransfersClearCompleted"));
        AutomationProperties.SetName(FileTransfersScroll, _strings.Get("FileTransfersTitle"));
        RefreshFileTransfers(presentation);
    }

    private void RetireTransferPresentation()
    {
        var presentation = Interlocked.Exchange(ref _transferPresentation, null);
        if (presentation is null) return;
        _deviceHandoff.SendsChanged -= presentation.Changed;
        FileTransfersFlyout.Hide();
    }

    private bool IsTransferPresentationCurrent(TransferPresentation presentation) =>
        ReferenceEquals(Volatile.Read(ref _transferPresentation), presentation) &&
        !_dialogLifetime.IsCancellationRequested && IsLoaded;

    private void QueueTransferRefresh(TransferPresentation presentation)
    {
        if (!ReferenceEquals(Volatile.Read(ref _transferPresentation), presentation) ||
            Interlocked.Exchange(ref presentation.RefreshQueued, 1) != 0) return;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref presentation.RefreshQueued, 0);
            if (IsTransferPresentationCurrent(presentation)) RefreshFileTransfers(presentation);
        }))
            Interlocked.Exchange(ref presentation.RefreshQueued, 0);
    }

    private void RefreshFileTransfers(TransferPresentation presentation)
    {
        if (!IsTransferPresentationCurrent(presentation)) return;
        var snapshots = _deviceHandoff.GetSendSnapshots();
        var visible = snapshots.Select(item => item.Id).ToHashSet();
        foreach (var id in _fileTransferRows.Keys.Where(id => !visible.Contains(id)).ToArray())
        {
            FileTransfersList.Children.Remove(_fileTransferRows[id].Root);
            _fileTransferRows.Remove(id);
        }

        foreach (var snapshot in snapshots.Reverse())
        {
            if (!_fileTransferRows.TryGetValue(snapshot.Id, out var row))
            {
                row = new FileTransferRow(this, snapshot);
                _fileTransferRows.Add(snapshot.Id, row);
                // Insert new sends at the top; existing rows and their focused
                // buttons stay mounted as bytes and stages change.
                FileTransfersList.Children.Insert(0, row.Root);
            }
            row.Update(snapshot);
        }

        var active = snapshots.Count(item => !item.IsSettled);
        var title = _strings.Get("FileTransfersTitle");
        var activeText = _strings.Format("FileTransfersActiveCount", active);
        FileTransfersActiveText.Text = activeText;
        FileTransfersActivityBadge.Value = active;
        FileTransfersActivityBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(FileTransfersButton, title);
        AutomationProperties.SetHelpText(FileTransfersButton, activeText);
        ToolTipService.SetToolTip(FileTransfersButton, active > 0 ? string.Concat(title, "\n", activeText) : title);
        FileTransfersEmptyText.Visibility = snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearCompletedTransfersButton.IsEnabled = snapshots.Any(item => item.IsSettled);
    }

    private void StartTrackedFileTransfer(PairedPeer peer, string path, string displayName)
    {
        if (_transferPresentation is not { } presentation || !IsTransferPresentationCurrent(presentation)) return;
        _deviceHandoff.StartFileTransfer(peer.Peer, peer.Endpoint, [path], displayName);
        RefreshFileTransfers(presentation);
        // Only this explicit send opens the panel. Completion, new subscriptions,
        // language changes and background notifications never activate a window.
        if (_presentationActive) FileTransfersFlyout.ShowAt(FileTransfersButton);
    }

    private void OnFileTransfersOpening(object sender, object args)
    {
        if (_transferPresentation is not { } presentation || !IsTransferPresentationCurrent(presentation)) return;
        if (XamlRoot is { } root)
        {
            FileTransfersBody.Width = Math.Max(160, Math.Min(360, root.Size.Width - 64));
            FileTransfersScroll.MaxHeight = Math.Max(80, Math.Min(520, root.Size.Height - 64));
        }
        RefreshFileTransfers(presentation);
    }

    private void OnClearCompletedTransfersClicked(object sender, RoutedEventArgs args)
    {
        if (_transferPresentation is not { } presentation || !IsTransferPresentationCurrent(presentation)) return;
        _deviceHandoff.ClearCompletedSends();
        RefreshFileTransfers(presentation);
    }

    private void CancelFileTransfer(Guid id)
    {
        if (_transferPresentation is not { } presentation || !IsTransferPresentationCurrent(presentation)) return;
        // A finalization report may already have arrived on the worker. The
        // tracker makes the authoritative decision, regardless of this button.
        _deviceHandoff.CancelSend(id);
        RefreshFileTransfers(presentation);
    }

    private string FileTransferMessage(HandoffSendSnapshot snapshot) => snapshot.Stage switch
    {
        HandoffSendStage.Preparing => _strings.Get("FileTransferPreparing"),
        HandoffSendStage.WaitingForApproval => _strings.Get("FileTransferWaitingForApproval"),
        HandoffSendStage.Sending => _strings.Get("FileTransferSending"),
        HandoffSendStage.Finalizing => _strings.Get("FileTransferFinalizing"),
        HandoffSendStage.Cancelling => _strings.Get("FileTransferCanceling"),
        HandoffSendStage.Completed => _strings.Get("TransferSentTitle"),
        HandoffSendStage.Cancelled => _strings.Get("FileTransferCanceled"),
        HandoffSendStage.CancelledOutcomeUnknown => _strings.Get("FileTransferCanceledOutcomeUnknown"),
        _ => snapshot.FailureReason switch
        {
            HandoffSendFailureReason.Disabled => _strings.Get("DevicesDisabledContent"),
            HandoffSendFailureReason.Busy => _strings.Get("TransferBusyContent"),
            HandoffSendFailureReason.PreparationLimit => _strings.Get("TransferPreparationLimitContent"),
            HandoffSendFailureReason.Rejected => _strings.Get("FileTransferRejected"),
            HandoffSendFailureReason.ApprovalTimeout => _strings.Get("FileTransferApprovalTimeout"),
            HandoffSendFailureReason.Network => _strings.Get("FileTransferNetworkError"),
            HandoffSendFailureReason.Source => _strings.Get("FileTransferSourceError"),
            _ => _strings.Get("FileTransferFailed"),
        },
    };

    private string FormatTransferBytes(long value) => value switch
    {
        < 1024 => _strings.Format("Bytes", value),
        < 1024 * 1024 => _strings.Format("Kilobytes", value / 1024d),
        < 1024L * 1024 * 1024 => _strings.Format("Megabytes", value / (1024d * 1024)),
        _ => _strings.Format("Gigabytes", value / (1024d * 1024 * 1024)),
    };

    private sealed class TransferPresentation
    {
        internal EventHandler? Changed;
        internal int RefreshQueued;
    }

    private sealed class FileTransferRow
    {
        private readonly MainPage _owner;
        private readonly Guid _id;
        private readonly TextBlock _name = new() { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        private readonly TextBlock _target = new() { TextWrapping = TextWrapping.Wrap, Opacity = .75 };
        private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _bytes = new() { TextWrapping = TextWrapping.Wrap };
        private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100 };
        private readonly Button _cancel = new() { HorizontalAlignment = HorizontalAlignment.Left };
        private long _revision = -1;

        internal FileTransferRow(MainPage owner, HandoffSendSnapshot snapshot)
        {
            _owner = owner;
            _id = snapshot.Id;
            Root = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 12) };
            Root.Children.Add(_name);
            Root.Children.Add(_target);
            Root.Children.Add(_status);
            Root.Children.Add(_progress);
            Root.Children.Add(_bytes);
            Root.Children.Add(_cancel);
            AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
            _cancel.Click += (_, _) => _owner.CancelFileTransfer(_id);
        }

        internal StackPanel Root { get; }

        internal void Update(HandoffSendSnapshot snapshot)
        {
            if (snapshot.Revision <= _revision) return;
            _revision = snapshot.Revision;
            _name.Text = snapshot.DisplayName;
            _target.Text = _owner._strings.Format("FileTransferTarget", snapshot.PeerDisplayName);
            _status.Text = _owner.FileTransferMessage(snapshot);
            _cancel.Content = _owner._strings.Get("CommonCancel");
            AutomationProperties.SetName(_cancel, _owner._strings.Format("FileTransferCancelAutomationName",
                snapshot.DisplayName, snapshot.PeerDisplayName));
            AutomationProperties.SetName(_progress, snapshot.DisplayName);
            if (!snapshot.CanCancel && _owner._presentationActive && _cancel.FocusState != FocusState.Unfocused)
                _owner.FileTransfersScroll.Focus(FocusState.Programmatic);
            _cancel.Visibility = snapshot.CanCancel ? Visibility.Visible : Visibility.Collapsed;

            var running = snapshot.Stage is HandoffSendStage.Preparing or HandoffSendStage.WaitingForApproval or
                HandoffSendStage.Sending or HandoffSendStage.Finalizing or HandoffSendStage.Cancelling;
            _progress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            _progress.IsIndeterminate = running &&
                (snapshot.Stage != HandoffSendStage.Sending || snapshot.TotalBytes is not > 0);
            _progress.Value = snapshot.TotalBytes is > 0
                ? Math.Clamp(snapshot.TransferredBytes * 100d / snapshot.TotalBytes.Value, 0, 100) : 0;
            var showBytes = snapshot.TotalBytes is not null &&
                snapshot.Stage is HandoffSendStage.Sending or HandoffSendStage.Finalizing or HandoffSendStage.Completed;
            _bytes.Visibility = showBytes ? Visibility.Visible : Visibility.Collapsed;
            if (showBytes)
                _bytes.Text = _owner._strings.Format("FileTransferByteProgress",
                    _owner.FormatTransferBytes(snapshot.TransferredBytes),
                    _owner.FormatTransferBytes(snapshot.TotalBytes!.Value));
        }
    }
}
