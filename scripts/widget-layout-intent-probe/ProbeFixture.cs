using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;
using DropSpace.Core.Widgets;

namespace DropSpace.App.ViewModels
{
    // Only the settings persistence/publication boundary is replaced. The linked
    // NativeSettingsEditor owns its real semaphore and invokes real layout policy.
    public sealed class MainViewModel(AppSettings initial) : ObservableObject
    {
        private AppSettings _settings = initial;
        private readonly TaskCompletionSource _releaseFirstSave = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource FirstSaveEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal List<AppSettings> Submitted { get; } = [];
        internal List<AppSettings> Published { get; } = [];
        public AppSettings Settings => _settings;
        public string Language => "en-US";

        public async Task UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
        {
            Submitted.Add(settings);
            if (Submitted.Count == 1)
            {
                FirstSaveEntered.TrySetResult();
                await _releaseFirstSave.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            SetProperty(ref _settings, settings, nameof(Settings));
            Published.Add(settings);
        }

        internal void ReleaseFirstSave() => _releaseFirstSave.TrySetResult();
    }
}

namespace DropSpace.App.Views.Settings
{
    public sealed partial class WidgetEditorView : IDisposable
    {
        private readonly ViewModels.NativeSettingsEditor _editor;
        private readonly IAppStringLocalizer _strings;
        private NativeWidgetId? _selectedId;
        private int _layoutEdits;
        private WidgetLayout? _undoLayout;
        private readonly ProbeButton _undo = new();
        private long _layoutFeedbackRevision;

        internal string Error { get; private set; } = string.Empty;
        internal List<string> ErrorTransitions { get; } = [];
        internal int MoveCalls { get; private set; }
        internal int ResizeCalls { get; private set; }
        internal int PendingLayoutEdits => _layoutEdits;
        internal WidgetLayout? UndoSnapshot => _undoLayout;
        internal bool UndoEnabled => _undo.IsEnabled;

        internal WidgetEditorView(ViewModels.NativeSettingsEditor editor, IAppStringLocalizer strings)
        {
            _editor = editor;
            _strings = strings;
            _editor.PropertyChanged += OnEditorChanged;
        }

        internal void Select(NativeWidgetId id) => _selectedId = id;

        internal Task MoveToAsync(int column, int row)
        {
            ++MoveCalls;
            return PlaceAsync(_selectedId!.Value, column, row);
        }

        internal Task ResizeSelectedAsync(WidgetSize size)
        {
            ++ResizeCalls;
            // Match the production size-button handler: the snapshot it passes
            // can precede a drag save already waiting on persistence.
            var current = _editor.Settings.Widgets.Layout.Expanded.Single(item => item.Id == _selectedId);
            return ResizeAsync(current, size);
        }

        private void OnEditorChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(ViewModels.NativeSettingsEditor.Error)) SetError(_editor.Error);
        }

        private void SetError(string? message)
        {
            var next = message ?? string.Empty;
            if (next == Error) return;
            Error = next;
            ErrorTransitions.Add(next);
        }

        // These hooks own visual controls only. They cannot change settings,
        // feedback revisions, operation order, or the production editing queue.
        private void RefreshSelection() { }
        public void Dispose() => _editor.PropertyChanged -= OnEditorChanged;

        private sealed class ProbeButton
        {
            public bool IsEnabled { get; set; }
        }
    }
}

namespace DropSpace.App.Services
{
    // Unrelated constructor dependencies are inert and fail if a scenario starts
    // using them. This probe does not exercise shell folders or notifications.
    public sealed class NativeFolderPickerService
    {
        public Task<string?> PickAsync(nint windowHandle) => throw new NotSupportedException();
        public static void OpenDirectory(string directory) => throw new NotSupportedException();
        public static string GetDownloadsDirectory() => throw new NotSupportedException();
    }
}

namespace DropSpace.App.Services.Notifications
{
    public sealed class WindowsNotificationActivityService
    {
        public Task<DropSpace.Core.SystemActivities.NotificationAccessState> RequestAccessAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}

namespace DropSpace.Infrastructure.Downloads
{
    public sealed class DownloadManager { }
}
