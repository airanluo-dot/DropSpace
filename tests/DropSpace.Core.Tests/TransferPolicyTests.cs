using DropSpace.Core.Transfer;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class TransferPolicyTests
{
    [TestMethod]
    public void ManifestRejectsTraversalAndChunkMismatch()
    {
        var item = new TransferItemManifest(Guid.NewGuid(), TransferItemKind.File, "file.txt", "../file.txt", 1, new string('a', 64), "text/plain", 1);
        Assert.ThrowsExactly<InvalidDataException>(() => TransferManifestPolicy.Create(Guid.NewGuid(), [item]));

        var safe = item with { RelativePath = "file.txt", ChunkCount = 2 };
        Assert.ThrowsExactly<InvalidDataException>(() => TransferManifestPolicy.Create(Guid.NewGuid(), [safe]));
    }

    [TestMethod]
    public void ZeroLengthManifestHasZeroChunksAndStableTimestamp()
    {
        var timestamp = new DateTimeOffset(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        var item = new TransferItemManifest(Guid.NewGuid(), TransferItemKind.File, "empty.txt", "empty.txt", 0, new string('0', 64), "text/plain", 0);
        var manifest = TransferManifestPolicy.Create(Guid.NewGuid(), [item], nowUtc: timestamp);

        TransferManifestPolicy.Validate(manifest);
        Assert.AreEqual(0, manifest.TotalBytes);
    }

    [TestMethod]
    public void ClipboardEnvelopeRejectsTamperingAndLoopGuardIsBounded()
    {
        var envelope = ClipboardEnvelopePolicy.CreateText(Guid.NewGuid(), 1, "hello", nowUtc: DateTimeOffset.UtcNow);
        ClipboardEnvelopePolicy.Validate(envelope);
        var tampered = envelope with { Text = "changed" };
        Assert.ThrowsExactly<InvalidDataException>(() => ClipboardEnvelopePolicy.Validate(tampered));

        var guard = new ClipboardLoopGuard(maximumEntries: 2, lifetime: TimeSpan.FromMinutes(5));
        Assert.IsTrue(guard.TryAccept(envelope));
        Assert.IsFalse(guard.TryAccept(envelope));
        var later = envelope with { EventId = Guid.NewGuid(), Sha256 = new string('b', 64) };
        Assert.IsTrue(guard.TryAccept(later));
        var third = envelope with { EventId = Guid.NewGuid(), Sha256 = new string('c', 64) };
        Assert.IsTrue(guard.TryAccept(third));
    }

    [TestMethod]
    public void ClipboardEnvelopeRejectsDeclaredLengthMismatch()
    {
        var envelope = ClipboardEnvelopePolicy.CreateText(Guid.NewGuid(), 1, "hello", nowUtc: DateTimeOffset.UtcNow);
        Assert.ThrowsExactly<InvalidDataException>(() => ClipboardEnvelopePolicy.Validate(envelope with { ByteLength = 1 }));
    }
}
