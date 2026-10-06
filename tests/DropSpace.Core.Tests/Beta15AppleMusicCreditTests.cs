using DropSpace.Core.Lyrics;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class Beta15AppleMusicCreditTests
{
    private const string Album = "FAST X (Original Motion Picture Soundtrack)";
    private static LyricsQuery AppleMusicMyCity => new("My City (feat. G Herbo)",
        "24kGoldn, Kane Brown & Fast & Furious: The Fast Saga — " + Album, "", TimeSpan.FromSeconds(149),
        "apple-music-recording", "24kGoldn, Kane Brown & Fast & Furious: The Fast Saga — " + Album);

    [TestMethod]
    public void ActualPublisherCreditsAcceptOnlyCompleteOriginalRecording()
    {
        var query = AppleMusicMyCity;
        Assert.IsTrue(LyricsMatcher.Score(query, "My City", "24kGoldn; Kane Brown; G Herbo", Album, 149.263) >= 4);
        Assert.IsTrue(LyricsMatcher.CandidateScore(query with { CollectSelectionCandidates = true }, "My City",
            "24kGoldn; Kane Brown; G Herbo", Album, 149.263) >= 4);
        Assert.IsTrue(LyricsMatcher.IsSafeSelectionCandidate(query, "My City", "24kGoldn; Kane Brown; G Herbo", 149.263, Album));
        foreach (var candidate in new[]
        {
            "24kGoldn; Kane Brown", "24kGoldn; G Herbo", "Kane Brown; G Herbo",
            "24kGoldn; Kane Brown; Another Singer", "24kGoldn; Kane Brown; G Herbo; Another Singer",
        })
        {
            Assert.AreEqual(0, LyricsMatcher.Score(query, "My City", candidate, Album, 149.263));
            Assert.AreEqual(0, LyricsMatcher.CandidateScore(query with { CollectSelectionCandidates = true },
                "My City", candidate, Album, 149.263));
        }
        Assert.AreEqual(0, LyricsMatcher.Score(query, "My City (Sped Up)", "24kGoldn; Kane Brown; G Herbo", Album, 124.135));
        Assert.AreEqual(0, LyricsMatcher.Score(query, "My City (Live)", "24kGoldn; Kane Brown; G Herbo", Album, 149.263));
        Assert.AreEqual(0, LyricsMatcher.Score(query, "My City", "24kGoldn; Kane Brown; G Herbo", Album, 240));
    }

    [TestMethod]
    public void BoundedReleaseEntityCannotEraseUnknownArtistOrMissingAlbumEvidence()
    {
        var original = AppleMusicMyCity;
        foreach (var query in new[]
        {
            original with { Artist = original.Artist.Replace("Fast & Furious: The Fast Saga", "Another Singer", StringComparison.Ordinal) },
            original with { Artist = "24kGoldn, Kane Brown & Fast & Furious: The Fast Saga", Album = "" },
            original with { Title = "My City (feat. Another Singer)" },
            original with { Title = "Another Song (feat. G Herbo)" },
        }) Assert.AreEqual(0, LyricsMatcher.Score(query, LyricsMatcher.SearchTitle(query.Title),
            "24kGoldn; Kane Brown; G Herbo", Album, 149.263));
        Assert.AreEqual(0, LyricsMatcher.Score(original, "My City", "24kGoldn; Kane Brown; G Herbo", "Another Album", 149.263));
    }

    [TestMethod]
    public void FeaturedTitleCreditsAreRequiredOnBothSidesWithoutNarrowAlbumArtistEscape()
    {
        var query = new LyricsQuery("Shared Song (feat. Performer B)", "Performer A", "Album", TimeSpan.FromSeconds(150),
            AlbumArtist: "Performer A");
        Assert.IsTrue(LyricsMatcher.Score(query, "Shared Song", "Performer B; Performer A", "Album", 150) >= 4);
        Assert.IsTrue(LyricsMatcher.Score(query, "Shared Song (feat. Performer B)", "Performer A", "Album", 150) >= 4);
        Assert.AreEqual(0, LyricsMatcher.Score(query, "Shared Song", "Performer A", "Album", 150));
        Assert.AreEqual(0, LyricsMatcher.Score(query, "Shared Song (feat. Performer C)", "Performer A", "Album", 150));
        var complete = new LyricsQuery("Shared Song", "Performer A; Performer B", "Album", TimeSpan.FromSeconds(150),
            AlbumArtist: "Performer A");
        Assert.AreEqual(0, LyricsMatcher.Score(complete, "Shared Song", "Performer A", "Album", 150));
        Assert.AreEqual(0, LyricsMatcher.CandidateScore(complete with { CollectSelectionCandidates = true },
            "Shared Song", "Performer A", "Album", 150));
        Assert.IsTrue(LyricsMatcher.Score(query with { Title = "Shared Song (feat. Performer B) [Live]" },
            "Shared Song [Live]", "Performer A; Performer B", "Album", 150) >= 4);
        Assert.AreEqual(0, LyricsMatcher.Score(query with { Title = "Shared Song (feat. Performer B) [Live]" },
            "Shared Song [Live]", "Performer A", "Album", 150));
    }

    [TestMethod]
    public void WindLanguageVersionsRequireTheirFullVocalCredits()
    {
        var english = new LyricsQuery("风的来信 (feat. Griffin Burns) [英文版]",
            "HOYO-MiX — 原神 - 风的来信 (《原神》六周年主题曲) - EP", "", TimeSpan.FromSeconds(197));
        const string catalog = "风的来信 A Letter From the Wind";
        Assert.AreEqual(0, LyricsMatcher.Score(english, catalog, "HOYO-MiX", "", 197));
        Assert.AreEqual(0, LyricsMatcher.Score(english, catalog, "HOYO-MiX; 孙晔Gary", "", 197));
        Assert.AreEqual(0, LyricsMatcher.CandidateScore(english with { CollectSelectionCandidates = true },
            catalog, "HOYO-MiX; 孙晔Gary", "", 197));
        Assert.IsTrue(LyricsMatcher.Score(english, catalog, "HOYO-MiX; Griffin Burns", "", 197) >= 4);
        Assert.AreEqual(0, LyricsMatcher.Score(english, "风的来信 [中文版]", "HOYO-MiX; Griffin Burns", "", 197));
        var chinese = english with { Title = "风的来信 (feat. 孙晔) [中文版]" };
        Assert.IsTrue(LyricsMatcher.Score(chinese, catalog, "HOYO-MiX; 孙晔Gary", "", 197) >= 4);
        Assert.AreEqual(0, LyricsMatcher.Score(chinese, catalog, "HOYO-MiX; Griffin Burns", "", 197));
    }
}
