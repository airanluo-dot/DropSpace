// Controlled native boundary for this Linux probe only. Production service logic is linked unchanged.
using System.Collections.Concurrent;
using Windows.Foundation;

namespace Windows.Foundation
{
    public delegate void TypedEventHandler<in T, in TArgs>(T sender, TArgs args);
    public interface IAsyncOperation<T> { Task<T> AsTask(CancellationToken token); void Cancel(); }
    public interface IAsyncAction { Task AsTask(CancellationToken token); void Cancel(); }

    internal sealed class AsyncOperation<T>(Task<T> task) : IAsyncOperation<T>
    { public Task<T> AsTask(CancellationToken token) => task; public void Cancel() { } }
    internal sealed class AsyncAction(Task task) : IAsyncAction
    { public Task AsTask(CancellationToken token) => task; public void Cancel() { } }
}

namespace Microsoft.UI.Dispatching
{
    public delegate void DispatcherQueueHandler();
    public sealed class DispatcherQueue
    {
        internal readonly BlockingCollection<Action> Queue = new();
        internal int ThreadId;
        public bool HasThreadAccess => Environment.CurrentManagedThreadId == ThreadId;
        public bool TryEnqueue(DispatcherQueueHandler callback)
        {
            try { Queue.Add(() => callback()); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }
    public sealed class DispatcherQueueSynchronizationContext(DispatcherQueue queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state)
        { if (!queue.TryEnqueue(() => callback(state))) throw new InvalidOperationException("Probe owner is closed."); }
    }
    public sealed class DispatcherQueueController
    {
        internal static int FailNextStartup;
        internal static int Starts;
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DispatcherQueue DispatcherQueue { get; } = new();
        private DispatcherQueueController()
        {
            var started = new ManualResetEventSlim();
            new Thread(() =>
            {
                DispatcherQueue.ThreadId = Environment.CurrentManagedThreadId;
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue));
                started.Set();
                try { foreach (var action in DispatcherQueue.Queue.GetConsumingEnumerable()) action(); }
                finally { _exited.TrySetResult(); }
            }) { IsBackground = true, Name = "Probe media owner (no Windows apartment emulation)" }.Start();
            started.Wait();
            started.Dispose();
        }
        public static DispatcherQueueController CreateOnDedicatedThread()
        {
            Interlocked.Increment(ref Starts);
            if (Interlocked.Exchange(ref FailNextStartup, 0) != 0) throw new InvalidOperationException("Controlled dispatcher startup failure.");
            return new();
        }
        public IAsyncAction ShutdownQueueAsync()
        { DispatcherQueue.Queue.CompleteAdding(); return new AsyncAction(_exited.Task); }
    }
}

namespace Windows.Storage.Streams
{
    public interface IRandomAccessStreamReference { IAsyncOperation<IRandomAccessStream> OpenReadAsync(); }
    public interface IRandomAccessStream : IDisposable { ulong Size { get; } }
    public sealed class DataReader(IRandomAccessStream stream) : IDisposable
    {
        public IAsyncOperation<uint> LoadAsync(uint length) => throw new NotSupportedException("Artwork is outside this probe.");
        public void ReadBytes(byte[] bytes) => throw new NotSupportedException("Artwork is outside this probe.");
        public void Dispose() { GC.KeepAlive(stream); }
    }
}

