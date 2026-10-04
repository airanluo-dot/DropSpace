namespace DropSpace.Core.Media;

public static class MediaProcessIdentityPolicy
{
    private const string AppleMusicApp = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
    private const string AppleMusicRenderer = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!LibraryServer";

    // Apple Music's COM-launched audio server is not a child of its UI process.
    // Match its full package/application identity, never an executable name alone.
    public static string AudioIdentity(string source)
    {
        if (source.Equals(AppleMusicApp, StringComparison.OrdinalIgnoreCase)) return AppleMusicRenderer;
        // The NetEase SMTC integration recognizes these aliases; resolve the same
        // player for capture instead of mistaking its alias for a missing process.
        if (source.Equals("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("com.netease.cloudmusic", StringComparison.OrdinalIgnoreCase)) return "cloudmusic.exe";
        return source;
    }
}
