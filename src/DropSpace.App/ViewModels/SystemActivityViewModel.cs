using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.Core.Abstractions;
using DropSpace.Core.SystemActivities;

namespace DropSpace.App.ViewModels;

public sealed class SystemActivityViewModel(IAppStringLocalizer strings) : ObservableObject
{
    private string _title = string.Empty, _detail = string.Empty, _glyph = "\uE7F4";
    private bool _isVolume;
    private double _percent;
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public string Glyph { get => _glyph; private set => SetProperty(ref _glyph, value); }
    public bool IsVolume { get => _isVolume; private set => SetProperty(ref _isVolume, value); }
    public double Percent { get => _percent; private set => SetProperty(ref _percent, value); }
    public void Show(SystemNotificationSnapshot notification)
    {
        IsVolume = false; Glyph = "\uE7F4";
        Title = string.IsNullOrWhiteSpace(notification.Title) ? notification.SourceName : notification.Title;
        Detail = string.IsNullOrWhiteSpace(notification.Body) ? notification.SourceName : notification.Body;
    }
    public void Show(VolumeActivitySnapshot volume)
    {
        IsVolume = true; Percent = volume.Muted ? 0 : volume.Percent;
        Glyph = volume.Muted ? "\uE74F" : "\uE767";
        Title = strings.Get(volume.Muted ? "ActivityMuted" : "ActivityVolume");
        Detail = $"{volume.Percent}%";
    }
    public void Clear() { Title = Detail = string.Empty; IsVolume = false; Percent = 0; }
}
