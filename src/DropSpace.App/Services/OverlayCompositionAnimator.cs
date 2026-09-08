using System.Numerics;
using DropSpace.Core.Overlay;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace DropSpace.App.Services;

/// <summary>
/// Keeps opacity, content choreography, hover tint, and press feedback on the compositor. Native
/// geometry remains owned by OverlayWindow so the OLE hit region stays exact and fail-closed.
/// </summary>
internal sealed class OverlayCompositionAnimator : IDisposable
{
    private readonly Compositor _compositor;
    private readonly Visual _surface;
    private readonly Visual _shadow;
    private readonly Visual _compact;
    private readonly Visual _drag;
    private readonly Visual _expanded;
    private readonly Visual _content;
    private readonly Visual _interactionTint;
    private readonly Vector3 _contentBaseOffset;
    private CompositionAnimation? _surfaceOpacityAnimation;
    private CompositionAnimation? _shadowOpacityAnimation;
    private CompositionAnimation? _compactOpacityAnimation;
    private CompositionAnimation? _dragOpacityAnimation;
    private CompositionAnimation? _expandedOpacityAnimation;
    private CompositionAnimation? _contentOffsetAnimation;
    private CompositionAnimation? _contentScaleAnimation;
    private CompositionAnimation? _interactionTintAnimation;
    private bool _disposed;

    public OverlayCompositionAnimator(
        FrameworkElement surface,
        FrameworkElement shadow,
        FrameworkElement compact,
        FrameworkElement drag,
        FrameworkElement expanded,
        FrameworkElement content,
        FrameworkElement interactionTint)
    {
        _surface = ElementCompositionPreview.GetElementVisual(surface);
        _shadow = ElementCompositionPreview.GetElementVisual(shadow);
        _compact = ElementCompositionPreview.GetElementVisual(compact);
        _drag = ElementCompositionPreview.GetElementVisual(drag);
        _expanded = ElementCompositionPreview.GetElementVisual(expanded);
        _content = ElementCompositionPreview.GetElementVisual(content);
        _interactionTint = ElementCompositionPreview.GetElementVisual(interactionTint);
        _contentBaseOffset = _content.Offset;
        _compositor = _surface.Compositor;
    }

    public void AnimateTo(
        OverlayMotionValues current,
        OverlayMotionValues target,
        ContentTransitionProfile profile,
        bool reducedMotion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var surfaceDuration = reducedMotion
            ? OverlayMotionTokens.FasterMilliseconds
            : OverlayMotionTokens.FastMilliseconds;
        AnimateScalar(
            ref _surfaceOpacityAnimation,
            _surface,
            "Opacity",
            current.Opacity,
            target.Opacity,
            surfaceDuration);
        AnimateScalar(
            ref _shadowOpacityAnimation,
            _shadow,
            "Opacity",
            current.Opacity * current.ShadowOpacity * 0.35,
            target.Opacity * target.ShadowOpacity * 0.35,
            surfaceDuration);
        AnimateContent(
            ref _compactOpacityAnimation,
            _compact,
            current.CompactContent,
            target.CompactContent,
            profile,
            reducedMotion);
        AnimateContent(
            ref _dragOpacityAnimation,
            _drag,
            current.DragContent,
            target.DragContent,
            profile,
            reducedMotion);
        AnimateContent(
            ref _expandedOpacityAnimation,
            _expanded,
            current.ExpandedContent,
            target.ExpandedContent,
            profile,
            reducedMotion);
        AnimateContentOffset(
            target.CompactContent > current.CompactContent ||
            target.DragContent > current.DragContent ||
            target.ExpandedContent > current.ExpandedContent,
            profile,
            reducedMotion);
    }

    public void SnapTo(OverlayMotionValues values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopAll();
        _surface.Opacity = (float)values.Opacity;
        _shadow.Opacity = (float)(values.Opacity * values.ShadowOpacity * 0.35);
        _compact.Opacity = (float)values.CompactContent;
        _drag.Opacity = (float)values.DragContent;
        _expanded.Opacity = (float)values.ExpandedContent;
        _content.Scale = Vector3.One;
        _content.Offset = _contentBaseOffset;
        _interactionTint.Opacity = 0;
    }

    public void ApplyHover(bool entered, bool reducedMotion)
    {
        AnimateScalar(
            ref _interactionTintAnimation,
            _interactionTint,
            "Opacity",
            _interactionTint.Opacity,
            entered ? 0.08 : 0,
            reducedMotion ? OverlayMotionTokens.FasterMilliseconds : OverlayMotionTokens.FasterMilliseconds);
    }