namespace Windows.Media.Control
{
    internal sealed class NativeAccess
    {
        private int _owner;
        internal readonly ConcurrentQueue<string> Calls = new();
        internal void Check(string call)
        {
            var thread = Environment.CurrentManagedThreadId;
            Interlocked.CompareExchange(ref _owner, thread, 0);
            if (_owner != thread) throw new InvalidOperationException($"Wrong native thread for {call}: {_owner} -> {thread}.");
            Calls.Enqueue(call);
        }
        internal int Owner => _owner;
    }
    public sealed class CurrentSessionChangedEventArgs : EventArgs;
    public sealed class SessionsChangedEventArgs : EventArgs;
    public sealed class MediaPropertiesChangedEventArgs : EventArgs;
    public sealed class PlaybackInfoChangedEventArgs : EventArgs;
    public sealed class TimelinePropertiesChangedEventArgs : EventArgs;
    public enum GlobalSystemMediaTransportControlsSessionPlaybackStatus { Closed, Opened, Changing, Stopped, Playing, Paused }
    public sealed class GlobalSystemMediaTransportControlsSessionManager
    {
        internal static GlobalSystemMediaTransportControlsSessionManager Next = new();
        internal readonly NativeAccess Access = new();
        internal GlobalSystemMediaTransportControlsSession[] Sessions = [];
        private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs>? _current;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>? _sessions;
        public event TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> CurrentSessionChanged
        { add { Access.Check("manager.current.add"); _current += value; } remove { Access.Check("manager.current.remove"); _current -= value; } }
        public event TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> SessionsChanged
        { add { Access.Check("manager.sessions.add"); _sessions += value; } remove { Access.Check("manager.sessions.remove"); _sessions -= value; } }
        public static IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager> RequestAsync()
        { var manager = Next; manager.Access.Check("manager.request"); return new AsyncOperation<GlobalSystemMediaTransportControlsSessionManager>(Task.FromResult(manager)); }
        public GlobalSystemMediaTransportControlsSession? GetCurrentSession()
        { Access.Check("manager.current.get"); return Sessions.FirstOrDefault(); }
        public IReadOnlyList<GlobalSystemMediaTransportControlsSession> GetSessions()
        { Access.Check("manager.sessions.get"); return Sessions; }
        internal Action CaptureChanged() { var callback = _sessions; return () => callback?.Invoke(this, new()); }
        internal void Changed() => CaptureChanged()();
    }
    public sealed class GlobalSystemMediaTransportControlsSession
    {
        internal readonly NativeAccess Access;
        internal GlobalSystemMediaTransportControlsSession(NativeAccess access, string title)
        { Access = access; Title = title; }
        internal string Title;
        internal bool Playing = true;
        internal bool SourceUnavailable;
        internal bool Disconnected;
        internal int TrackIndex;
        internal readonly List<string> Commands = [];
        internal TaskCompletionSource? ReadEntered;
        internal TaskCompletionSource? ReleaseRead;
        internal TaskCompletionSource? ReadCompleted;
        internal int ReadCount;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs>? _media;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs>? _playback;
        private TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs>? _timeline;
        public event TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> MediaPropertiesChanged
        { add { Access.Check("session.media.add"); _media += value; } remove { Access.Check("session.media.remove"); _media -= value; } }
        public event TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> PlaybackInfoChanged
        { add { Access.Check("session.playback.add"); _playback += value; } remove { Access.Check("session.playback.remove"); _playback -= value; } }
        public event TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> TimelinePropertiesChanged
        { add { Access.Check("session.timeline.add"); _timeline += value; } remove { Access.Check("session.timeline.remove"); _timeline -= value; } }
        public string SourceAppUserModelId { get { Access.Check("session.source"); if (SourceUnavailable) throw new System.Runtime.InteropServices.COMException("Initializing source", unchecked((int)0x80004005)); return "cloudmusic.exe"; } }
        public IAsyncOperation<GlobalSystemMediaTransportControlsSessionMediaProperties> TryGetMediaPropertiesAsync()
        {
            Access.Check("session.metadata.start");
            return new AsyncOperation<GlobalSystemMediaTransportControlsSessionMediaProperties>(ReadAsync());
            async Task<GlobalSystemMediaTransportControlsSessionMediaProperties> ReadAsync()
            {
                Interlocked.Increment(ref ReadCount);
                var title = Title;
                ReadEntered?.TrySetResult();
                if (ReleaseRead is { } release) await release.Task;
                await Task.Yield(); // Force production ReadNativeTrackAsync to restore its owner context.
                Access.Check("session.metadata.complete");
                ReadCompleted?.TrySetResult();
                return new() { Title = title + (TrackIndex == 0 ? "" : "-" + TrackIndex) };
            }
        }
        public GlobalSystemMediaTransportControlsSessionPlaybackInfo GetPlaybackInfo()
        { Access.Check("session.playback.get"); return new() { PlaybackStatus = Playing ? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing : GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused }; }
        public GlobalSystemMediaTransportControlsSessionTimelineProperties GetTimelineProperties()
        { Access.Check("session.timeline.get"); return new(); }
        public IAsyncOperation<bool> TryPauseAsync() => Command("pause");
        public IAsyncOperation<bool> TryPlayAsync() => Command("play");
        public IAsyncOperation<bool> TrySkipNextAsync() => Command("next");
        public IAsyncOperation<bool> TrySkipPreviousAsync() => Command("previous");
        public IAsyncOperation<bool> TryChangePlaybackPositionAsync(long ticks) => Command("seek");
        private IAsyncOperation<bool> Command(string command)
        {
            Access.Check("control." + command);
            lock (Commands) Commands.Add(command);
            if (Disconnected) throw new System.Runtime.InteropServices.COMException("Stale session", unchecked((int)0x80010108));
            {
                if (command is "next") TrackIndex++;
                if (command is "previous") TrackIndex--;
                if (command is "pause") Playing = false;
                if (command is "play") Playing = true;
                _playback?.Invoke(this, new());
            }
            return new AsyncOperation<bool>(Task.FromResult(true));
        }
        internal Action CaptureMetadataChanged() { var callback = _media; return () => callback?.Invoke(this, new()); }
        internal void Changed() => CaptureMetadataChanged()();
    }
    public sealed class GlobalSystemMediaTransportControlsSessionMediaProperties
    {
        public string Title { get; init; } = "Track";
        public string Artist => "Artist";
        public string AlbumTitle => "Album";
        public string AlbumArtist => "Artist";
        public int TrackNumber => 1;
        public Windows.Storage.Streams.IRandomAccessStreamReference? Thumbnail => null;
    }
    public sealed class GlobalSystemMediaTransportControlsSessionPlaybackInfo
    {
        public GlobalSystemMediaTransportControlsSessionPlaybackStatus PlaybackStatus { get; init; }
        public double? PlaybackRate => 1;
        public GlobalSystemMediaTransportControlsSessionPlaybackControls Controls => new();
    }
    public sealed class GlobalSystemMediaTransportControlsSessionPlaybackControls
    {
        public bool IsPlayEnabled => true;
        public bool IsPauseEnabled => true;
        public bool IsNextEnabled => true;
        public bool IsPreviousEnabled => true;
        public bool IsPlaybackPositionEnabled => true;
    }
    public sealed class GlobalSystemMediaTransportControlsSessionTimelineProperties
    {
        public TimeSpan Position => TimeSpan.FromSeconds(1);
        public TimeSpan StartTime => TimeSpan.Zero;
        public TimeSpan EndTime => TimeSpan.FromMinutes(3);
        public DateTimeOffset LastUpdatedTime => DateTimeOffset.UtcNow;
    }
}

namespace System
{ public static class ProbeAsyncAwaiter
  { public static System.Runtime.CompilerServices.TaskAwaiter GetAwaiter(this global::Windows.Foundation.IAsyncAction operation) => operation.AsTask(CancellationToken.None).GetAwaiter(); }
}
