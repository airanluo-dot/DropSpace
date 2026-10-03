using System.Numerics;
using Wuc = Windows.UI.Composition;

namespace DropSpace.App.Services;

/// <summary>Geometry-only physical pixel coverage over the controller's actual Acrylic brush.</summary>
internal sealed class AcrylicCoverageMask : IDisposable
{
    private readonly Wuc.Compositor _compositor;
    private readonly Wuc.ShapeVisual _visual = null!;
    private readonly Wuc.CompositionVisualSurface _surface = null!;
    private readonly Wuc.CompositionSurfaceBrush _surfaceBrush = null!;
    private readonly Wuc.CompositionMaskBrush _mask = null!;
    private readonly List<IDisposable> _owned = [];
    private readonly Dictionary<byte, Wuc.CompositionColorBrush> _fills = [];
    private readonly List<MaskSlot> _rectangles = [];
    private readonly Dictionary<(int Width, int Height, int Top, int Bottom), MaskPlan> _plans = [];
    private readonly Queue<(int, int, int, int)> _planOrder = [];
    private (int Width, int Height, int Top, int Bottom)? _geometry;
    private bool _disposed;
    private int _updates, _cacheHits;

    private readonly record struct MaskRectangle(int X, int Y, int Span, int Height, byte Alpha);
    private sealed class MaskSlot(Wuc.CompositionRectangleGeometry geometry, Wuc.CompositionSpriteShape shape)
    {
        internal Wuc.CompositionRectangleGeometry Geometry { get; } = geometry;
        internal Wuc.CompositionSpriteShape Shape { get; } = shape;
        internal int X, Y, Span, Height;
        internal byte Alpha;
        internal bool HasOffset, HasSize, HasFill;
    }
    private sealed record MaskPlan(byte[] Alpha, List<MaskRectangle> Rectangles);
    internal Wuc.CompositionMaskBrush Brush => _mask;
    internal Wuc.Compositor Compositor => _compositor;
    internal Wuc.CompositionVisualSurface CoverageSurface => _surface;
    internal byte[] Coverage { get; private set; } = [];

    internal AcrylicCoverageMask(Wuc.Compositor compositor)
    {
        _compositor = compositor;
        try
        {
            _visual = Own(compositor.CreateShapeVisual());
            _surface = Own(compositor.CreateVisualSurface());
            _surface.SourceVisual = _visual;
            _surfaceBrush = Own(compositor.CreateSurfaceBrush(_surface));
            _surfaceBrush.Stretch = Wuc.CompositionStretch.Fill;
            _surfaceBrush.BitmapInterpolationMode = Wuc.CompositionBitmapInterpolationMode.NearestNeighbor;
            _mask = Own(compositor.CreateMaskBrush());
            _mask.Mask = _surfaceBrush;
        }
        catch { try { Dispose(); } catch { } throw; }
    }

    private T Own<T>(T resource) where T : IDisposable { _owned.Add(resource); return resource; }
    internal void SetSource(Wuc.CompositionBrush? source) => _mask.Source = source;