    public void ApplyPress(bool pressed, bool reducedMotion)
    {
        var from = _content.Scale.X;
        var to = pressed && !reducedMotion ? OverlayMotionTokens.PressScale : 1;
        StopAnimation(_content, nameof(Visual.Scale), ref _contentScaleAnimation);
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        try
        {
            animation.InsertKeyFrame(0f, new Vector3(from, from, 1));
            animation.InsertKeyFrame(1f, new Vector3((float)to, (float)to, 1));
            animation.Duration = TimeSpan.FromMilliseconds(
                reducedMotion ? OverlayMotionTokens.FasterMilliseconds : OverlayMotionTokens.FasterMilliseconds);
            _content.StartAnimation(nameof(Visual.Scale), animation);
            _contentScaleAnimation = animation;
        }
        catch
        {
            DisposeAnimation(animation);
            throw;
        }
    }

    public void StopAll()
    {
        StopAnimation(_surface, nameof(Visual.Opacity), ref _surfaceOpacityAnimation);
        StopAnimation(_shadow, nameof(Visual.Opacity), ref _shadowOpacityAnimation);
        StopAnimation(_compact, nameof(Visual.Opacity), ref _compactOpacityAnimation);
        StopAnimation(_drag, nameof(Visual.Opacity), ref _dragOpacityAnimation);
        StopAnimation(_expanded, nameof(Visual.Opacity), ref _expandedOpacityAnimation);
        StopAnimation(_content, nameof(Visual.Offset), ref _contentOffsetAnimation);
        StopAnimation(_content, nameof(Visual.Scale), ref _contentScaleAnimation);
        StopAnimation(_interactionTint, nameof(Visual.Opacity), ref _interactionTintAnimation);
    }

    private void AnimateContent(
        ref CompositionAnimation? animationSlot,
        Visual visual,
        double current,
        double target,
        ContentTransitionProfile profile,
        bool reducedMotion)
    {
        if (target > current)
        {
            AnimateScalar(
                ref animationSlot,
                visual,
                "Opacity",
                current,
                target,
                reducedMotion ? OverlayMotionTokens.FasterMilliseconds : profile.IncomingDurationMilliseconds,
                reducedMotion ? 0 : profile.IncomingDelayMilliseconds);
        }
        else
        {
            AnimateScalar(
                ref animationSlot,
                visual,
                "Opacity",
                current,
                target,
                reducedMotion ? OverlayMotionTokens.FasterMilliseconds : profile.OutgoingDurationMilliseconds);
        }
    }

    private void AnimateContentOffset(
        bool incoming,
        ContentTransitionProfile profile,
        bool reducedMotion)
    {
        StopAnimation(_content, nameof(Visual.Offset), ref _contentOffsetAnimation);
        var duration = reducedMotion
            ? OverlayMotionTokens.FasterMilliseconds
            : incoming ? profile.IncomingDurationMilliseconds : profile.OutgoingDurationMilliseconds;
        var delay = reducedMotion || !incoming ? 0 : profile.IncomingDelayMilliseconds;
        var from = incoming
            ? _contentBaseOffset + new Vector3(0, (float)profile.IncomingOffsetDip, 0)
            : _contentBaseOffset;
        _content.Offset = from;
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        try
        {
            animation.InsertKeyFrame(0f, from);
            animation.InsertKeyFrame(1f, _contentBaseOffset);
            animation.Duration = TimeSpan.FromMilliseconds(Math.Max(1, duration));
            animation.DelayTime = TimeSpan.FromMilliseconds(Math.Max(0, delay));
            _content.StartAnimation(nameof(Visual.Offset), animation);
            _contentOffsetAnimation = animation;
        }
        catch
        {
            DisposeAnimation(animation);
            throw;
        }
    }

    private void AnimateScalar(
        ref CompositionAnimation? animationSlot,
        Visual visual,
        string property,
        double current,
        double target,
        double durationMilliseconds,
        double delayMilliseconds = 0)
    {
        StopAnimation(visual, property, ref animationSlot);
        visual.Opacity = (float)Math.Clamp(current, 0, 1);
        var animation = _compositor.CreateScalarKeyFrameAnimation();
        try
        {
            animation.InsertKeyFrame(0f, (float)Math.Clamp(current, 0, 1));
            animation.InsertKeyFrame(1f, (float)Math.Clamp(target, 0, 1));
            animation.Duration = TimeSpan.FromMilliseconds(Math.Max(1, durationMilliseconds));
            animation.DelayTime = TimeSpan.FromMilliseconds(Math.Max(0, delayMilliseconds));
            visual.StartAnimation(property, animation);
            animationSlot = animation;
        }
        catch
        {
            DisposeAnimation(animation);
            throw;
        }
    }

    private static void StopAnimation(
        Visual visual,
        string property,
        ref CompositionAnimation? animationSlot)
    {
        visual.StopAnimation(property);
        DisposeAnimation(animationSlot);
        animationSlot = null;
    }

    private static void DisposeAnimation(CompositionAnimation? animation)
    {
        if (animation is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopAll();
    }
}
