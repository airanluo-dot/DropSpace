using DropSpace.Core.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class Beta15AlbumMatchingTests
{
    [TestMethod]
    public void OrdinaryAlbumWordsDoNotVetoVerifiedTrackIdentity()
    {
        foreach (var album in new[] { "Long Live Rock ’n’ Roll", "Acoustic Dreams", "Remix Planet" })
        {
            var query = new LyricsQuery("Song", "Artist", album, TimeSpan.FromSeconds(180))
            { CollectSelectionCandidates = true };
            foreach (var providerAlbum in new[] { "", "Compilation", album })
            {
                Assert.IsGreaterThan(4, LyricsMatcher.Score(query, "Song", "Artist", providerAlbum, 180), album);
                Assert.IsTrue(LyricsMatcher.IsSafeSelectionCandidate(query, "Song", "Artist", 180, providerAlbum), album);
            }
            var compilationQuery = query with { Album = "Compilation" };
            Assert.IsGreaterThan(4, LyricsMatcher.Score(compilationQuery, "Song", "Artist", album, 180), album);
            Assert.IsTrue(LyricsMatcher.IsSafeSelectionCandidate(compilationQuery, "Song", "Artist", 180, album), album);
            // Album metadata cannot veto an otherwise admissible cross-script
            // candidate with independently matching title and duration either.
            Assert.IsTrue(LyricsMatcher.IsSafeSelectionCandidate(query, "Song", "歌手", 180, ""), album);
        }
    }

    [TestMethod]
    public void AlbumRecoveryRetainsVersionLanguageCreditsDurationAndAlbumEvidence()
    {
        var query = new LyricsQuery("Song", "Artist A; Artist B", "Long Live Rock ’n’ Roll", TimeSpan.FromSeconds(180))
        { CollectSelectionCandidates = true };
        foreach (var title in new[] { "Song (Live)", "Song (Remix)", "Song (Acoustic)", "Song [日文版]" })
        {
            Assert.AreEqual(0d, LyricsMatcher.CandidateScore(query, title, query.Artist, query.Album, 180), title);
            Assert.IsFalse(LyricsMatcher.IsSafeSelectionCandidate(query, title, query.Artist, 180, query.Album), title);
        }
        foreach (var (artist, duration) in new[] { ("Artist A", 180d), ("Other Artist", 180d), (query.Artist, 240d) })
        {
            Assert.AreEqual(0d, LyricsMatcher.CandidateScore(query, query.Title, artist, query.Album, duration));
            Assert.IsFalse(LyricsMatcher.IsSafeSelectionCandidate(query, query.Title, artist, duration, query.Album));
        }
        var missingPerformerAndDuration = query with { Artist = "", Duration = TimeSpan.Zero };
        Assert.IsGreaterThan(4, LyricsMatcher.Score(missingPerformerAndDuration, "Song", "Artist", query.Album, 0));
        Assert.IsTrue(LyricsMatcher.IsSafeSelectionCandidate(missingPerformerAndDuration, "Song", "Artist", 0, query.Album));
        Assert.AreEqual(0d, LyricsMatcher.CandidateScore(missingPerformerAndDuration, "Song", "Artist", "Unrelated album", 0));
        Assert.IsFalse(LyricsMatcher.IsSafeSelectionCandidate(missingPerformerAndDuration, "Song", "Artist", 0, "Unrelated album"));
    }
}
