using DropSpace.Core.Lyrics;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class LyricsTranslationPolicyTests
{
    private static readonly IReadOnlySet<string> Supported = new HashSet<string> { "zh", "en", "ja", "ko", "zh-Hant" };

    [TestMethod]
    public void TargetUsesTheSameResolutionAsAppResources()
    {
        Assert.AreEqual("en-US", LyricsTranslationPolicy.ResolveTarget(AppLanguagePreference.English, ["zh-CN"]));
        Assert.AreEqual("zh-CN", LyricsTranslationPolicy.ResolveTarget(AppLanguagePreference.SimplifiedChinese, ["en-US"]));
        Assert.AreEqual("en-US", LyricsTranslationPolicy.ResolveTarget(AppLanguagePreference.System, ["ja-JP"]));
        Assert.AreEqual("zh-CN", LyricsTranslationPolicy.ResolveTarget(AppLanguagePreference.System, ["zh-TW"]));
    }

    [TestMethod]
    public void OtherTargetProviderTranslationDoesNotSuppressAi()
    {
        Assert.AreEqual(LyricsTranslationDecision.TranslateLocally,
            LyricsTranslationPolicy.Decide(true, true, "en-US", "ja", "zh-CN", true, Supported));
        Assert.AreEqual(LyricsTranslationDecision.UseProvider,
            LyricsTranslationPolicy.Decide(true, true, "en-US", "ja", "en-GB", true, Supported));
    }

    [TestMethod]
    public void ChineseAndEnglishAreBothSupportedTargets()
    {
        foreach (var target in new[] { "zh-CN", "en-US" })
        foreach (var source in new[] { "ja", "ko" })
            Assert.AreEqual(LyricsTranslationDecision.TranslateLocally,
                LyricsTranslationPolicy.Decide(true, true, target, source, null, false, Supported));
    }

    [TestMethod]
    public void SameLanguageDisabledAndMissingOriginalAreSafe()
    {
        Assert.AreEqual(LyricsTranslationDecision.OriginalOnly, LyricsTranslationPolicy.Decide(true, true, "en-US", "en-GB", null, false, Supported));
        Assert.AreEqual(LyricsTranslationDecision.OriginalOnly, LyricsTranslationPolicy.Decide(false, true, "en", "ja", null, false, Supported));
        Assert.AreEqual(LyricsTranslationDecision.OriginalOnly, LyricsTranslationPolicy.Decide(true, false, "en", "ja", null, false, Supported));
        Assert.AreEqual(LyricsTranslationDecision.UnsupportedLanguage, LyricsTranslationPolicy.Decide(true, true, "en", "xx", null, false, Supported));
    }
}
