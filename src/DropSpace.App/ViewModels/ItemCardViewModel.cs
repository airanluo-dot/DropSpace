using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Actions;
using DropSpace.Core.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using System.Text.Json;

namespace DropSpace.App.ViewModels;

public sealed class ItemCardViewModel : ObservableObject
{
    private DropItem _item;
    private readonly IAppStringLocalizer _strings;
    private BitmapImage? _thumbnail;
    private IStorageItem? _dragStorageItem;
    private bool _isBatchHeader;
    private bool _isBatchMemberVisible = true;
    private bool _isBatchExpanded = true;

    public ItemCardViewModel(DropItem item, IAppStringLocalizer strings)
    {
        _item = item;
        _strings = strings;
    }

    public ObservableCollection<QuickActionButtonViewModel> PrimaryQuickActions { get; } = [];

    public DropItem Item
    {
        get => _item;
        private set
        {
            if (SetProperty(ref _item, value))
            {
                OnPropertyChanged(string.Empty);
            }
        }
    }

    public Guid Id => Item.Id;

    public string Title => Item.Title;

    public string Preview => Item.File?.OriginalPath
        ?? Item.Url?.DisplayUrl
        ?? CreateTextPreview(Item.Text?.InlineText)
        ?? (Item.Image is null
            ? string.Empty
            : _strings.Format("ImagePreview", Item.Image.PixelWidth, Item.Image.PixelHeight, FormatBytes(Item.Image.EncodedBytes)));

    public string SourceLabel => Item.Source == ItemSource.Space
        ? _strings.Get("ItemSourceSpace")
        : _strings.Get("ItemSourceClipboard");

    public string KindLabel => Item.Kind switch
    {
        ItemKind.File => _strings.Get("ItemKindFile"),
        ItemKind.Folder => _strings.Get("ItemKindFolder"),
        ItemKind.Text => _strings.Get("ItemKindText"),
        ItemKind.Image => _strings.Get("ItemKindImage"),
        ItemKind.Url => _strings.Get("ItemKindUrl"),
        ItemKind.Color => _strings.Get("ItemKindColor"),
        ItemKind.Code => _strings.Get("ItemKindCode"),
        _ => _strings.Get("ItemKindUnknown"),
    };

    public string StatusLabel => Item.Status switch
    {
        ItemStatus.Available => string.Empty,
        ItemStatus.Missing => _strings.Get("ItemStatusMissing"),
        ItemStatus.Unavailable => _strings.Get("ItemStatusUnavailable"),
        ItemStatus.Processing => _strings.Get("ItemStatusProcessing"),
        ItemStatus.Error => _strings.Get("ItemStatusError"),
        _ => string.Empty,
    };

    public string CreatedLabel
    {
        get
        {
            var local = Item.CreatedAtUtc.ToLocalTime();
            var now = DateTimeOffset.Now;
            return local.Date == now.Date
                ? local.ToString(_strings.Get("ItemCreatedTodayFormat"), _strings.Culture)
                : local.ToString(_strings.Get("ItemCreatedDateFormat"), _strings.Culture);
        }
    }

    public string Glyph => Item.Kind switch
    {
        ItemKind.File => "\uE8A5",
        ItemKind.Folder => "\uE8B7",
        ItemKind.Text => "\uE8A5",
        ItemKind.Image => "\uEB9F",
        ItemKind.Url => "\uE71B",
        ItemKind.Color => "\uE790",
        ItemKind.Code => "\uE943",
        _ => "\uE9CE",
    };

    public bool IsPinned => Item.IsPinned;

    public DropBatchMetadata? BatchMetadata => TryReadBatchMetadata(Item.MetadataJson);

    public Guid? DropBatchId => BatchMetadata?.DropBatchId;

    public bool IsGrouped => BatchMetadata is { ItemCount: > 1 };

    public bool IsBatchHeader
    {
        get => _isBatchHeader;
        set => SetProperty(ref _isBatchHeader, value);
    }

    public bool IsBatchMemberVisible
    {
        get => _isBatchMemberVisible;
        set => SetProperty(ref _isBatchMemberVisible, value);
    }

    public bool IsBatchExpanded
    {
        get => _isBatchExpanded;
        set
        {
            if (SetProperty(ref _isBatchExpanded, value))
            {
                OnPropertyChanged(nameof(BatchToggleLabel));
            }
        }
    }

    public string BatchLabel => BatchMetadata is { ItemCount: var count }
        ? _strings.Format("DropBatchLabel", count)
        : string.Empty;

    public string BatchToggleLabel => IsBatchExpanded
        ? _strings.Get("CollapseBatch")
        : _strings.Get("ExpandBatch");

    public string PinActionLabel => Item.IsPinned
        ? _strings.Get("ItemUnpin")
        : _strings.Get("ItemPin");

    public bool IsFileReference => Item.File is not null;

    public bool IsMissing => Item.File is not null && Item.Status is ItemStatus.Missing or ItemStatus.Unavailable;

    public bool CanOpen => ItemCapabilities.For(Item).CanOpen;

    public bool CanCopy => ItemCapabilities.For(Item).CanCopy;

    public bool CanExport => ItemCapabilities.For(Item).CanExport;

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }

    public IStorageItem? DragStorageItem
    {
        get => _dragStorageItem;
        set => SetProperty(ref _dragStorageItem, value);
    }

    public void Update(DropItem item) => Item = item;

    private static DropBatchMetadata? TryReadBatchMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<DropBatchMetadata>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? CreateTextPreview(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var singleLine = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return singleLine.Length <= 220 ? singleLine : string.Concat(singleLine.AsSpan(0, 219), "…");
    }

    private string FormatBytes(long bytes)
    {
        return bytes switch
        {
            < 1024 => _strings.Format("Bytes", bytes),
            < 1024 * 1024 => _strings.Format("Kilobytes", bytes / 1024d),
            < 1024L * 1024 * 1024 => _strings.Format("Megabytes", bytes / (1024d * 1024)),
            _ => _strings.Format("Gigabytes", bytes / (1024d * 1024 * 1024)),
        };
    }
}
