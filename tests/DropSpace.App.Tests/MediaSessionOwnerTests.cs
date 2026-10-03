using DropSpace.App.Services.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MediaSessionOwnerTests
{
    [TestMethod]
    public async Task FailedPreparedCandidateKeepsCanonicalSubscriptionAndIdentity()
    {
        var owner = new MediaSessionOwner<object>();
        var oldSession = new object();
        var oldSubscription = new MediaEventSubscription();
        await owner.PrepareAndCommitAsync(oldSession, _ => Task.FromResult<MediaEventSubscription?>(oldSubscription),
            _ => Task.FromResult("Healthy track"), CancellationToken.None);
        var identity = owner.Identity;
        var prepared = new MediaEventSubscription();
        var read = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidate = owner.PrepareAndCommitAsync(new object(), _ => Task.FromResult<MediaEventSubscription?>(prepared),
            _ => read.Task, CancellationToken.None);
        Assert.AreSame(oldSession, owner.Session);
        Assert.AreEqual(identity, owner.Identity);
        Assert.IsTrue(oldSubscription.IsActive);
        read.SetException(new IOException("Candidate metadata failed"));
        await Assert.ThrowsExactlyAsync<IOException>(() => candidate);
        Assert.AreSame(oldSession, owner.Session);
        Assert.AreSame(oldSubscription, owner.Subscription);
        Assert.AreEqual(identity, owner.Identity);
        Assert.IsTrue(oldSubscription.IsActive);
        Assert.IsFalse(prepared.IsActive);
        var subscriptions = 0;
        Assert.AreEqual("Healthy track", await owner.PrepareAndCommitAsync(oldSession,
            _ => { subscriptions++; return Task.FromResult<MediaEventSubscription?>(null); },
            _ => Task.FromResult("Healthy track"), CancellationToken.None));
        Assert.AreEqual(0, subscriptions, "The fallback must reuse the healthy canonical lease.");
        Assert.AreEqual(identity, owner.Identity);
        owner.Clear();
    }

    [TestMethod]
    public async Task PreparedCandidateCommitsOnlyAfterReadableMetadata()
    {
        var owner = new MediaSessionOwner<object>();
        var old = new MediaEventSubscription();
        await owner.PrepareAndCommitAsync(new object(), _ => Task.FromResult<MediaEventSubscription?>(old),
            _ => Task.FromResult("Old"), CancellationToken.None);
        var oldIdentity = owner.Identity;
        var replacement = new object();
        var subscription = new MediaEventSubscription();
        var read = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = owner.PrepareAndCommitAsync(replacement, _ => Task.FromResult<MediaEventSubscription?>(subscription),
            _ => read.Task, CancellationToken.None);
        Assert.AreNotSame(replacement, owner.Session);
        Assert.AreEqual(oldIdentity, owner.Identity);
        Assert.IsTrue(old.IsActive);
        read.SetResult("New track");
        Assert.AreEqual("New track", await pending);
        Assert.AreSame(replacement, owner.Session);
        Assert.AreSame(subscription, owner.Subscription);
        Assert.AreNotEqual(oldIdentity, owner.Identity);
        Assert.IsFalse(old.IsActive);
        Assert.IsTrue(subscription.IsActive);
        owner.Clear();
    }

    [TestMethod]
    public async Task CancellationAfterCandidateReadCannotCommitOrRetireCurrentSession()
    {
        var owner = new MediaSessionOwner<object>();
        var current = new object();
        var old = new MediaEventSubscription();
        await owner.PrepareAndCommitAsync(current, _ => Task.FromResult<MediaEventSubscription?>(old),
            _ => Task.FromResult("Old"), CancellationToken.None);
        var identity = owner.Identity;
        var prepared = new MediaEventSubscription();
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => owner.PrepareAndCommitAsync(new object(),
            _ => Task.FromResult<MediaEventSubscription?>(prepared),
            _ => { stop.Cancel(); return Task.FromResult("Late candidate"); }, stop.Token));
        Assert.AreSame(current, owner.Session);
        Assert.AreEqual(identity, owner.Identity);
        Assert.IsTrue(old.IsActive);
        Assert.IsFalse(prepared.IsActive);
        owner.Clear();
    }

    [TestMethod]
    public async Task RetryingSubscriptionForSameSessionPreservesTrackIdentity()
    {
        var owner = new MediaSessionOwner<object>();
        var session = new object();
        await owner.PrepareAndCommitAsync(session, _ => Task.FromResult<MediaEventSubscription?>(null),
            _ => Task.FromResult("Current"), CancellationToken.None);
        var identity = owner.Identity;
        var subscription = new MediaEventSubscription();
        await owner.PrepareAndCommitAsync(session, _ => Task.FromResult<MediaEventSubscription?>(subscription),
            _ => Task.FromResult("Current"), CancellationToken.None);
        Assert.AreEqual(identity, owner.Identity);
        Assert.AreSame(subscription, owner.Subscription);
        owner.Clear();
        Assert.IsNull(owner.Session);
        Assert.IsNull(owner.Subscription);
        Assert.AreEqual(string.Empty, owner.Identity);
        Assert.IsFalse(subscription.IsActive);
    }
}
