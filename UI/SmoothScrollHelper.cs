/*
 * 功能说明：为 WPF ScrollViewer 提供连续的垂直缓动滚动。
 * 主要职责：把滚轮刻度累加为目标位置，逐帧按时间常数平滑逼近；连续滚轮不打断已有滑动。
 * 创建日期：2026-10-02
 * 修改记录：
 *   2026-10-09 改为逐帧缓动。原实现每格都用 FillBehavior.Stop 重起动画，
 *              动画结束时会先回落到基准值再由回调跳到目标，产生可见回弹与顿挫；
 *              每格距离改由 SystemParameters.WheelScrollLines 换算，并限制单帧最大位移，
 *              避免连续滚轮时画面飞掠；静止后清理状态，避免静态字典随控件累积
 *   2026-10-09 手感调慢：每格封顶 2 行、时间常数 75ms→110ms、单帧限速 40→24px
 */

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EcommerceWorkbench.UI;

public static class SmoothScrollHelper
{
    /// <summary>逼近目标的时间常数（秒）。越小越跟手，越大越柔和。</summary>
    private const double TimeConstantSeconds = 0.11;

    /// <summary>单帧最大位移（像素）。限制最高速度，避免连续滚轮时文字飞掠导致看不清。</summary>
    private const double MaxPixelsPerFrame = 24;

    /// <summary>
    /// 每格滚轮最多滚动的行数。系统默认 3 行，本工作台表格行高、文字密集，
    /// 一格 3 行会跟不住阅读位置；这里封顶为 2 行，用户若把系统设得更少则尊重其设置。
    /// </summary>
    private const int MaxLinesPerNotch = 2;

    private const double SnapEpsilon = 0.5;
    private const double DefaultFrameSeconds = 1.0 / 60.0;

    /// <summary>单次滑动的最大帧数（约 3 秒 @60fps）看门狗，避免异常情况下每帧空转。</summary>
    private const int MaxFramesPerScroll = 180;

    private static readonly Dictionary<ScrollViewer, ScrollState> States = [];
    private static bool _renderingHooked;
    private static TimeSpan _lastFrameTime;

    /// <summary>
    /// 把系统设置里「每次滚动的行数」换算成像素，并按 <see cref="MaxLinesPerNotch"/> 封顶；
    /// 值为 0 或 -1（翻页）时按 Windows 默认的 3 行处理。
    /// </summary>
    public static double ResolveWheelStep(double unitPixels)
    {
        var lines = SystemParameters.WheelScrollLines;
        if (lines <= 0)
            lines = 3;

        lines = Math.Min(lines, MaxLinesPerNotch);

        var unit = double.IsFinite(unitPixels) && unitPixels > 0 ? unitPixels : 20;
        return unit * lines;
    }

    /// <summary>该方向上是否还有可滚动余量。滑动进行中按目标位置判断，避免边界处反复抢滚轮。</summary>
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

    /// <summary>
    /// 按滚轮刻度推进 <paramref name="viewer"/>，<paramref name="stepPixels"/> 为一格的距离。
    /// </summary>
    public static void ScrollWheel(ScrollViewer viewer, int wheelDelta, double stepPixels)
    {
        if (wheelDelta == 0)
            return;

        var step = wheelDelta > 0 ? -Math.Abs(stepPixels) : Math.Abs(stepPixels);

        if (!TransitionHelper.AnimationsEnabled)
        {
            viewer.ScrollToVerticalOffset(ClampOffset(viewer.VerticalOffset + step, viewer));
            return;
        }

        if (!States.TryGetValue(viewer, out var state))
        {
            state = new ScrollState();
            States[viewer] = state;
        }

        // 滑动中继续累加目标，而不是从当前动画位置重新开始，保证连续滚轮的匀速手感。
        if (!state.Active)
        {
            state.Target = viewer.VerticalOffset;
            state.Frames = 0;
        }

        state.Target = ClampOffset(state.Target + step, viewer);
        state.Active = true;
        HookRendering();
    }

    private static double ClampOffset(double offset, ScrollViewer viewer)
        => Math.Clamp(offset, 0, Math.Max(0, viewer.ScrollableHeight));

    private static void HookRendering()
    {
        if (_renderingHooked)
            return;

        _lastFrameTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    private static void UnhookRendering()
    {
        if (!_renderingHooked)
            return;

        CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var dt = DefaultFrameSeconds;
        if (e is RenderingEventArgs args)
        {
            if (_lastFrameTime != TimeSpan.Zero && args.RenderingTime > _lastFrameTime)
                dt = (args.RenderingTime - _lastFrameTime).TotalSeconds;
            _lastFrameTime = args.RenderingTime;
        }

        // 断点或卡顿后不要一帧跳很远。
        if (dt <= 0 || dt > 0.1)
            dt = DefaultFrameSeconds;

        // 与帧率无关的指数逼近系数。
        var alpha = 1 - Math.Exp(-dt / TimeConstantSeconds);
        var anyActive = false;

        foreach (var pair in States.ToArray())
        {
            var viewer = pair.Key;
            var state = pair.Value;
            if (!state.Active)
                continue;

            // 行数变化（增删行）会让可滚动高度收缩，目标随之收敛，避免长期追不到目标。
            state.Target = ClampOffset(state.Target, viewer);

            var current = viewer.VerticalOffset;
            var remaining = state.Target - current;

            // 看门狗：即使写入被容器钳制而始终无法到达目标，也要在约 3 秒内收手，不能每帧空转。
            if (Math.Abs(remaining) <= SnapEpsilon || ++state.Frames > MaxFramesPerScroll)
            {
                viewer.ScrollToVerticalOffset(state.Target);
                state.Active = false;
                continue;
            }

            var delta = remaining * alpha;
            if (Math.Abs(delta) > MaxPixelsPerFrame)
                delta = Math.Sign(delta) * MaxPixelsPerFrame;

            viewer.ScrollToVerticalOffset(current + delta);
            anyActive = true;
        }

        if (anyActive)
            return;

        UnhookRendering();

        // 静止后移除已停止的状态，避免静态字典随控件长期累积。
        foreach (var pair in States.ToArray())
        {
            if (!pair.Value.Active)
                States.Remove(pair.Key);
        }
    }

    private sealed class ScrollState
    {
        public double Target { get; set; }
        public bool Active { get; set; }
        public int Frames { get; set; }
    }
}
