using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LlamaCompletionRunnerTests
{
    [TestMethod]
    public void OnlyExactTrailingRuntimeMarkerIsRemoved()
    {
        Assert.AreEqual("[]", LlamaCompletionRunner.RemoveRuntimeTerminator("[] [end of text]\n\n"));
        const string withinText = "[{\"id\":0,\"text\":\"[end of text]\"}]";
        Assert.AreEqual(withinText, LlamaCompletionRunner.RemoveRuntimeTerminator(withinText));
        Assert.AreEqual("explanation []", LlamaCompletionRunner.RemoveRuntimeTerminator("explanation []"));
    }
}
