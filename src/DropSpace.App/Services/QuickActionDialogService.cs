using DropSpace.Core.Abstractions;
using DropSpace.Core.Actions;
using DropSpace.Core.Preview;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DropSpace.App.Services;

/// <summary>
/// Collects explicit parameters for actions whose output cannot be chosen safely by convention.
/// The returned context is still executed by the central action registry.
/// </summary>
public sealed class QuickActionDialogService(
    IAppStringLocalizer strings,
    ILogger<QuickActionDialogService> logger) : IDisposable
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);

    public async Task<ItemActionContext?> RequestAsync(
        ItemSelectionSnapshot selection,
        ItemActionId actionId,
        XamlRoot xamlRoot,
        nint ownerHandle,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(xamlRoot);
        cancellationToken.ThrowIfCancellationRequested();

        if (!RequiresParameters(actionId))
        {
            return new ItemActionContext(selection, CancellationToken: cancellationToken);
        }

        await _dialogGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            return await ShowParametersAsync(selection, actionId, xamlRoot, ownerHandle, cancellationToken)
                .ConfigureAwait(true);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    public async Task ShowResultAsync(
        ItemActionResult result,
        XamlRoot xamlRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(xamlRoot);
        await _dialogGate.WaitAsync(cancellationToken).ConfigureAwait(true);
        try
        {
            var messageKey = result.MessageResourceKey ?? "ActionUnavailable";
            var content = result.OutputPaths.Count == 0
                ? strings.Get(messageKey)
                : strings.Format("ActionOutputSaved", string.Join(Environment.NewLine, result.OutputPaths));
            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = strings.Get(messageKey),
                Content = content,
                CloseButtonText = strings.Get("CommonAcknowledge"),
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    public void Dispose() => _dialogGate.Dispose();

    private async Task<ItemActionContext?> ShowParametersAsync(
        ItemSelectionSnapshot selection,
        ItemActionId actionId,
        XamlRoot xamlRoot,
        nint ownerHandle,
        CancellationToken cancellationToken)
    {
        var destination = new TextBox
        {
            Header = strings.Get("QuickActionDestination"),
            PlaceholderText = strings.Get("QuickActionDefaultDestination"),
            IsReadOnly = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var chooseFolder = new Button
        {
            Content = strings.Get("QuickActionChooseFolder"),
            Margin = new Thickness(8, 0, 0, 0),
        };
        var destinationRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        destination.Width = 360;
        destinationRow.Children.Add(destination);
        destinationRow.Children.Add(chooseFolder);

        var format = selection.IsSingle ? CreateFormatSelector(selection.Single, actionId) : null;
        var width = new NumberBox
        {
            Header = strings.Get("QuickActionWidth"),
            PlaceholderText = strings.Get("QuickActionRequiredValue"),
            Minimum = 1,
            Maximum = 16_384,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var height = new NumberBox
        {
            Header = strings.Get("QuickActionHeight"),
            PlaceholderText = strings.Get("QuickActionRequiredValue"),
            Minimum = 1,
            Maximum = 16_384,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var sizeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
        };
        sizeRow.Children.Add(width);
        sizeRow.Children.Add(height);

        var keepAspect = new CheckBox
        {
            Content = strings.Get("QuickActionKeepAspectRatio"),
            IsChecked = true,
        };
        var resizeHint = new TextBlock
        {
            Text = strings.Get("QuickActionResizeOptional"),
            TextWrapping = TextWrapping.Wrap,
            Visibility = actionId == ItemActionId.ConvertImage ? Visibility.Visible : Visibility.Collapsed,
        };
        var error = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Red),
            TextWrapping = TextWrapping.Wrap,
        };

        chooseFolder.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker();
                picker.FileTypeFilter.Add("*");
                if (ownerHandle != 0)
                {
                    InitializeWithWindow.Initialize(picker, ownerHandle);
                }

                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null)
                {
                    destination.Text = folder.Path;
                    error.Text = string.Empty;
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "The quick-action export folder picker failed.");
                error.Text = strings.Get("QuickActionFolderPickerFailed");
            }
        };

        var content = new StackPanel
        {
            Spacing = 12,
            MinWidth = 520,
        };
        content.Children.Add(new TextBlock
        {
            Text = strings.Get("QuickActionParametersDescription"),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(destinationRow);
        if (format is not null)
        {
            content.Children.Add(format);
        }

        if (actionId is ItemActionId.ResizeImage or ItemActionId.ConvertImage)
        {
            content.Children.Add(resizeHint);
            content.Children.Add(sizeRow);
            content.Children.Add(keepAspect);
        }

        content.Children.Add(error);
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = strings.Get("QuickActionParametersTitle"),
            Content = content,
            PrimaryButtonText = strings.Get("QuickActionApply"),
            CloseButtonText = strings.Get("QuickActionCancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }

            var parsedWidth = ReadNumber(width);
            var parsedHeight = ReadNumber(height);
            if (actionId == ItemActionId.ResizeImage && (parsedWidth is null || parsedHeight is null))
            {
                error.Text = strings.Get("ActionParametersRequired");
                continue;
            }

            if (actionId == ItemActionId.ConvertImage && parsedWidth.HasValue != parsedHeight.HasValue)
            {
                error.Text = strings.Get("ActionParametersRequired");
                continue;
            }

            var outputFormat = format is null ? null : GetSelectedFormat(format);
            if (actionId == ItemActionId.ConvertImage && string.IsNullOrWhiteSpace(outputFormat))
            {
                error.Text = strings.Get("ActionParametersRequired");
                continue;
            }

            return new ItemActionContext(
                selection,
                string.IsNullOrWhiteSpace(destination.Text) ? null : destination.Text,
                outputFormat,
                parsedWidth,
                parsedHeight,
                keepAspect.IsChecked is not false,
                cancellationToken);
        }
    }

    private ComboBox? CreateFormatSelector(DropItemSnapshot item, ItemActionId actionId)
    {
        if (actionId is not (ItemActionId.ResizeImage or ItemActionId.ConvertImage or ItemActionId.StripMetadata))
        {
            return null;
        }

        var selector = new ComboBox
        {
            Header = strings.Get("QuickActionOutputFormat"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (actionId == ItemActionId.StripMetadata)
        {
            selector.Items.Add(new ComboBoxItem
            {
                Content = strings.Get("QuickActionKeepOriginalFormat"),
                Tag = null,
            });
        }

        selector.Items.Add(CreateFormatItem(".png", "QuickActionFormatPng"));
        selector.Items.Add(CreateFormatItem(".jpg", "QuickActionFormatJpeg"));
        selector.Items.Add(CreateFormatItem(".bmp", "QuickActionFormatBmp"));
        selector.SelectedIndex = actionId == ItemActionId.ConvertImage
            ? FindFormatIndex(selector, ".jpg")
            : FindFormatIndex(selector, NormalizeFormat(item.Extension) ?? ".png");
        return selector;
    }

    private ComboBoxItem CreateFormatItem(string format, string resourceKey) => new()
    {
        Content = strings.Get(resourceKey),
        Tag = format,
    };

    private static int FindFormatIndex(ComboBox selector, string format)
    {
        for (var index = 0; index < selector.Items.Count; index++)
        {
            if (selector.Items[index] is ComboBoxItem { Tag: string selected } &&
                string.Equals(selected, format, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 0;
    }

    private static string? GetSelectedFormat(ComboBox selector) =>
        (selector.SelectedItem as ComboBoxItem)?.Tag as string;

    private static string? NormalizeFormat(string? extension) => extension?.Trim().ToLowerInvariant() switch
    {
        ".png" or "png" => ".png",
        ".jpg" or ".jpeg" or "jpg" or "jpeg" => ".jpg",
        ".bmp" or "bmp" => ".bmp",
        _ => null,
    };

    private static int? ReadNumber(NumberBox numberBox)
    {
        var value = numberBox.Value;
        if (double.IsNaN(value) || double.IsInfinity(value) || value != Math.Truncate(value))
        {
            return null;
        }

        return value is < 1 or > 16_384 ? null : (int)value;
    }

    private static bool RequiresParameters(ItemActionId actionId) => actionId is
        ItemActionId.HashSha256 or
        ItemActionId.CompressZip or
        ItemActionId.GenerateQr or
        ItemActionId.ResizeImage or
        ItemActionId.ConvertImage or
        ItemActionId.StripMetadata;
}
