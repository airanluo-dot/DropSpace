using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.Core.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;

namespace DropSpace.App.ViewModels;

public sealed class ItemCardViewModel : ObservableObject
{
    private DropItem _item;
    private BitmapImage? _thumbnail;
    private IStorageItem? _dragStorageItem;

    public ItemCardViewModel(DropItem item)
    {
        _item = item;
    }

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
        ?? (Item.Image is null ? string.Empty : $"{Item.Image.PixelWidth} × {Item.Image.PixelHeight} · {FormatBytes(Item.Image.EncodedBytes)}");

    public string SourceLabel => Item.Source == ItemSource.Space ? "Space" : "Clipboard";

    public string KindLabel => Item.Kind switch
    {
        ItemKind.File => "文件",
        ItemKind.Folder => "文件夹",
        ItemKind.Text => "文本",
        ItemKind.Image => "图片",
        ItemKind.Url => "链接",
        ItemKind.Color => "颜色",
        ItemKind.Code => "代码",
        _ => "未知",
    };

    public string StatusLabel => Item.Status switch
    {
        ItemStatus.Available => string.Empty,
        ItemStatus.Missing => "文件不存在",
        ItemStatus.Unavailable => "暂时不可用",
        ItemStatus.Processing => "处理中",
        ItemStatus.Error => "读取失败",
        _ => string.Empty,
    };

    public string CreatedLabel
    {
        get
        {
            var local = Item.CreatedAtUtc.ToLocalTime();
            var now = DateTimeOffset.Now;
            return local.Date == now.Date
                ? local.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture)
                : local.ToString("M月d日 HH:mm", System.Globalization.CultureInfo.CurrentCulture);
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

    public string PinActionLabel => Item.IsPinned ? "取消固定" : "固定";

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

    private static string? CreateTextPreview(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var singleLine = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return singleLine.Length <= 220 ? singleLine : string.Concat(singleLine.AsSpan(0, 219), "…");
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }
}
