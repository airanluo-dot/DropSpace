using DropSpace.App.Services.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Selection = DropSpace.App.Services.Media.WindowsMediaSessionService.SessionSelection;
namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaSessionSelectionTests
{
    private sealed class Candidate(string source, Selection value)
    {
        public string Source { get; } = source;
        public Selection Value { get; set; } = value;
    }
    private static readonly Selection Weak = new("Track", "Artist", "", TimeSpan.Zero, false, true);
    private static readonly Selection Rich = new("Track", "Artist", "Album", TimeSpan.FromSeconds(180), true, true);
    private static Task<Candidate> Select(params Candidate[] candidates) => WindowsMediaSessionService.SelectRicherSessionAsync(
        candidates, value => value.Source, (value, _) => Task.FromResult<Selection?>(value.Value), CancellationToken.None);

    [TestMethod]
    public async Task FullArtistCreditsCanCompletePrimaryArtistOnlySibling()
    {
        var weak = new Candidate("player", Weak);
        var rich = new Candidate("player", Rich with { Artist = "Artist / Guest" });
        Assert.AreSame(rich, await Select(weak, rich));
    }
    [TestMethod]
    public async Task NeteaseCreditDelimitersAndPublisherTitleSuffixCanMergeDuplicateRenderers()
    {
        foreach (var credits in new[] { "Artist; Guest", "Artist、Guest", "Artist feat. Guest", "Artist x Guest" })
        {
            var weak = new Candidate("player", Weak with { Title = "Track - 网易云音乐" });
            var rich = new Candidate("player", Rich with { Artist = credits });
            Assert.AreSame(rich, await Select(weak, rich), credits);
        }
    }
    [TestMethod]
    public async Task ConflictingCreditsAndArtistNameSlashesAreNotPrefixes()
    {
        foreach (var pair in new[] { ("AC", "AC/DC"), ("Artist", "Artist Two"), ("Artist / One", "Artist / Two") })
        {
            var weak = new Candidate("player", Weak with { Artist = pair.Item1 });
            Assert.AreSame(weak, await Select(weak, new("player", Rich with { Artist = pair.Item2 })));
        }
    }
    [TestMethod]
    public async Task SameSourceObjectsRemainDistinctAndRicherObjectOwnsSelection()
    {
        var weak = new Candidate("player", Weak); var rich = new Candidate("player", Rich);
        Assert.AreSame(rich, await Select(weak, rich));
        Assert.AreSame(rich, await Select(rich, weak));
    }
    [TestMethod]
    public async Task DifferentPlayerCannotOverrideSystemPriority()
    {
        var preferred = new Candidate("preferred", Weak);
        Assert.AreSame(preferred, await Select(preferred, new("other", Rich)));
    }
    [TestMethod]
    public async Task DifferentTrackOrConflictingAlbumOrDurationCannotReplacePreferred()
    {
        foreach (var other in new[] { Rich with { Title = "Other" }, Rich with { Artist = "Other" }, Rich with { Album = "Other" }, Rich with { Duration = TimeSpan.FromSeconds(240) } })
        {
            var preferred = new Candidate("player", Rich with { CanSeek = false });
            Assert.AreSame(preferred, await Select(preferred, new("player", other)));
        }
    }
    [TestMethod]
    public async Task UnknownIdentityCannotMergeSessions()
    {
        var preferred = new Candidate("player", Weak with { Artist = "" });
        Assert.AreSame(preferred, await Select(preferred, new("player", Rich)));
    }
    [TestMethod]
    public async Task EquivalentQualityKeepsOriginalObject()
    {
        var preferred = new Candidate("player", Rich);
        Assert.AreSame(preferred, await Select(preferred, new("player", Rich)));
    }
    [TestMethod]
    public async Task UnreadableDuplicateDoesNotDiscardReadablePreferred()
    {
        var preferred = new Candidate("player", Weak); var sibling = new Candidate("player", Rich);
        var result = await WindowsMediaSessionService.SelectRicherSessionAsync(new[] { preferred, sibling }, value => value.Source,
            (value, _) => Task.FromResult<Selection?>(ReferenceEquals(value, preferred) ? value.Value : null), CancellationToken.None);
        Assert.AreSame(preferred, result);
    }
    [TestMethod]
    public async Task ParentCancellationPropagates()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await WindowsMediaSessionService.SelectRicherSessionAsync(
            new[] { new Candidate("player", Weak), new Candidate("player", Rich) }, value => value.Source,
            (_, token) => Task.FromCanceled<Selection?>(token), stop.Token));
    }

    [TestMethod]
    public async Task TransientEmptyPreferredRendererRetainsLiveControlSession()
    {
        var preferred = new Candidate("cloudmusic.exe", Weak with { Title = "", Artist = "" });
        var retained = new Candidate("cloudmusic.exe", Rich with { Title = "Previous Track" });

        var selected = await WindowsMediaSessionService.SelectRicherSessionAsync(
            new[] { preferred, retained }, value => value.Source,
            (value, _) => Task.FromResult<Selection?>(value.Value), CancellationToken.None, retained);

        Assert.AreSame(retained, selected);
    }

    [TestMethod]
    public async Task RepeatedOutOfOrderNeteaseUpdatesAlwaysRecoverRicherRenderer()
    {
        var weak = new Candidate("cloudmusic.exe", Weak with { Title = "Track 0" });
        var rich = new Candidate("cloudmusic.exe", Rich with { Title = "Track 0" });
        var candidates = new[] { weak, rich };

        for (var index = 1; index <= 256; index++)
        {
            var title = $"Track {index}";
            if ((index & 1) == 0) weak.Value = weak.Value with { Title = title };
            else rich.Value = rich.Value with { Title = title };

            Assert.AreSame(weak, await Select(candidates), $"Divergent renderers merged unsafely at switch {index}.");
            var recovery = WindowsMediaSessionService.SelectRecoverySessions(candidates, value => value.Source, 8);
            Assert.IsTrue(recovery.Any(value => ReferenceEquals(value, rich)), $"Richer renderer was lost at switch {index}.");

            if ((index & 1) == 0) rich.Value = rich.Value with { Title = title };
            else weak.Value = weak.Value with { Title = title };

            Assert.AreSame(rich, await Select(candidates), $"Richer renderer did not recover at switch {index}.");
        }
    }

    [TestMethod]
    public void RecoveryObservationIsSameSourceDistinctAndBounded()
    {
        var first = new Candidate("cloudmusic.exe", Weak);
        var second = new Candidate("cloudmusic.exe", Rich);
        var third = new Candidate("cloudmusic.exe", Rich);
        var otherPlayer = new Candidate("other.exe", Rich);

        var selected = WindowsMediaSessionService.SelectRecoverySessions(
            new[] { first, second, first, otherPlayer, third }, value => value.Source, 2);

        CollectionAssert.AreEqual(new[] { first, second }, selected.ToArray());
    }
}
