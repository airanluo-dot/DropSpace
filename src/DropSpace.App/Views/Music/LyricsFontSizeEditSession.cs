using System.Globalization;

namespace DropSpace.App.Views.Music;

/// <summary>Shared per settings editor: previews are immediate, writes are explicit and latest-value-only.</summary>
internal sealed class LyricsFontSizeEditSession
{
    internal const double Minimum = 12;
    internal const double Maximum = 28;
    internal const double Default = 16;
    internal const double TranslationRatio = 0.875;
    private readonly object _gate = new();
    private readonly Func<double, Func<bool>, Task<bool>> _write;
    private double _value;
    private long _revision;
    private bool _dirty, _commitRequested, _saveFailed;
    private TaskCompletionSource? _flush;

    internal LyricsFontSizeEditSession(double value, Func<double, Func<bool>, Task<bool>> write)
    {
        _value = IsValid(value) ? value : Default;
        _write = write;
    }

    internal event EventHandler? Changed;
    internal double Value { get { lock (_gate) return _value; } }
    internal bool HasChanges { get { lock (_gate) return _dirty; } }
    internal bool SaveFailed { get { lock (_gate) return _saveFailed; } }
    internal static bool IsValid(double value) => double.IsFinite(value) && value >= Minimum && value <= Maximum;
    internal static bool TryParse(string? text, CultureInfo culture, out double value) =>
        double.TryParse(text, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, culture, out value) && IsValid(value);
    internal static string Format(double value, CultureInfo culture) => value.ToString("0.###############", culture);

    internal bool SetValue(double value)
    {
        if (!IsValid(value)) return false;
        lock (_gate)
        {
            if (_value == value && !_saveFailed) return true;
            _value = value;
            ++_revision;
            _dirty = true;
            _saveFailed = false;
        }
        NotifyChanged();
        return true;
    }

    internal void RefreshFromStore(double value)
    {
        if (!IsValid(value)) return;
        lock (_gate)
        {
            // Store notifications from an older in-flight write must not replace a newer preview.
            if (_dirty || _value == value) return;
            _value = value;
        }
        NotifyChanged();
    }

    internal Task FlushAsync()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (!_dirty) return _flush?.Task ?? Task.CompletedTask;
            _commitRequested = true;
            if (_flush is not null) return _flush.Task;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _flush = completion;
        }
        _ = WriteLoopAsync(completion);
        return completion.Task;
    }

    private async Task WriteLoopAsync(TaskCompletionSource completion)
    {
        while (true)
        {
            double value;
            long revision;
            lock (_gate)
            {
                if (!_commitRequested || !_dirty)
                {
                    _flush = null;
                    completion.TrySetResult();
                    return;
                }
                _commitRequested = false;
                value = _value;
                revision = _revision;
            }
            bool saved;
            try
            {
                // The editor invokes this predicate inside its serialized latest-settings transaction.
                saved = await _write(value, () => { lock (_gate) return revision == _revision; });
            }
            catch (Exception) { saved = false; }
            lock (_gate)
            {
                if (revision == _revision)
                {
                    _dirty = !saved;
                    _saveFailed = !saved;
                    // Failed writes stay available for a later explicit retry, never a hot retry loop.
                    if (!saved) _commitRequested = false;
                }
            }
            NotifyChanged();
        }
    }
    private void NotifyChanged()
    {
        if (Changed is not { } changed) return;
        foreach (EventHandler subscriber in changed.GetInvocationList())
        {
            try { subscriber(this, EventArgs.Empty); }
            catch (Exception error) { System.Diagnostics.Debug.WriteLine($"Font-size observer failed: {error.GetType().Name}"); }
        }
    }

}
