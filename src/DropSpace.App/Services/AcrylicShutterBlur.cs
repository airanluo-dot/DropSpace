using System.Numerics;
using Wuc = Windows.UI.Composition;

namespace DropSpace.App.Services;

internal enum IslandBlurComparison { ShutterCoverage, GaussianMaterial }

/// <summary>
/// A bounded, symmetric three-tap shutter convolution of geometry coverage. Original Acrylic
/// remains the material source; the current AA mask clips the result. This intentionally blurs
/// the moving outline inward, not SurfaceContent, glow, or the entire object's RGB texture.
/// One graph per compositor; updates write only changed scalar parameters.
/// </summary>
internal sealed class AcrylicShutterBlur : IDisposable
{
    private readonly AcrylicCoverageMask _coverage;
    private readonly List<IDisposable> _owned = [];
    private readonly Wuc.CompositionPropertySet _parameters = null!;
    private readonly Wuc.CompositionEffectBrush _shutterMaterial = null!;
    private Wuc.CompositionEffectBrush? _gaussian;
    private Wuc.CompositionBrush? _source;
    private Wuc.CompositionBrush? _installed;
    private IslandBlurComparison _mode;
    private bool _active, _disposed;
    private float _strength, _scaleX = 1, _scaleY = 1, _offsetX, _offsetY;
    private long _scalarWrites, _sourceSwitches;

