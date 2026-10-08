using System.Globalization;
using DropSpace.Core.Abstractions;
using DropSpace.Core.Models;

namespace DropSpace.Core.Tests;

[TestClass]
public sealed class AppUiMessageTests
{
    [TestMethod]
    public void RetainedIdentity_RendersNestedReasonsAndRawValuesInCurrentLanguage()
    {
        var strings = new SwitchingLocalizer();
        object?[] arguments = [AppUiMessage.Resource("Reason"), 1234.5m, "Reason"];
        var retained = AppUiMessage.Resource("Status", arguments);
        arguments[0] = "mutated";
        var literal = AppUiMessage.Literal("Unavailable");
        var size = AppUiMessage.Bytes(1536);
        Assert.AreEqual("Failed: Unavailable; 1,234.5; Reason", retained.Render(strings));
        for (var index = 0; index < 4096; index++) strings.Format("Churn", index);
        strings.Language = "de-DE";
        Assert.AreEqual("Fehler: Nicht verfügbar; 1.234,5; Reason", retained.Render(strings));
        Assert.AreEqual("Unavailable", literal.Render(strings));
        Assert.AreEqual(1536L, size.ByteCount);
        Assert.AreEqual("1,5 KiB", size.Render(strings));
        Assert.AreEqual("Reason", ((AppUiMessage)retained.Arguments[0]!).ResourceKey);
    }

    private sealed class SwitchingLocalizer : IAppStringLocalizer
    {
        public string Language { get; set; } = "en-US";
        public CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
        public string Get(string key) => (key, Language) switch
        {
            ("Reason", "de-DE") => "Nicht verfügbar",
            ("Reason", _) => "Unavailable",
            ("Status", "de-DE") => "Fehler: {0}; {1:N1}; {2}",
            ("Status", _) => "Failed: {0}; {1:N1}; {2}",
            ("Kilobytes", _) => "{0:0.##} KiB",
            _ => "{0}",
        };
        public bool TryGet(string key, out string value) { value = Get(key); return true; }
        public string Format(string key, params object?[] arguments) => string.Format(Culture, Get(key), arguments);
    }
}
