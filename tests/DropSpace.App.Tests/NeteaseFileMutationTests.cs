using DropSpace.App.Services.NeteaseEnhancement;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class NeteaseFileMutationTests
{
    [TestMethod]
    public async Task ReleasedImageContentionRetriesTheMutationOnly()
    {
        int calls = 0, waits = 0;
        await InfLinkDeploymentService.RetryFileMutationAsync(() =>
        {
            if (++calls < 3) throw new UnauthorizedAccessException();
        }, CancellationToken.None, (_, _) => { waits++; return Task.CompletedTask; });
        Assert.AreEqual(3, calls);
        Assert.AreEqual(2, waits);
    }

    [TestMethod]
    public async Task PersistentPermissionFailureRemainsVisibleAndBounded()
    {
        int calls = 0;
        var expected = new UnauthorizedAccessException();
        var actual = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() =>
            InfLinkDeploymentService.RetryFileMutationAsync(() => { calls++; throw expected; },
                CancellationToken.None, (_, _) => Task.CompletedTask));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(7, calls);
    }

    [TestMethod]
    public async Task CancellationDuringContentionDoesNotPerformAnotherMutation()
    {
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            InfLinkDeploymentService.RetryFileMutationAsync(() => { calls++; throw new UnauthorizedAccessException(); },
                cancellation.Token, (_, _) => { cancellation.Cancel(); return Task.CompletedTask; }));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task SuccessfulMutationHasNoArtificialWait()
    {
        await InfLinkDeploymentService.RetryFileMutationAsync(() => { }, CancellationToken.None,
            (_, _) => throw new AssertFailedException("Success must not wait."));
    }

    [TestMethod]
    public async Task UnrelatedIoFailureIsNotRetried()
    {
        await Assert.ThrowsExactlyAsync<IOException>(() => InfLinkDeploymentService.RetryFileMutationAsync(
            () => throw new IOException("Disk failure", unchecked((int)0x80070070)), CancellationToken.None,
            (_, _) => throw new AssertFailedException("Unrelated failures must not wait.")));
    }
}
