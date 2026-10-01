using DropSpace.Core.Lyrics;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsInferenceCircuitTests
{
    [TestMethod]
    public void ThreeConsecutiveFailuresPauseUntilManualResume()
    {
        var circuit = new LyricsInferenceCircuit();
        for (var i = 0; i < 3; i++)
        {
            Assert.IsTrue(circuit.TryBegin(out var generation));
            circuit.Complete(generation, false);
            Assert.AreEqual(i == 2, circuit.IsPaused);
        }
        Assert.IsFalse(circuit.TryBegin(out var oldGeneration));
        circuit.Complete(oldGeneration, true);
        Assert.IsTrue(circuit.IsPaused);
        circuit.Resume();
        Assert.IsTrue(circuit.TryBegin(out _));
        circuit.Complete(oldGeneration, false);
        Assert.IsFalse(circuit.IsPaused);
    }

    [TestMethod]
    public void SuccessResetsCountAndCancellationDoesNotCount()
    {
        var circuit = new LyricsInferenceCircuit();
        circuit.TryBegin(out var generation);
        circuit.Complete(generation, false);
        circuit.Complete(generation, false);
        // A caller-cancelled attempt reports no completion at all.
        Assert.IsTrue(circuit.TryBegin(out _));
        circuit.Complete(generation, true);
        circuit.Complete(generation, false);
        circuit.Complete(generation, false);
        Assert.IsFalse(circuit.IsPaused);
    }
}
