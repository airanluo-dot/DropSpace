using System.Numerics;
using DropSpace.Core.Overlay;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace DropSpace.App.Services;

/// <summary>
/// Keeps opacity, content choreography, hover tint, and press feedback on compositor visuals
/// without allocating transient animations. Native geometry remains owned by OverlayWindow so the
/// OLE hit region stays exact and fail-closed.
/// </summary>
internal sealed class OverlayCompositionAnimator : IDisposable
{
    private readonly Visual _surface;
    private readonly Visual _compact;
    private readonly Visual _drag;
    private readonly Visual _expanded;
    private readonly Visual _content;
    private readonly Visual _interactionTint;
    private bool _reducedMotion;
    private double _contentIncomingOffsetDip;
    private float _hoverOpacity;
    private float _pressScale = 1;
    private bool _disposed;

    public OverlayCompositionAnimator(
        FrameworkElement surface,
        FrameworkElement compact,
        FrameworkElement drag,
        FrameworkElement expanded,
        FrameworkElement content,
        FrameworkElement interactionTint)
    {
        _surface = ElementCompositionPreview.GetElementVisual(surface);
        _compact = ElementCompositionPreview.GetElementVisual(compact);
        _drag = ElementCompositionPreview.GetElementVisual(drag);
        _expanded = ElementCompositionPreview.GetElementVisual(expanded);
        _content = ElementCompositionPreview.GetElementVisual(content);
        _interactionTint = ElementCompositionPreview.GetElementVisual(interactionTint);
        ElementCompositionPreview.SetIsTranslationEnabled(compact, true);
        ElementCompositionPreview.SetIsTranslationEnabled(drag, true);
        ElementCompositionPreview.SetIsTranslationEnabled(expanded, true);
    }

    public void AnimateTo(
        OverlayMotionValues current,
        OverlayMotionValues target,
        ContentTransitionProfile profile,
        bool reducedMotion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _reducedMotion = reducedMotion;
        _contentIncomingOffsetDip = profile.IncomingOffsetDip;
    }

    public void ApplyMotion(OverlayMotionValues values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _surface.Opacity = (float)Math.Clamp(values.Opacity, 0, 1);
        _compact.Opacity = (float)Math.Clamp(values.CompactContent, 0, 1);
        _drag.Opacity = (float)Math.Clamp(values.DragContent, 0, 1);
        _expanded.Opacity = (float)Math.Clamp(values.ExpandedContent, 0, 1);
        ApplyContentPose(_compact, values.CompactContent);
        ApplyContentPose(_drag, values.DragContent);
        ApplyContentPose(_expanded, values.ExpandedContent);
        _content.Scale = new Vector3(_pressScale, _pressScale, 1);
        _interactionTint.Opacity = _hoverOpacity;
    }

    private void ApplyContentPose(Visual visual, double progress)
    {
        var pose = OverlayContentPose.FromProgress(progress, _reducedMotion, _contentIncomingOffsetDip);
        visual.CenterPoint = new Vector3(visual.Size.X / 2, 0, 0);
        visual.Properties.InsertVector3("Translation", new Vector3(0, (float)pose.OffsetY, 0));
        visual.Scale = new Vector3((float)pose.Scale, (float)pose.Scale, 1);
    }

    public void SnapTo(OverlayMotionValues values)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _contentIncomingOffsetDip = 0;
        _hoverOpacity = 0;
        _pressScale = 1;
        ApplyMotion(values);
    }

    public void ApplyHover(bool entered, bool reducedMotion)
    {
        _hoverOpacity = entered ? 0.08f : 0;
        _interactionTint.Opacity = _hoverOpacity;
    }

    public void ApplyPress(bool pressed, bool reducedMotion)
    {
        _pressScale = pressed && !reducedMotion ? (float)OverlayMotionTokens.PressScale : 1;
        _content.Scale = new Vector3(_pressScale, _pressScale, 1);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