    internal void UpdateGeometry(int width, int height, int topRadius, int bottomRadius)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var maximumRadius = Math.Min(width, height) / 2;
        if (width <= 0 || height <= 0 || topRadius < 0 || bottomRadius < 0 || topRadius > maximumRadius || bottomRadius > maximumRadius)
            throw new ArgumentOutOfRangeException(nameof(topRadius));
        var key = (width, height, topRadius, bottomRadius);
        if (_geometry == key) return;
        if (!_plans.TryGetValue(key, out var plan))
        {
            plan = BuildPlan(width, height, topRadius, bottomRadius);
            if (_plans.Count >= 64) _plans.Remove(_planOrder.Dequeue());
            _plans.Add(key, plan); _planOrder.Enqueue(key);
        }
        else _cacheHits++;
        // A partially failed application must not leave the prior geometry eligible for a no-op.
        _geometry = null;
        for (var index = 0; index < plan.Rectangles.Count; index++)
        {
            var rectangle = plan.Rectangles[index];
            if (index == _rectangles.Count)
            {
                var geometry = Own(_compositor.CreateRectangleGeometry());
                var shape = Own(_compositor.CreateSpriteShape(geometry));
                _visual.Shapes.Add(shape); _rectangles.Add(new MaskSlot(geometry, shape));
            }
            var item = _rectangles[index];
            if (!item.HasOffset || item.X != rectangle.X || item.Y != rectangle.Y)
            {
                item.Geometry.Offset = new Vector2(rectangle.X, rectangle.Y);
                item.X = rectangle.X; item.Y = rectangle.Y; item.HasOffset = true;
            }
            if (!item.HasSize || item.Span != rectangle.Span || item.Height != rectangle.Height)
            {
                item.Geometry.Size = new Vector2(rectangle.Span, rectangle.Height);
                item.Span = rectangle.Span; item.Height = rectangle.Height; item.HasSize = true;
            }
            if (!item.HasFill || item.Alpha != rectangle.Alpha)
            {
                item.Shape.FillBrush = GetFill(rectangle.Alpha);
                item.Alpha = rectangle.Alpha; item.HasFill = true;
            }
        }
        for (var index = plan.Rectangles.Count; index < _rectangles.Count; index++)
        {
            var item = _rectangles[index];
            if (item.HasSize && (item.Span != 0 || item.Height != 0))
            {
                item.Geometry.Size = Vector2.Zero;
                item.Span = 0; item.Height = 0;
            }
        }
        _visual.Size = new Vector2(width, height);
        _surface.SourceSize = new Vector2(width, height);
        Coverage = plan.Alpha; _geometry = key; _updates++;
    }

    private MaskPlan BuildPlan(int width, int height, int topRadius, int bottomRadius)
    {
        var alpha = new byte[checked(width * height)]; var rectangles = new List<MaskRectangle>();
        var top = GetQuarter(topRadius);
        var bottom = bottomRadius == topRadius ? top : GetQuarter(bottomRadius);
        Array.Fill(alpha, (byte)255);
        for (var y = 0; y < height; y++)
        {
            var radius = y < topRadius ? topRadius : y >= height - bottomRadius ? bottomRadius : 0;
            if (radius == 0)
            {
                // Only the fully opaque middle band is compressed; corner scanlines are unchanged.
                var middleHeight = height - bottomRadius - y;
                rectangles.Add(new MaskRectangle(0, y, width, middleHeight, 255));
                y += middleHeight - 1;
                continue;
            }
            var quarter = y < topRadius ? top : bottom;
            var quarterRow = y < topRadius ? y : height - y - 1;
            var quarterOffset = quarterRow * radius;
            var firstOpaque = radius;
            for (var x = 0; x < radius; x++)
            {
                var value = quarter[quarterOffset + x];
                alpha[y * width + x] = value;
                alpha[y * width + width - x - 1] = value;
                if (value == 255)
                {
                    if (firstOpaque == radius) firstOpaque = x;
                }
                else if (value != 0) rectangles.Add(new MaskRectangle(x, y, 1, 1, value));
            }
            if (width > firstOpaque * 2)
                rectangles.Add(new MaskRectangle(firstOpaque, y, width - firstOpaque * 2, 1, 255));
            for (var x = radius - 1; x >= 0; x--)
            {
                var value = quarter[quarterOffset + x];
                if (value != 0 && value != 255)
                    rectangles.Add(new MaskRectangle(width - x - 1, y, 1, 1, value));
            }
        }
        return new MaskPlan(alpha, rectangles);
    }

    private static byte Alpha8(int covered)
    {
        var scaled = 255 * covered;
        var quotient = scaled / 4096;
        var remainder = scaled % 4096;
        if (remainder > 2048 || remainder == 2048 && (quotient & 1) != 0) quotient++;
        return (byte)quotient;
    }

    // Exact count of the original 64x64 midpoint lattice; no RGB or area approximation.
    private static byte[] BuildQuarter(int radius)
    {
        if (radius == 0) return Array.Empty<byte>();
        var alpha = new byte[checked(radius * radius)];
        var difference = new int[radius + 1];
        var scaledRadius = (long)radius * 128;
        var squaredRadius = checked(scaledRadius * scaledRadius);
        // Walk outward from the circle center: horizontal odd reach only decreases.
        // Across the entire quarter, the loop decreases reach fewer than 64*r times.
        var oddReach = scaledRadius - 1;
        for (var y = radius - 1; y >= 0; y--)
        {
            Array.Clear(difference);
            var nearestY = (long)(radius - y - 1) * 128;
            for (var subRow = 0; subRow < 64; subRow++)
            {
                var distanceY = nearestY + 2 * subRow + 1;
                var squaredReach = squaredRadius - distanceY * distanceY;
                while (oddReach * oddReach > squaredReach) oddReach -= 2;
                // Positive odd coordinates 1,3,...reach are exactly the midpoint samples.
                var sampleColumns = (int)((oddReach + 1) / 2);
                var fullPixels = sampleColumns / 64;
                var remainder = sampleColumns % 64;
                difference[0] += 64;
                difference[fullPixels] -= 64;
                if (remainder != 0)
                {
                    difference[fullPixels] += remainder;
                    difference[fullPixels + 1] -= remainder;
                }
            }
            var covered = 0;
            for (var fromCenter = 0; fromCenter < radius; fromCenter++)
            {
                covered += difference[fromCenter];
                alpha[y * radius + radius - fromCenter - 1] = Alpha8(covered);
            }
        }
        return alpha;
    }


    private Wuc.CompositionColorBrush GetFill(byte alpha)
    {
        if (!_fills.TryGetValue(alpha, out var fill))
        {
            fill = Own(_compositor.CreateColorBrush(Microsoft.UI.ColorHelper.FromArgb(alpha, 255, 255, 255)));
            _fills.Add(alpha, fill);
        }
        return fill;
    }

    private readonly Dictionary<int, byte[]> _quarters = [];
    private readonly Queue<int> _quarterOrder = [];
    private byte[] GetQuarter(int radius)
    {
        if (!_quarters.TryGetValue(radius, out var quarter))
        {
            quarter = BuildQuarter(radius);
            if (_quarters.Count >= 64) _quarters.Remove(_quarterOrder.Dequeue());
            _quarters.Add(radius, quarter); _quarterOrder.Enqueue(radius);
        }
        return quarter;
    }
    internal object Snapshot() => new
    {
        physicalGeometry = _geometry is { } g ? new { g.Width, g.Height, TopRadius = g.Top, BottomRadius = g.Bottom } : null,
        geometryUpdates = _updates, cacheHits = _cacheHits,
        cachedPlans = _plans.Count, shapeCount = _rectangles.Count, colorBrushCount = _fills.Count,
        sourceType = _mask.Source?.GetType().FullName, sourceIsMask = _mask.Source is Wuc.CompositionMaskBrush,
        transparentPixels = Coverage.Count(x => x == 0), partialPixels = Coverage.Count(x => x is > 0 and < 255),
        alphaOnly = true, interpolation = _surfaceBrush.BitmapInterpolationMode.ToString()
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var errors = new List<Exception>();
        // The owner detaches the target first; no framework-owned source or compositor is closed.
        for (var index = _owned.Count - 1; index >= 0; index--)
            try { _owned[index].Dispose(); } catch (Exception error) { errors.Add(error); }
        _owned.Clear(); _fills.Clear(); _rectangles.Clear(); _plans.Clear(); _planOrder.Clear(); _quarters.Clear(); _quarterOrder.Clear(); Coverage = [];
        if (errors.Count != 0) throw new AggregateException("Acrylic coverage cleanup failed.", errors);
    }
}
