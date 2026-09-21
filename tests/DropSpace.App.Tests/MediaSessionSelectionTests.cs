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
        public Selection Value { get; } = value;
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
        var result = await WindowsMediaSessionService.SelectRicherSessionAsync(new[] {preferred, sibling}, value => value.Source,
            (value, _) => Task.FromResult<Selection?>(ReferenceEquals(value, preferred) ? value.Value : null), CancellationToken.None);
        Assert.AreSame(preferred, result);
    }
    [TestMethod]
    public async Task ParentCancellationPropagates()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () => await WindowsMediaSessionService.SelectRicherSessionAsync(
            new[] {new Candidate("player", Weak), new Candidate("player", Rich)}, value => value.Source,
            (_, token) => Task.FromCanceled<Selection?>(token), stop.Token));
    }
}
