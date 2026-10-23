using DropSpace.App.Services.Media;
using DropSpace.App.ViewModels;
using DropSpace.Core.Agent;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Media;
using Microsoft.UI.Xaml;

namespace DropSpace.App.Services.Agent;

public sealed class MediaAgentDomain : AppAgentDomain
{
    public MediaAgentDomain(MainViewModel main, MediaViewModel view, WindowsMediaSessionService media,
        MediaExperienceService experience, NeteaseEnhancementViewModel netease, AppAgentDesktopControl desktop, IAppStringLocalizer strings,
        MusicWindowService musicWindow, Volume.WindowsVolumeActivityService volume)
    {
        var ai=experience.AiLyrics;
        object MusicState() => new { media.IsAvailable,media.AvailabilityReason,media.AvailableSources,session=media.Current with { Artwork=null },view.PositionSeconds,view.DurationSeconds,view.PositionEstimated,view.ControlError };
        object LoginState() => new { experience.QqMusicLogin.Session.State,experience.QqMusicLogin.Session.HasSavedSession };
        object EnhancementState() => new { netease.IsBusy,netease.IsEnhanced,netease.IsManaged,netease.Status,netease.Error };
        Add("music.state","Read current media capabilities, source, position and availability; album image bytes are excluded.",false,"",(a,t)=>Value(MusicState()));
        Add("music.window_state","Read the independent music window state without opening it.",false,"",(a,t)=>Value(musicWindow.GetState()));
        Add("music.window_open","Explicitly open or activate the single shared-session music window.",true,"",(a,t)=>{musicWindow.Open();return Value(musicWindow.GetState());});
        Add("music.window_close","Close only the independent music window; playback and the island continue.",true,"",(a,t)=>{musicWindow.Close();return Value(musicWindow.GetState());});
        Add("music.window_lyrics","Show or hide the lyrics column in an already open music window.",true,"visible!:boolean",(a,t)=>{var window=musicWindow.CurrentWindow ?? throw new AppAgentCommandException("action_unavailable","Open the music window first.");window.SetLyricsVisible(Boolean(a,"visible"));return Value(musicWindow.GetState());});
        Add("music.window_translation","Set the shared secondary-lyrics preference from an already open music window.",true,"enabled!:boolean",async(a,t)=>{var window=musicWindow.CurrentWindow ?? throw new AppAgentCommandException("action_unavailable","Open the music window first.");await window.SetTranslationEnabledAsync(Boolean(a,"enabled"));return Result(musicWindow.GetState());});
        Add("volume.state","Read Windows system master volume, distinct from any player's volume.",false,"",async(a,t)=>{var owner=new object();try{await volume.SetControlActiveAsync(owner,true,t);return Result(new { volume.IsAvailable,volume.Current });}finally{await volume.SetControlActiveAsync(owner,false);}});
        Add("volume.set","Set Windows system master volume from 0 to 100; a nonzero level also unmutes the endpoint.",true,"percent!:integer",async(a,t)=>Result(await volume.SetMasterVolumeAsync(Integer(a,"percent",0,0,100)/100d,t)));
        Add("volume.mute","Mute or unmute the Windows system output endpoint.",true,"muted!:boolean",async(a,t)=>Result(await volume.SetMutedAsync(Boolean(a,"muted"),t)));
        Add("music.play_pause","Request play/pause from the existing Windows media-session owner. Fails if the publisher does not expose the required capability.",true,"",async(a,t)=>{var s=media.Current;if(!media.IsAvailable || (s.PlaybackState==MediaPlaybackState.Playing ? !s.CanPause : !s.CanPlay))throw new AppAgentCommandException("action_unavailable","Play/pause is not available.");await media.PlayPauseAsync(t);return Result(new { status="requested",state=MusicState() });});
        Add("music.next","Request the next track from the current media publisher.",true,"",async(a,t)=>{if(!media.IsAvailable||!media.Current.CanSkipNext)throw new AppAgentCommandException("action_unavailable","Next track is unavailable.");await media.SkipNextAsync(t);return Result(new { status="requested" });});
        Add("music.previous","Request the previous track from the current media publisher.",true,"",async(a,t)=>{if(!media.IsAvailable||!media.Current.CanSkipPrevious)throw new AppAgentCommandException("action_unavailable","Previous track is unavailable.");await media.SkipPreviousAsync(t);return Result(new { status="requested" });});
        Add("music.seek","Seek to seconds from the start of the current track within its known duration.",true,"seconds!:number",async(a,t)=>{var seconds=a.GetProperty("seconds").GetDouble();var s=media.Current;if(!media.IsAvailable||!s.CanSeek)throw new AppAgentCommandException("action_unavailable","Seeking is unavailable.");if(!double.IsFinite(seconds)||seconds<0||seconds>s.Timeline.Duration.TotalSeconds)throw Invalid("Seek position is outside the timeline.");await media.SeekAsync(s.Timeline.Start+TimeSpan.FromSeconds(seconds),t);return Result(new { status="requested" });});
        Add("music.restart","Run the existing bounded media reconnect operation.",true,"",async(a,t)=>{await experience.RestartAsync(t);return Result(MusicState());});
        Add("lyrics.get","Read current lyrics, translated lines, status and active line. Settings use settings.patch.",false,"",(a,t)=>Value(new { view.LyricsStatus,view.LyricsStatusText,view.LyricsLines,view.CurrentLyricIndex,view.CurrentLyricText,view.SecondaryLyricText }));
        Add("lyrics.clear_cache","Ask in the native app before clearing the existing lyrics/translation cache.",true,"",async(a,t)=>{if(!await desktop.ConfirmAsync("lyrics.clear_cache",strings.Get("AiLyricsClearCacheConfirm"),t))return Result(new { status="cancelled" });await experience.ClearLyricsCacheAsync(t);return Done();});
        Add("ai.status","Read actual AI execution/backend state and cache status; this is not a capability claim about untested hardware.",false,"",(a,t)=>Value(new { ai.TranslationState,ai.TranslationPaused,ai.ActualBackend,ai.CacheMigrationFailed,execution=ai.GetExecutionStatus(main.Settings.Lyrics) }));
        Add("ai.resume","Resume translation after its existing circuit pause.",true,"",(a,t)=>{ai.ResumeTranslation();return Value(new { ai.TranslationState,ai.TranslationPaused });});
        Add("ai.retry_gpu","Run the existing GPU retry path and report resulting execution state.",true,"",async(a,t)=>{await ai.RetryGpuAsync(t);return Result(ai.GetExecutionStatus(main.Settings.Lyrics));});
        Add("ai.model_inspect","Inspect a declared local lyric model's installed resources. Model downloads/deletion use dlc commands.",false,"id!:string",async(a,t)=>Result(await ai.InspectModelAsync(String(a,"id",max:128),t)));
        Add("music.qq_status","Read QQ Music sign-in state without cookies, credentials or browser data.",false,"",(a,t)=>Value(LoginState()));
        Add("music.qq_login","Open the existing native QQ Music login window. The user completes sign-in; no credentials are accepted over this API.",true,"",(a,t)=>{experience.QqMusicLogin.Open(experience.VerifyQqMusicAsync,ElementTheme.Default);return Value(new { status="pending_confirmation",nextStep="Complete sign-in in the opened QQ Music login window." });});
        Add("music.qq_verify","Verify the currently saved QQ Music session without exposing credentials.",true,"",async(a,t)=>{await experience.VerifyQqMusicAsync(t);return Result(LoginState());});
        Add("music.qq_sign_out","Sign out of the existing QQ Music session.",true,"",async(a,t)=>{await experience.QqMusicLogin.SignOutAsync();return Result(LoginState());});
        Add("music.netease_inspect","Inspect the existing managed NetEase enhancement.",false,"",async(a,t)=>{await netease.InspectAsync();return Result(EnhancementState());});
        Add("music.netease_install","Ask in the native app before deploying or reinstalling the managed NetEase enhancement; returns actual state.",true,"reinstall:boolean",async(a,t)=>{if(!await desktop.ConfirmAsync("music.netease_install",strings.Get("NeteaseEnhancementConfirmBody"),t))return Result(new { status="cancelled" });await netease.EnhanceAsync(Boolean(a,"reinstall"));return Result(EnhancementState());});
        Add("music.netease_update","Update an existing managed NetEase enhancement using its normal action.",true,"",async(a,t)=>{if(!netease.IsManaged)throw new AppAgentCommandException("action_unavailable","Install the managed enhancement first.");await netease.EnhanceAsync();return Result(EnhancementState());});
        Add("music.netease_remove","Remove the managed enhancement through its existing removal action.",true,"",async(a,t)=>{await netease.RemoveAsync();return Result(EnhancementState());});
    }
}
