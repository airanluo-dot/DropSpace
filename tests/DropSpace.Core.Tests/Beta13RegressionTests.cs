using System.Text.Json;
using DropSpace.Core.Island;
using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;
using DropSpace.Core.Overlay;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class Beta13RegressionTests
{
    [TestMethod]
    public void HostLanguageEvidenceProtectsOriginals()
    {
        foreach (var text in new[] { "Conquering doubts that linger near", "A thousand light-years, wild and free" })
        {
            var evidence = LyricsLanguagePolicy.Identify(text);
            Assert.AreEqual("en", evidence.Language);
            Assert.AreEqual(LyricsTranslationAdmission.SameLanguage, LyricsLanguagePolicy.GetAdmission(text, "en", evidence));
            Assert.AreEqual(LyricsTranslationAdmission.Translate, LyricsLanguagePolicy.GetAdmission(text, "zh-Hans", evidence));
        }
        Assert.AreEqual(LyricsTranslationAdmission.Abstain, LyricsLanguagePolicy.GetAdmission("Xyzzy", "en", default));
        Assert.AreNotEqual("en", LyricsLanguagePolicy.Identify("Je t'aime pour toujours").Language);
    }

    [TestMethod]
    public void HiddenDeadlineDoesNotRestartAndManualDismissSuppressesSameContent()
    {
        var time = new Clock(); var c = new IslandExperienceCoordinator(time);
        var settings = new IslandAppearanceSettings { HideDelayMilliseconds = 3000 };
        c.UpdateSettings(settings, true);
        c.UpdateMedia(true, true, 3000, contentIdentity: "A");
        c.UpdateSettings(settings, false);
        var deadline = c.Current.NextDeadline!.Value; var generation = c.HideGeneration;
        Assert.AreEqual(IslandPresentationVariant.Music, c.Current.Variant);
        Assert.IsTrue(c.Current.PendingHide);
        time.Now += TimeSpan.FromSeconds(2);
        c.UpdateMedia(false, true, 3000, contentIdentity: "A");
        Assert.AreEqual(deadline, c.Current.NextDeadline);
        time.Now += TimeSpan.FromSeconds(1); c.OnDeadline(generation, deadline);
        Assert.AreEqual(OverlayState.Dismissing, c.Current.State);
        Assert.IsTrue(c.CompleteDismissal(generation));
        Assert.AreEqual(OverlayState.Hidden, c.Current.State);
        c.UpdateSettings(settings, true); // Permission itself cannot create content.
        Assert.AreEqual(OverlayState.Hidden, c.Current.State);
        c.UpdateMedia(true, true, 3000, contentIdentity: "A"); c.DismissNow();
        Assert.IsTrue(c.CompleteDismissal(c.HideGeneration));
        c.UpdateMedia(true, true, 3000, contentIdentity: "A");
        Assert.AreEqual(OverlayState.Hidden, c.Current.State);
        c.UpdateMedia(true, true, 3000, contentIdentity: "B");
        Assert.AreEqual(OverlayState.Compact, c.Current.State);
        c.OnDeadline(generation, deadline); Assert.AreEqual(OverlayState.Compact, c.Current.State);
    }

    [TestMethod]
    public void FullscreenToggleAndMigrationReadLegacyPresence()
    {
        Assert.IsFalse(FullscreenOverlayPolicy.Allows(false, true));
        Assert.IsTrue(FullscreenOverlayPolicy.Allows(true, true));
        Assert.IsTrue(FullscreenOverlayPolicy.Allows(false, false));
        using var raw = JsonDocument.Parse("""{"Version":14,"IslandAppearance":{"AutoHide":false,"ForceShowOverFullscreen":false,"HideDelayMilliseconds":1800},"SystemActivities":{"SuppressOverFullscreen":false}}""");
        var old = JsonSerializer.Deserialize<AppSettings>(raw.RootElement)!;
        var migrated = SettingsMigration15.Apply(old, raw.RootElement);
        Assert.IsFalse(migrated.IslandAppearance.Resident);
        Assert.IsTrue(migrated.IslandAppearance.ForceShowOverFullscreen);
        Assert.AreEqual(2000, migrated.IslandAppearance.HideDelayMilliseconds);
    }

    [TestMethod]
    public void PlaceholderRequiresWholeBodyAndOfficialAliasRetainsRecordingChecks()
    {
        var placeholder = LyricsParser.Parse("[00:00.00]纯音乐，请欣赏", LyricsProviderKind.NetEase);
        Assert.AreEqual(0, placeholder.Lines.Count);
        Assert.AreEqual(LyricsBodyQuality.PlaceholderOnly, LyricsBodyQualityPolicy.Normalize(placeholder).BodyQuality);
        Assert.IsTrue(LyricsParser.Parse("[00:00.00]I love instrumental music every day", LyricsProviderKind.NetEase).Lines.Count > 0);
        var query = new LyricsQuery("Kaiju", "Sakanaction", "", TimeSpan.FromSeconds(205));
        Assert.IsTrue(LyricsMatcher.CandidateScore(query, "怪獣", "サカナクション", "", 205) >= 4);
        Assert.IsTrue(LyricsMatcher.CandidateScore(query, "怪獣", "Some Other Singer", "", 205) < 4);
        Assert.IsTrue(LyricsMatcher.CandidateScore(query, "怪獣 (Live)", "サカナクション", "", 205) < 4);
        var appleQuery = new LyricsQuery("Kaiju", "鱼韵 — Kaiju - Single", "", TimeSpan.FromSeconds(257));
        Assert.IsTrue(LyricsMatcher.SearchTerms(appleQuery).Contains("怪獣 サカナクション"));
        Assert.IsTrue(LyricsMatcher.CandidateScore(appleQuery, "怪獣", "サカナクション", "怪獣", 257) >= 4);
    }
    [TestMethod]
    public void AppleArtistRenameMatchesRecordingWithoutAcceptingOtherVersions()
    {
        var query = new LyricsQuery("Die For You", "Abel Tesfaye — Starboy", "", TimeSpan.FromSeconds(260));
        Assert.IsTrue(LyricsMatcher.SearchTerms(query).Contains("Die For You The Weeknd"));
        Assert.IsTrue(LyricsMatcher.CandidateScore(query, "Die For You", "The Weeknd", "Starboy", 260.255) >= 4);
        Assert.AreEqual(0, LyricsMatcher.CandidateScore(query, "Die For You (Remix)", "The Weeknd; Ariana Grande", "Starboy", 232));
        Assert.AreEqual(0, LyricsMatcher.CandidateScore(query, "Die For You", "VALORANT; Grabbitz", "Die For You", 212));
        Assert.AreEqual(0, LyricsMatcher.CandidateScore(query, "Die For You", "The Weeknd Tribute", "Starboy", 260));
    }

    private sealed class Clock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
}
