/*
 * 功能说明：为 WPF ScrollViewer 提供可连续叠加的垂直缓动滚动。
 * 主要职责：把滚轮刻度转换成短距离动画，并在连续滚动时平滑更新目标位置。
 * 创建日期：2026-10-02
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace EcommerceWorkbench.UI;

public static class SmoothScrollHelper
{
    private const double WheelFactor = 0.62;
    private static readonly Dictionary<ScrollViewer, ScrollState> States = [];

    private static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedOffset",
            typeof(double),
            typeof(SmoothScrollHelper),
            new PropertyMetadata(0d, OnAnimatedOffsetChanged));

    public static bool CanScroll(ScrollViewer viewer, int wheelDelta)
    {
        var position = States.TryGetValue(viewer, out var state) && state.Active
            ? state.Target
            : viewer.VerticalOffset;
        const double epsilon = 0.5;
        return wheelDelta > 0
            ? position > epsilon
            : position < viewer.ScrollableHeight - epsilon;
    }

    public static void ScrollWheel(ScrollViewer viewer, int wheelDelta)
    {
        if (!TransitionHelper.AnimationsEnabled)
        {
            viewer.ScrollToVerticalOffset(viewer.VerticalOffset - wheelDelta);
            return;
        }

        if (!States.TryGetValue(viewer, out var state))
        {
            state = new ScrollState();
            States[viewer] = state;
        }

        var current = viewer.VerticalOffset;
        if (!state.Active)
            state.Target = current;

        state.Target = Math.Clamp(
            state.Target - wheelDelta * WheelFactor,
            0,
            viewer.ScrollableHeight);
        state.Version++;
        var version = state.Version;
        state.Active = true;

        viewer.BeginAnimation(AnimatedOffsetProperty, null);
        viewer.SetValue(AnimatedOffsetProperty, current);

        var animation = new DoubleAnimation
        {
            From = current,
            To = state.Target,
            Duration = TimeSpan.FromMilliseconds(155),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            if (version != state.Version)
                return;
            viewer.BeginAnimation(AnimatedOffsetProperty, null);
            viewer.ScrollToVerticalOffset(state.Target);
            state.Active = false;
        };
        viewer.BeginAnimation(AnimatedOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static void OnAnimatedOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer viewer && e.NewValue is double offset)
            viewer.ScrollToVerticalOffset(offset);
    }

    private sealed class ScrollState
    {
        public double Target { get; set; }
        public int Version { get; set; }
        public bool Active { get; set; }
    }
}