    internal AcrylicShutterBlur(AcrylicCoverageMask coverage)
    {
        _coverage = coverage;
        var compositor = coverage.Compositor;
        try
        {
            _parameters = Own(compositor.CreatePropertySet());
            _parameters.InsertScalar("Strength", 0);
            _parameters.InsertScalar("PastScaleX", 1);
            _parameters.InsertScalar("PastScaleY", 1);
            _parameters.InsertScalar("PastOffsetX", 0);
            _parameters.InsertScalar("PastOffsetY", 0);
            // Existing outer AA represents the current tap. A white center prevents A*A
            // from darkening partial AA pixels as soon as motion strength becomes nonzero.
            var current = Own(compositor.CreateColorBrush(new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 }));
            var past = CreateTap(nearest: false);
            var future = CreateTap(nearest: false);
            Animate(past, "Scale", "Vector2(P.PastScaleX, P.PastScaleY)");
            Animate(past, "Offset", "Vector2(P.PastOffsetX, P.PastOffsetY)");
            Animate(future, "Scale", "Vector2(2 - P.PastScaleX, 2 - P.PastScaleY)");
            Animate(future, "Offset", "Vector2(-P.PastOffsetX, -P.PastOffsetY)");
            var pair = new ArithmeticCompositeEffectDescription("Pair",
                new Wuc.CompositionEffectSourceParameter("past"), new Wuc.CompositionEffectSourceParameter("future"),
                source1Amount: .5f, source2Amount: .5f);
            var description = new ArithmeticCompositeEffectDescription("Shutter",
                new Wuc.CompositionEffectSourceParameter("now"), pair);
            // CompositionMaskBrush.Mask rejects an effect brush (verified headlessly).
            // Multiply material and shutter alpha within this supported effect graph instead.
            var material = new ArithmeticCompositeEffectDescription("Material",
                new Wuc.CompositionEffectSourceParameter("material"), description,
                multiplyAmount: 1, source1Amount: 0, source2Amount: 0);
            var factory = Own(compositor.CreateEffectFactory(material,
                ["Shutter.Source1Amount", "Shutter.Source2Amount"]));
            _shutterMaterial = Own(factory.CreateBrush());
            _shutterMaterial.SetSourceParameter("now", current);
            _shutterMaterial.SetSourceParameter("past", past);
            _shutterMaterial.SetSourceParameter("future", future);
            Animate(_shutterMaterial.Properties, "Shutter.Source1Amount", "1 - 2 * P.Strength / 3");
            Animate(_shutterMaterial.Properties, "Shutter.Source2Amount", "2 * P.Strength / 3");
        }
        catch { ReleaseOwned(ignoreErrors: true); throw; }
    }

    private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }

    private Wuc.CompositionSurfaceBrush CreateTap(bool nearest)
    {
        var tap = Own(_coverage.Compositor.CreateSurfaceBrush(_coverage.CoverageSurface));
        tap.Stretch = Wuc.CompositionStretch.Fill;
        tap.HorizontalAlignmentRatio = 0;
        tap.VerticalAlignmentRatio = 0;
        tap.CenterPoint = Vector2.Zero;
        tap.BitmapInterpolationMode = nearest
            ? Wuc.CompositionBitmapInterpolationMode.NearestNeighbor
            : Wuc.CompositionBitmapInterpolationMode.Linear;
        return tap;
    }

    private void Animate(Wuc.CompositionObject target, string property, string expression)
    {
        var animation = Own(_coverage.Compositor.CreateExpressionAnimation(expression));
        animation.SetReferenceParameter("P", _parameters);
        target.StartAnimation(property, animation);
    }

    // The source is borrowed from DesktopAcrylicController, and is never disposed here.
    internal void SetSource(Wuc.CompositionBrush? source, bool force = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!force && ReferenceEquals(source, _source)) return;
        if (source is not null && !ReferenceEquals(source.Compositor, _coverage.Compositor))
            throw new ArgumentException("Recreate the graph when the source compositor changes.", nameof(source));
        _shutterMaterial.SetSourceParameter("material", source!);
        _gaussian?.SetSourceParameter("material", source!);
        _source = source;
        if (source is null) { _active = false; Write("Strength", ref _strength, 0); }
        InstallSource();
    }

    internal void SetComparison(IslandBlurComparison mode)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mode == IslandBlurComparison.GaussianMaterial && _gaussian is null)
        {
            // Comparison graph is optional and lazy: its failure cannot block shutter setup.
            var factory = _coverage.Compositor.CreateEffectFactory(
                new GaussianBlurEffectDescription("Softness", new Wuc.CompositionEffectSourceParameter("material")),
                ["Softness.BlurAmount"]);
            Wuc.CompositionEffectBrush? brush = null;
            Wuc.ExpressionAnimation? expression = null;
            try
            {
                brush = factory.CreateBrush();
                if (_source is not null) brush.SetSourceParameter("material", _source);
                expression = _coverage.Compositor.CreateExpressionAnimation("2.5 * P.Strength");
                expression.SetReferenceParameter("P", _parameters);
                brush.Properties.StartAnimation("Softness.BlurAmount", expression);
                Own(factory); Own(brush); Own(expression); _gaussian = brush;
            }
            catch
            {
                try { expression?.Dispose(); } catch { }
                try { brush?.Dispose(); } catch { }
                try { factory.Dispose(); } catch { }
                throw;
            }
        }
        _mode = mode;
        InstallSource();
    }

    internal void SetMotion(IslandMotionBlurFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var strength = double.IsFinite(frame.Strength) ? (float)Math.Clamp(frame.Strength, 0, 1) : 0;
        var past = frame.Sample1;
        var sx = (float)past.ScaleX; var sy = (float)past.ScaleY;
        var ox = (float)past.OffsetX; var oy = (float)past.OffsetY;
        if (!float.IsFinite(sx) || !float.IsFinite(sy) || !float.IsFinite(ox) || !float.IsFinite(oy) ||
            sx <= 0 || sy <= 0 || sx >= 2 || sy >= 2)
            strength = 0;
        Write("PastScaleX", ref _scaleX, strength > 0 ? sx : 1);
        Write("PastScaleY", ref _scaleY, strength > 0 ? sy : 1);
        Write("PastOffsetX", ref _offsetX, strength > 0 ? ox : 0);
        Write("PastOffsetY", ref _offsetY, strength > 0 ? oy : 0);
        Write("Strength", ref _strength, strength);
        _active = strength > 0 && _source is not null;
        InstallSource();
    }

    private void Write(string name, ref float oldValue, float value)
    {
        if (oldValue == value) return;
        _parameters.InsertScalar(name, value);
        oldValue = value; _scalarWrites++;
    }

    private void InstallSource()
    {
        Wuc.CompositionBrush? chosen = _active
            ? _mode == IslandBlurComparison.GaussianMaterial ? _gaussian : _shutterMaterial
            : _source;
        if (ReferenceEquals(_installed, chosen)) return;
        _coverage.SetSource(chosen);
        _installed = chosen; _sourceSwitches++;
    }

    internal bool IsActive => _active;
    internal long ScalarWrites => _scalarWrites;
    internal long SourceSwitches => _sourceSwitches;
    internal Wuc.CompositionBrush? OriginalSource => _source;
    internal Wuc.CompositionBrush? InstalledSource => _installed;
    internal object Snapshot() => new
    {
        active = _active, mode = _mode.ToString(), strength = _strength,
        past = new { scaleX = _scaleX, scaleY = _scaleY, offsetX = _offsetX, offsetY = _offsetY },
        scalarWrites = _scalarWrites, sourceSwitches = _sourceSwitches,
        ownedObjects = _owned.Count, sourceType = _source?.GetType().FullName
    };

    public void Dispose()
    {
        if (_disposed) return;
        // Caller detaches the real native target first. If restoration fails, retain the graph
        // so the caller can retry or release it only after that native target closes.
        _coverage.SetSource(_source);
        ReleaseAfterTargetDetached();
    }

    internal void ReleaseAfterTargetDetached()
    {
        if (_disposed) return;
        _disposed = true; _active = false; _source = null; _installed = null;
        ReleaseOwned(ignoreErrors: false);
    }

    private void ReleaseOwned(bool ignoreErrors)
    {
        List<Exception>? errors = null;
        for (var index = _owned.Count - 1; index >= 0; index--)
            try { _owned[index].Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
        _owned.Clear();
        if (!ignoreErrors && errors is not null) throw new AggregateException(errors);
    }
}
