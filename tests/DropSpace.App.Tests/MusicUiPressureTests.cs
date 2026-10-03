using DropSpace.App.Views.Island;
using DropSpace.App.Views.Music;
using DropSpace.Core.Lyrics;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class MusicUiPressureTests
{
    [TestMethod]
    public void ProgressiveTranslationsReuseTwoThousandOriginalRows()
    {
        var created = 0; var updated = 0;
        var rows = new LyricsRowCollection<Row>(2000,
            line => { created++; return new(line); },
            (row, line) => { updated++; row.Line = line; });
        var lines = Enumerable.Range(0, 2000).Select(Line).ToArray();
        Assert.IsTrue(rows.Update(lines, "track", "options"));
        var originals = Enumerable.Range(0, rows.Count).Select(index => rows[index]).ToArray();
        for (var index = 0; index < 64; index++)
        {
            lines = lines.Select(line => line with { }).ToArray();
            lines[index] = lines[index] with { Secondary = $"Translation {index}", TranslationOrigin = LyricsTranslationOrigin.LocalAi };
            Assert.IsFalse(rows.Update(lines, "track", "options"), "AI progress cannot invalidate row structure or scroll anchoring.");
        }
        Assert.AreEqual(2000, created);
        Assert.AreEqual(64, updated);
        for (var index = 0; index < rows.Count; index++) Assert.AreSame(originals[index], rows[index]);
        Assert.IsFalse(rows.Update(lines, "track", "options"));
        Assert.AreEqual(64, updated);
        Assert.IsFalse(rows.Update(lines.Select(line => line with { }).ToArray(), "track", "options"));
        Assert.AreEqual(64, updated, "An all-clone publication with unchanged values must do no row presentation work.");
    }

    [TestMethod]
    public void OriginLanguageAndOptionsStillUpdateReusedRows()
    {
        var created = 0; var updated = 0;
        var rows = new LyricsRowCollection<Row>(2000,
            line => { created++; return new(line); },
            (row, line) => { updated++; row.Line = line; });
        var line = Line(0) with { Secondary = "Translation" };
        var lines = new[] { line, Line(1) };
        rows.Update(lines, "track", "options");
        var first = rows[0]; var second = rows[1];
        lines = lines.Select(value => value with { }).ToArray();
        Assert.IsFalse(rows.Update(lines, "track", "options"));
        Assert.AreEqual(0, updated);

        lines = lines.Select(value => value with { }).ToArray();
        lines[0] = lines[0] with { TranslationOrigin = LyricsTranslationOrigin.LocalAi };
        Assert.IsFalse(rows.Update(lines, "track", "options"));
        Assert.AreEqual(1, updated);
        Assert.AreEqual(LyricsTranslationOrigin.LocalAi, rows[0].Line.TranslationOrigin);
        lines = lines.Select(value => value with { }).ToArray();
        lines[0] = lines[0] with { TranslationLanguage = "en-US" };
        Assert.IsFalse(rows.Update(lines, "track", "options"));
        Assert.AreEqual(2, updated);
        Assert.AreEqual("en-US", rows[0].Line.TranslationLanguage);
        lines = lines.Select(value => value with { }).ToArray();
        lines[0] = lines[0] with { TranslationLanguage = "zh-CN" };
        Assert.IsFalse(rows.Update(lines, "track", "options"));
        Assert.AreEqual(3, updated);
        Assert.AreEqual("zh-CN", rows[0].Line.TranslationLanguage);
        Assert.IsFalse(rows.Update(lines, "track", "new-font-and-label-options"));
        Assert.AreEqual(5, updated, "Options must restyle both rows even when every line value is unchanged.");
        Assert.AreSame(first, rows[0]);
        Assert.AreSame(second, rows[1]);
        Assert.AreEqual(2, created);
    }

    [TestMethod]
    public void SettingsRestyleExistingRowsAndTrackChangesReplaceThem()
    {
        var created = 0; var updated = 0;
        var rows = new LyricsRowCollection<Row>(2000, line => { created++; return new(line); },
            (row, line) => { updated++; row.Line = line; });
        var lines = new[] { Line(0), Line(1) };
        rows.Update(lines, "first", "translation-visible");
        var first = rows[0];
        Assert.IsFalse(rows.Update(lines, "first", "translation-hidden-new-font"));
        Assert.AreSame(first, rows[0]);
        Assert.AreEqual(2, updated);
        Assert.AreEqual(2, created);
        Assert.IsTrue(rows.Update(lines, "second", "translation-hidden-new-font"));
        Assert.AreNotSame(first, rows[0]);
        Assert.AreEqual(4, created);
        Assert.IsTrue(rows.Update([], "second", "translation-hidden-new-font"));
        Assert.AreEqual(0, rows.Count);
    }

    [TestMethod]
    public void ChangedOriginalReplacesOnlyThatRowAndDuplicateIndicesRemainDistinct()
    {
        var rows = new LyricsRowCollection<Row>(2, line => new(line), (row, line) => row.Line = line);
        var line = Line(0);
        rows.Update([line, line, Line(2)], "track", "options");
        Assert.AreEqual(2, rows.Count);
        var first = rows[0]; var second = rows[1];
        Assert.AreNotSame(first, second);
        Assert.IsFalse(rows.Update([line with { Secondary = "First translation" }, line], "track", "options"));
        Assert.AreSame(first, rows[0]);
        Assert.AreSame(second, rows[1]);
        Assert.IsTrue(rows.Update([line, line with { Text = "New original" }], "track", "options"));
        Assert.AreSame(first, rows[0]);
        Assert.AreNotSame(second, rows[1]);
    }

    [TestMethod]
    public void NotificationBurstSchedulesOneRenderAndHiddenOrUnloadedViewsScheduleNone()
    {
        var queue = new MediaRenderQueue();
        var scheduled = 0;
        for (var index = 0; index < 1000; index++) if (queue.Request(true)) scheduled++;
        Assert.AreEqual(1, scheduled);
        Assert.IsTrue(queue.BeginRender(true));
        Assert.IsFalse(queue.BeginRender(true));
        for (var index = 0; index < 1000; index++) Assert.IsFalse(queue.Request(false));
        Assert.IsFalse(queue.BeginRender(false));
        Assert.IsTrue(queue.Request(true));
        queue.Cancel();
        Assert.IsFalse(queue.BeginRender(true), "A retired frame cannot render after unload.");
        Assert.IsTrue(queue.Request(true));
        Assert.IsTrue(queue.BeginRender(true), "Showing/loading again renders the latest state.");
        Assert.IsTrue(queue.Request(true));
        Assert.IsFalse(queue.BeginRender(false), "Visibility changes before a queued frame must suppress it.");
        Assert.IsTrue(queue.Request(true));
        Assert.IsTrue(queue.BeginRender(true));
    }

    private static LyricsLine Line(int index) => new(TimeSpan.FromSeconds(index), TimeSpan.FromSeconds(index + 1), $"Line {index}", null, []);
    private sealed class Row(LyricsLine line) { public LyricsLine Line { get; set; } = line; }
}
