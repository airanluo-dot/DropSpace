using System.Diagnostics;
using System.Text.Json;
using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Windows.Media;
using Windows.Media.Control;
using Windows.Media.Core;
using Windows.Media.Playback;
using CorePlaybackState = DropSpace.Core.Media.MediaPlaybackState;
using NativePlaybackState = Windows.Media.Playback.MediaPlaybackState;

namespace DropSpace.App.Services.Diagnostics;

/// <summary>
/// Opt-in native acceptance probe. This is a silent, short-lived diagnostic publisher,
/// never a production player. It refuses to control sessions when another source exists.
/// Automatic SMTC integration follows the platform contract:
/// https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/integrate-with-systemmediatransportcontrols
/// </summary>
internal static class MusicSessionProbe
{
    internal sealed record ProbeResult(string Status, string Reason, string ReceiptPath,
        IReadOnlyList<object> Checks, bool PublisherDisposed, bool NativeOwnerDrained,
        string AudioBoundary = "Silent PCM only; nonzero process-loopback capture is not verified by this probe.");

    internal static async Task<ProbeResult> RunAsync(string outputDirectory,
        ILoggerFactory? loggerFactory = null, CancellationToken cancellationToken = default)
    {
        var output = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(output) || File.Exists(output))
            throw new IOException("The native session probe requires a new output directory.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(output)!); parent is not null; parent = parent.Parent)
            if (parent.Exists && (parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The native session probe does not follow reparse points.");
        Directory.CreateDirectory(output);
        var receipt = Path.Combine(output, "receipt.json");
        var ledger = new List<object>();
        var status = "blocked";
        var reason = "The publisher was not started.";
        var playerDisposed = false;
        var ownerDrained = false;
        MediaPlayer? player = null;
        MediaSource? source = null;
        WindowsMediaSessionService? media = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(token)
                .WaitAsync(TimeSpan.FromSeconds(5), token);
            var before = manager.GetSessions().Select(session => session.SourceAppUserModelId).ToArray();
            ledger.Add(new { check = "initial-read-only-inventory", sources = before, count = before.Length });
            if (before.Length > 0) throw new ProbeBlockedException("Existing SMTC sources were found; none were controlled and no test publisher was created.");

            var title = "DropSpace verification " + Guid.NewGuid().ToString("N");
            var path = Path.Combine(output, "silent-verification.wav");
            WriteSilentWave(path);
            source = MediaSource.CreateFromUri(new Uri(path));
            var item = new MediaPlaybackItem(source);
            var properties = item.GetDisplayProperties();
            properties.Type = MediaPlaybackType.Music;
            properties.MusicProperties.Title = title;
            properties.MusicProperties.Artist = "DropSpace local verification";
            item.ApplyDisplayProperties(properties);
            player = new MediaPlayer { AutoPlay = false, Volume = 0, IsLoopingEnabled = false, Source = item };
            player.CommandManager.IsEnabled = true;
            player.Play();
            await WaitUntilAsync(() => player.PlaybackSession.PlaybackState == NativePlaybackState.Playing &&
                    player.PlaybackSession.NaturalDuration > TimeSpan.Zero,
                TimeSpan.FromSeconds(10), token, "The silent test media did not open on this Windows installation.");
            ledger.Add(new { check = "real-media-player-opened", durationSeconds = player.PlaybackSession.NaturalDuration.TotalSeconds, playerVolume = player.Volume });

            GlobalSystemMediaTransportControlsSession? publisher = null;
            var discoveryDeadline = Stopwatch.StartNew();
            while (publisher is null && discoveryDeadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                token.ThrowIfCancellationRequested();
                var sessions = manager.GetSessions();
                if (sessions.Count > 1) throw new ProbeBlockedException("Another SMTC source appeared during discovery; no native control was sent.");
                if (sessions.Count == 1)
                {
                    var candidate = sessions[0];
                    var metadata = await candidate.TryGetMediaPropertiesAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token);
                    if (metadata.Title == title) publisher = candidate;
                    else throw new ProbeBlockedException("The only discovered SMTC session was not the uniquely named test publisher; no native control was sent.");
                }
                if (publisher is null) await Task.Delay(200, token);
            }
            if (publisher is null) throw new ProbeBlockedException("Windows did not expose the test publisher through SMTC within 15 seconds.");
            var sourceId = publisher.SourceAppUserModelId;
            media = new WindowsMediaSessionService(loggerFactory?.CreateLogger<WindowsMediaSessionService>() ?? NullLogger<WindowsMediaSessionService>.Instance,
                dispatcher: DispatcherQueue.GetForCurrentThread());
            media.SetAllowedSources([sourceId], restrictToList: true);
            await media.SetEnabledAsync(true, token);
            await WaitUntilAsync(() => media.Current.TrackTitle == title && media.Current.SourceAppUserModelId == sourceId,
                TimeSpan.FromSeconds(10), token, "The existing native media owner could not acquire the test publisher.");
            var view = new MediaViewModel(media, IdentityAppStringLocalizer.Instance,
                loggerFactory?.CreateLogger<MediaViewModel>() ?? NullLogger<MediaViewModel>.Instance);

            async Task GuardAndRefreshAsync()
            {
                var sessions = manager.GetSessions();
                if (sessions.Count != 1 || sessions[0].SourceAppUserModelId != sourceId)
                    throw new ProbeBlockedException("The session inventory changed; subsequent native controls were stopped.");
                var metadata = await sessions[0].TryGetMediaPropertiesAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token);
                if (metadata.Title != title || media.Current.TrackTitle != title || media.Current.SourceAppUserModelId != sourceId)
                    throw new ProbeBlockedException("Test publisher identity no longer matches; subsequent native controls were stopped.");
                view.Session = media.Current;
                view.Position = media.Current.Timeline.Position;
                view.PositionEstimated = media.Current.Timeline.Duration <= TimeSpan.Zero;
            }

            await GuardAndRefreshAsync();
            ledger.Add(new { check = "existing-owner-acquired-real-publisher", sourceId, snapshot = media.Current });
            if (!view.IsPlaying || !view.Session.CanPause) throw new ProbeBlockedException("The real publisher does not advertise pause support in its current state.");
            await view.PlayPauseCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8), token);
            await WaitUntilAsync(() => player.PlaybackSession.PlaybackState == NativePlaybackState.Paused && media.Current.PlaybackState == CorePlaybackState.Paused,
                TimeSpan.FromSeconds(6), token, "Pause was advertised but the actual publisher did not pause.", controlSent: true);
            if (view.ControlError.Length > 0) throw new InvalidOperationException("The existing ViewModel reported a pause control failure.");
            ledger.Add(new { check = "viewmodel-native-pause", status = "passed", nativeState = player.PlaybackSession.PlaybackState.ToString() });

            await GuardAndRefreshAsync();
            if (!view.Session.CanPlay) throw new ProbeBlockedException("The real publisher does not advertise resume support while paused.");
            await view.PlayPauseCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(8), token);
            await WaitUntilAsync(() => player.PlaybackSession.PlaybackState == NativePlaybackState.Playing && media.Current.PlaybackState == CorePlaybackState.Playing,
                TimeSpan.FromSeconds(6), token, "Resume was advertised but the actual publisher did not resume.", controlSent: true);
            if (view.ControlError.Length > 0) throw new InvalidOperationException("The existing ViewModel reported a resume control failure.");
            ledger.Add(new { check = "viewmodel-native-resume", status = "passed", nativeState = player.PlaybackSession.PlaybackState.ToString() });

            await GuardAndRefreshAsync();
            // A target far from the live position cannot pass merely because the
            // playback clock happened to advance while a seek was ignored.
            var targetSeconds = player.PlaybackSession.Position.TotalSeconds < 45 ? 60d : 10d;
            if (!view.SeekCommand.CanExecute(targetSeconds)) throw new ProbeBlockedException("The real publisher has no exact seekable timeline; seek remains unverified.");
            var observations = new List<SeekObservation>();
            var seekClock = Stopwatch.StartNew();
            var requestedAt = DateTimeOffset.UtcNow;
            DateTimeOffset? commandCompletedAt = null;
            var absoluteTargetSeconds = view.Session.Timeline.Start.TotalSeconds + targetSeconds;
            var playerReached = false;
            var publishedReached = false;
            var ownerReached = false;
            SeekObservation? playerFirstReached = null, publishedFirstReached = null, ownerFirstReached = null;
            observations.Add(CaptureSeek("before-command", seekClock.Elapsed.TotalSeconds, player, publisher, media, view));
            SeekObservation ObserveSeek(string phase)
            {
                var sample = CaptureSeek(phase, seekClock.Elapsed.TotalSeconds, player, publisher, media, view);
                observations.Add(sample);
                // Each layer may be sampled at a different moment. Accumulate its
                // first confirmation, with a range that permits natural playback,
                // instead of requiring three clocks to be near a fixed point at once.
                if (!playerReached && NearSeekTarget(sample.PlayerSeconds, absoluteTargetSeconds, sample.SecondsSinceCommand))
                { playerReached = true; playerFirstReached = sample; }
                if (!publishedReached && sample.PublishedSeconds is { } published &&
                    NearSeekTarget(published, absoluteTargetSeconds, sample.SecondsSinceCommand))
                { publishedReached = true; publishedFirstReached = sample; }
                if (!ownerReached && sample.OwnerTrackTitle == title && sample.OwnerSourceId == sourceId &&
                    NearSeekTarget(sample.OwnerSeconds, absoluteTargetSeconds, sample.SecondsSinceCommand))
                { ownerReached = true; ownerFirstReached = sample; }
                return sample;
            }
            try
            {
                // Sample during command completion too: an immediate player jump
                // or SMTC event must not be missed while its async reply is pending.
                var command = view.SeekCommand.ExecuteAsync(targetSeconds).WaitAsync(TimeSpan.FromSeconds(8), token);
                while (!command.IsCompleted)
                {
                    ObserveSeek("command-pending");
                    await Task.WhenAny(command, Task.Delay(100, token));
                    token.ThrowIfCancellationRequested();
                }
                await command;
                commandCompletedAt = DateTimeOffset.UtcNow;
                var confirmationClock = Stopwatch.StartNew();
                do
                {
                    token.ThrowIfCancellationRequested();
                    var sample = ObserveSeek("after-command");
                    if (sample.ControlError.Length > 0)
                        throw new InvalidOperationException("The existing ViewModel reported a seek control failure: " + sample.ControlError);
                    if (playerReached && publishedReached && ownerReached) break;
                    await Task.Delay(100, token);
                } while (confirmationClock.Elapsed < TimeSpan.FromSeconds(6));
                if (!playerReached)
                    throw new InvalidOperationException("The actual MediaPlayer never reached the requested seek target; native publisher and owner observations are recorded.");
                if (!ownerReached)
                    throw new InvalidOperationException(publishedReached
                        ? "The actual MediaPlayer and direct SMTC timeline reached the seek target, but the existing media owner never confirmed it; three-layer observations are recorded."
                        : "The actual MediaPlayer reached the seek target, but neither the direct SMTC timeline nor the existing media owner confirmed it; three-layer observations are recorded.");
                if (!publishedReached)
                    throw new InvalidOperationException("The actual MediaPlayer and existing media owner reached the seek target, but independent direct SMTC samples never confirmed it; three-layer observations are recorded.");
                ledger.Add(new { check = "viewmodel-native-seek", status = "passed", targetSeconds,
                    absoluteTargetSeconds, actualSeconds = player.PlaybackSession.Position.TotalSeconds,
                    ownerSeconds = media.Current.Timeline.Position.TotalSeconds, playerReached, publishedReached, ownerReached });
            }
            finally
            {
                observations.Add(CaptureSeek("final-before-cleanup", seekClock.Elapsed.TotalSeconds, player, publisher, media, view));
                ledger.Add(new { check = "native-seek-observations", targetSeconds,
                    requestedTicks = (view.Session.Timeline.Start + TimeSpan.FromSeconds(targetSeconds)).Ticks,
                    absoluteTargetSeconds, requestedAt, commandCompletedAt,
                    confirmationRule = "Each layer independently observed an absolute position from target-2 through target+elapsed+3 seconds. The target is distant from the pre-command position, so ordinary progression cannot satisfy it.",
                    playerReached, publishedReached, ownerReached, playerFirstReached, publishedFirstReached, ownerFirstReached, observations });
            }
            status = "passed";
            reason = "A uniquely named silent MediaPlayer publisher was paused, resumed and sought through the existing ViewModel and WindowsMediaSessionService.";
        }
        catch (ProbeBlockedException exception) { reason = exception.Message; }
        catch (TimeoutException exception) { reason = "Native setup or control timed out: " + exception.Message; }
        catch (OperationCanceledException) { status = cancellationToken.IsCancellationRequested ? "cancelled" : "blocked"; reason = "The bounded native probe was cancelled or reached its 90-second deadline."; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { status = "failed"; reason = exception.GetType().Name + ": " + exception.Message; }
        finally
        {
            if (media is not null)
            {
                try { await media.DisposeAsync(); ownerDrained = true; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { status = "failed"; ledger.Add(new { check = "native-owner-cleanup", error = exception.GetType().Name }); }
            }
            if (player is not null)
            {
                try { player.Pause(); player.Source = null; }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                { status = "failed"; ledger.Add(new { check = "test-publisher-stop", error = exception.GetType().Name }); }
                finally
                {
                    try { player.Dispose(); playerDisposed = true; }
                    catch (Exception exception) when (exception is not OutOfMemoryException)
                    { status = "failed"; ledger.Add(new { check = "test-publisher-cleanup", error = exception.GetType().Name }); }
                }
            }
            try { source?.Dispose(); }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            { status = "failed"; ledger.Add(new { check = "test-source-cleanup", error = exception.GetType().Name }); }
        }
        var result = new ProbeResult(status, reason, receipt, ledger, playerDisposed, ownerDrained);
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        return result;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, CancellationToken token, string failure, bool controlSent = false)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            if (predicate()) return;
            await Task.Delay(100, token);
        }
        if (controlSent) throw new InvalidOperationException(failure);
        throw new ProbeBlockedException(failure);
    }

    private static bool NearSeekTarget(double observed, double target, double secondsSinceCommand) =>
        double.IsFinite(observed) && observed >= target - 2 && observed <= target + secondsSinceCommand + 3;

    private sealed record SeekObservation(string Phase, DateTimeOffset CapturedAt, double SecondsSinceCommand,
        double PlayerSeconds, string PlayerState, double PlayerDurationSeconds,
        double OwnerSeconds, string OwnerState, bool OwnerCanSeek, DateTimeOffset OwnerTimelineUpdatedAt,
        DateTimeOffset OwnerSnapshotUpdatedAt, string OwnerSessionId, string OwnerSourceId, string OwnerTrackTitle,
        double? PublishedSeconds, DateTimeOffset? PublishedTimelineUpdatedAt,
        string? PublishedState, bool? PublishedCanSeek, string? PublishedReadError, string ControlError);

    private static SeekObservation CaptureSeek(string phase, double elapsed, MediaPlayer player,
        GlobalSystemMediaTransportControlsSession publisher, WindowsMediaSessionService media, MediaViewModel view)
    {
        double? publishedSeconds = null;
        DateTimeOffset? publishedUpdatedAt = null;
        string? publishedState = null, readError = null;
        bool? publishedCanSeek = null;
        try
        {
            var timeline = publisher.GetTimelineProperties();
            publishedSeconds = timeline.Position.TotalSeconds;
            publishedUpdatedAt = timeline.LastUpdatedTime;
            var playback = publisher.GetPlaybackInfo();
            publishedState = playback.PlaybackStatus.ToString();
            publishedCanSeek = playback.Controls.IsPlaybackPositionEnabled;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        { readError = exception.GetType().Name + ": " + exception.Message; }
        var owner = media.Current;
        return new(phase, DateTimeOffset.UtcNow, elapsed, player.PlaybackSession.Position.TotalSeconds,
            player.PlaybackSession.PlaybackState.ToString(), player.PlaybackSession.NaturalDuration.TotalSeconds,
            owner.Timeline.Position.TotalSeconds, owner.PlaybackState.ToString(), owner.CanSeek,
            owner.Timeline.LastUpdated, owner.LastUpdated, owner.SessionId, owner.SourceAppUserModelId, owner.TrackTitle,
            publishedSeconds, publishedUpdatedAt,
            publishedState, publishedCanSeek, readError, view.ControlError);
    }

    private static void WriteSilentWave(string path)
    {
        const int sampleRate = 8_000, seconds = 90, bytes = sampleRate * seconds * 2;
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate);
        writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(bytes);
        var silence = new byte[8_000];
        for (var written = 0; written < bytes; written += silence.Length) writer.Write(silence, 0, Math.Min(silence.Length, bytes - written));
    }
    private sealed class ProbeBlockedException(string message) : Exception(message);
}
