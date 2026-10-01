using System.Globalization;
using DropSpace.App.Views.Music;

namespace DropSpace.App.Tests;

[TestClass]
public sealed class LyricsFontSizeEditSessionTests
{
    [TestMethod]
    [DataRow("12", 12d)]
    [DataRow("28", 28d)]
    [DataRow("16.125", 16.125)]
    [DataRow(" 17.375 ", 17.375)]
    public void DecimalAndInclusiveBoundariesAreAccepted(string text, double expected)
    {
        Assert.IsTrue(LyricsFontSizeEditSession.TryParse(text, CultureInfo.GetCultureInfo("en-US"), out var value));
        Assert.AreEqual(expected, value);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-Infinity")]
    [DataRow("∞")]
    [DataRow("11.999")]
    [DataRow("28.001")]
    [DataRow("-12")]
    [DataRow("16.5.1")]
    [DataRow("1,600")]
    [DataRow("1e309")]
    public void InvalidInputCannotReplaceLastValidPreview(string text)
    {
        var editor = NewSession();
        editor.SetValue(18.25);
        if (LyricsFontSizeEditSession.TryParse(text, CultureInfo.GetCultureInfo("en-US"), out var value)) editor.SetValue(value);
        Assert.AreEqual(18.25, editor.Value);
    }

    [TestMethod]
    public void ParsingAndFormattingUseTheSameCultureWithoutGrouping()
    {
        foreach (var culture in new[] { "en-US", "zh-CN", "fr-FR", "de-DE" }.Select(CultureInfo.GetCultureInfo))
        {
            var text = LyricsFontSizeEditSession.Format(16.123456789, culture);
            Assert.IsTrue(LyricsFontSizeEditSession.TryParse(text, culture, out var parsed));
            Assert.AreEqual(16.123456789, parsed);
        }
        Assert.IsTrue(LyricsFontSizeEditSession.TryParse("16,25", CultureInfo.GetCultureInfo("fr-FR"), out var value));
        Assert.AreEqual(16.25, value);
        Assert.IsFalse(LyricsFontSizeEditSession.TryParse("16,25", CultureInfo.GetCultureInfo("en-US"), out _));
    }

    [TestMethod]
    public void NonFiniteAndOutOfRangeProgrammaticValuesAreRejected()
    {
        var editor = NewSession();
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, 11.99, 28.01 })
            Assert.IsFalse(editor.SetValue(value));
        Assert.AreEqual(16d, editor.Value);
        Assert.IsFalse(editor.HasChanges);
    }

    [TestMethod]
    public void InvalidInitialValueFallsBackToDefault()
    {
        Assert.AreEqual(16d, new LyricsFontSizeEditSession(double.NaN, (_, _) => Task.FromResult(true)).Value);
    }

    [TestMethod]
    public async Task DragPreviewsAreImmediateAndOnlyExplicitCommitWrites()
    {
        var writes = new List<double>();
        var editor = NewSession((value, _) => { writes.Add(value); return Task.FromResult(true); });
        for (var index = 0; index <= 1000; index++)
        {
            var value = 12 + 16 * index / 1000d;
            Assert.IsTrue(editor.SetValue(value));
            Assert.AreEqual(value, editor.Value);
        }
        Assert.AreEqual(0, writes.Count);
        await editor.FlushAsync();
        CollectionAssert.AreEqual(new[] { 28d }, writes);
        Assert.IsFalse(editor.HasChanges);
    }

    [TestMethod]
    public async Task RepeatedCommitOfUnchangedValueDoesNotFloodDisk()
    {
        var writes = 0;
        var editor = NewSession((_, _) => { writes++; return Task.FromResult(true); });
        editor.SetValue(17.625);
        for (var count = 0; count < 20; count++) await editor.FlushAsync();
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    public async Task InFlightOldWriteCannotOverwriteNewerInputAndUnloadFlush()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<double>();
        var attempts = 0;
        var editor = NewSession(async (value, isLatest) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) await blocked.Task;
            if (isLatest()) writes.Add(value);
            return true;
        });
        editor.SetValue(18.5);
        var first = editor.FlushAsync();
        editor.SetValue(20.125);
        var unload = editor.FlushAsync();
        // A newly opened page shares this session, so its even newer input wins too.
        editor.RefreshFromStore(16);
        Assert.AreEqual(20.125, editor.Value);
        editor.SetValue(22.75);
        var newPage = editor.FlushAsync();
        blocked.SetResult();
        await Task.WhenAll(first, unload, newPage).WaitAsync(TimeSpan.FromSeconds(2));
        CollectionAssert.AreEqual(new[] { 22.75 }, writes);
        Assert.AreEqual(22.75, editor.Value);
        Assert.IsFalse(editor.HasChanges);
    }

    [TestMethod]
    public async Task NewPreviewDuringSaveWaitsForItsOwnDebounceOrGestureCommit()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writes = new List<double>();
        var editor = NewSession(async (value, isLatest) =>
        {
            await blocked.Task;
            if (isLatest()) writes.Add(value);
            return true;
        });
        editor.SetValue(18);
        var oldSave = editor.FlushAsync();
        editor.SetValue(19.125);
        blocked.SetResult();
        await oldSave.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(0, writes.Count);
        Assert.IsTrue(editor.HasChanges);
        await editor.FlushAsync();
        CollectionAssert.AreEqual(new[] { 19.125 }, writes);
    }

    [TestMethod]
    public async Task ResetDuringSaveWinsWithoutLosingFixedRatio()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stored = 16d;
        var editor = NewSession(async (value, isLatest) => { await blocked.Task; if (isLatest()) stored = value; return true; });
        editor.SetValue(27.777);
        var save = editor.FlushAsync();
        editor.SetValue(LyricsFontSizeEditSession.Default);
        var reset = editor.FlushAsync();
        blocked.SetResult();
        await Task.WhenAll(save, reset).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(16d, stored);
        Assert.AreEqual(14d, editor.Value * LyricsFontSizeEditSession.TranslationRatio);
    }

    [TestMethod]
    public async Task SaveFailureKeepsPreviewAndPendingValueWithoutInfiniteRetry()
    {
        var writes = 0;
        var editor = NewSession((_, _) => { writes++; return Task.FromResult(writes > 1); });
        editor.SetValue(21.125);
        await editor.FlushAsync();
        Assert.AreEqual(1, writes);
        Assert.AreEqual(21.125, editor.Value);
        Assert.IsTrue(editor.HasChanges);
        Assert.IsTrue(editor.SaveFailed);
        editor.RefreshFromStore(16);
        Assert.AreEqual(21.125, editor.Value);
        await editor.FlushAsync();
        Assert.AreEqual(2, writes);
        Assert.IsFalse(editor.HasChanges);
        Assert.IsFalse(editor.SaveFailed);
    }

    [TestMethod]
    public async Task PersistenceExceptionIsObservedAndCanBeRetried()
    {
        var fail = true;
        var editor = NewSession((_, _) => fail ? Task.FromException<bool>(new IOException("test")) : Task.FromResult(true));
        editor.SetValue(18.875);
        await editor.FlushAsync();
        Assert.IsTrue(editor.SaveFailed);
        fail = false;
        await editor.FlushAsync();
        Assert.IsFalse(editor.SaveFailed);
        Assert.IsFalse(editor.HasChanges);
    }

    [TestMethod]
    public void StoreChangesRefreshOnlyWhenThereIsNoNewerLocalEdit()
    {
        var editor = NewSession();
        editor.RefreshFromStore(20.5);
        Assert.AreEqual(20.5, editor.Value);
        Assert.IsFalse(editor.HasChanges);
        editor.SetValue(22.25);
        editor.RefreshFromStore(14);
        editor.RefreshFromStore(double.NaN);
        Assert.AreEqual(22.25, editor.Value);
    }

    [TestMethod]
    public async Task ThrowingObserverCannotStrandACommitOrPreventOtherObservers()
    {
        var observed = 0;
        var editor = NewSession();
        editor.Changed += (_, _) => throw new InvalidOperationException("retired view");
        editor.Changed += (_, _) => observed++;
        editor.SetValue(19.625);
        await editor.FlushAsync().WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(observed >= 2);
        Assert.IsFalse(editor.HasChanges);
    }

    [TestMethod]
    public async Task OldFailureDoesNotEraseAQueuedNewerInput()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        var saved = 16d;
        var editor = NewSession(async (value, isLatest) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { await blocked.Task; return false; }
            if (isLatest()) saved = value;
            return true;
        });
        editor.SetValue(18.125);
        var oldSave = editor.FlushAsync();
        editor.SetValue(24.875);
        var newSave = editor.FlushAsync();
        blocked.SetResult();
        await Task.WhenAll(oldSave, newSave).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.AreEqual(24.875, saved);
        Assert.IsFalse(editor.SaveFailed);
        Assert.IsFalse(editor.HasChanges);
    }

    [TestMethod]
    public void NearestDoubleOutsideRangeIsRejectedWithoutClamping()
    {
        var editor = NewSession();
        Assert.IsFalse(editor.SetValue(Math.BitDecrement(12d)));
        Assert.IsFalse(editor.SetValue(Math.BitIncrement(28d)));
        Assert.IsTrue(editor.SetValue(Math.BitIncrement(12d)));
        Assert.AreEqual(Math.BitIncrement(12d), editor.Value);
        Assert.IsTrue(editor.SetValue(Math.BitDecrement(28d)));
        Assert.AreEqual(Math.BitDecrement(28d), editor.Value);
    }

    private static LyricsFontSizeEditSession NewSession(Func<double, Func<bool>, Task<bool>>? write = null) =>
        new(16, write ?? ((_, _) => Task.FromResult(true)));
}
