using DropSpace.Infrastructure.Lyrics;

namespace DropSpace.Infrastructure.Tests;

[TestClass]
public sealed class LlamaCompletionRunnerTests
{
    [TestMethod]
    public void CompactAndStandardUseTheirSeparateApprovedMemoryBudgets()
    {
        Assert.AreEqual(1536L * 1024 * 1024, LlamaCompletionRunner.MemoryBudgetFor(DropSpace.Core.Lyrics.AiLyricsModelCatalog.Compact.Sha256));
        Assert.AreEqual(3L * 1024 * 1024 * 1024, LlamaCompletionRunner.MemoryBudgetFor(DropSpace.Core.Lyrics.AiLyricsModelCatalog.Standard.Sha256));
    }

    [TestMethod]
    public void CompactEosCorrectionIsWhitelistedToOneVerifiedHash()
    {
        Assert.HasCount(2, LlamaCompletionRunner.ModelCompatibilityArguments(DropSpace.Core.Lyrics.AiLyricsModelCatalog.Compact.Sha256));
        Assert.AreEqual("tokenizer.ggml.eos_token_id=int:120020", LlamaCompletionRunner.ModelCompatibilityArguments(DropSpace.Core.Lyrics.AiLyricsModelCatalog.Compact.Sha256.ToUpperInvariant())[1]);
        Assert.HasCount(0, LlamaCompletionRunner.ModelCompatibilityArguments(DropSpace.Core.Lyrics.AiLyricsModelCatalog.Standard.Sha256));
        Assert.HasCount(0, LlamaCompletionRunner.ModelCompatibilityArguments(null));
        Assert.HasCount(0, LlamaCompletionRunner.ModelCompatibilityArguments("untrusted-file"));
    }

    [TestMethod]
    public void OnlyExactTrailingRuntimeMarkerIsRemoved()
    {
        Assert.AreEqual("[]", LlamaCompletionRunner.RemoveRuntimeTerminator("[] [end of text]\n\n"));
        const string withinText = "[{\"id\":0,\"text\":\"[end of text]\"}]";
        Assert.AreEqual(withinText, LlamaCompletionRunner.RemoveRuntimeTerminator(withinText));
        Assert.AreEqual("explanation []", LlamaCompletionRunner.RemoveRuntimeTerminator("explanation []"));
    }
}
