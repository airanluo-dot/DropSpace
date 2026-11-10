namespace DropSpace.Core.Media;

public static class MediaProcessIdentityPolicy
{
    private const string AppleMusicApp = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!App";
    private const string AppleMusicRenderer = "AppleInc.AppleMusicWin_nzyj5cx40ttqa!LibraryServer";

    public static bool IsAppleMusic(string source) =>
        string.Equals(source, AppleMusicApp, StringComparison.OrdinalIgnoreCase);

    // Apple Music's COM-launched audio server is not a child of its UI process.
    // Match its full package/application identity, never an executable name alone.
    public static string AudioIdentity(string source)
    {
        if (IsAppleMusic(source)) return AppleMusicRenderer;
        return ApplicationIdentity(source);
    }

    public static string ApplicationIdentity(string source)
    {
        // The NetEase integration publishes these aliases for the same desktop app.
        if (source.Equals("cloudmusic", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("com.netease.cloudmusic", StringComparison.OrdinalIgnoreCase)) return "cloudmusic.exe";
        return source;
    }
}
