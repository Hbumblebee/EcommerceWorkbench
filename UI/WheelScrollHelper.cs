/*
 * 功能说明：JOOM / 速卖通7 标签页的滚轮步长与滚动应用。
 * 主要职责：按滚轮实际刻度累计目标位置；文字区平移已布局内容并在结束时对齐设备像素，
 *           虚拟化表格只预览已生成的行，滑完再提交一次偏移，避免逐帧重测。
 * 创建日期：2026-10-02
 * 修改记录：
 *   2026-10-09 由「逐帧缓动」改为立即滚动。实测依据（200 行 x 20 列虚拟化表格）：
 *              逐帧设置偏移量会让虚表每帧重新测量/生成行，帧间隔中位数 11ms → 22ms，
 *              并出现 16 次 >25ms 的掉帧，肉眼即为顿挫；不做中间帧动画时只有 1 次。
 *              文字不做滑动也就不会眼花。同时表格从像素滚动改回按行滚动（对齐整行、代价最低）。
 *   2026-10-09 立即跳格在字多时仍然眼花。改为位移预览：滑动中不逐帧改偏移，
 *              文字区结束时对齐设备像素；表格一次最多预览 4 行，段末才提交偏移。
 *              滚轮按 Delta/120 换算，高精度鼠标的一小格不再被当成一整格。
 */

using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace EcommerceWorkbench.UI;

/// <summary>
/// 滚轮步长与滚动应用。
/// </summary>
public static class WheelScrollHelper
{
    /// <summary>每格最多滚动的行数。系统默认 3 行，本工作台表格行高、文字密集，封顶 2 行。</summary>
    private const int MaxLinesPerNotch = 2;

    /// <summary>文字区一行文字的估算高度（像素）。</summary>
    public const double TextLinePixels = 20;

    /// <summary>Windows 约定的一格滚轮刻度。</summary>
    private const double WheelNotch = 120;

    /// <summary>位移跟手的时间常数（秒）。越小越利落，越大越拖。</summary>
    private const double SmoothSeconds = 0.06;

    /// <summary>虚拟化表格一次最多预览的行数，留在缓存区内，避免滑出空白。</summary>
    private const double MaxLogicalLeadRows = 4;

    private const double OffsetEpsilon = 0.5;

    private static readonly Dictionary<ScrollViewer, Motion> Motions = new();
    private static bool _renderingHooked;

    /// <summary>每格滚动的行数：尊重系统设置，但不超过上限。</summary>
    public static int LinesPerNotch()
    {
        var lines = SystemParameters.WheelScrollLines;
        if (lines <= 0)
            lines = 3;

        return Math.Min(lines, MaxLinesPerNotch);
    }

    /// <summary>该方向上是否还有可滚动余量，用于决定滚轮该交给内层还是外层容器。</summary>
    public static bool CanScroll(ScrollViewer viewer, int wheelDelta)
    {
        var offset = viewer.VerticalOffset;
        if (Motions.TryGetValue(viewer, out var motion) && motion.Active)
            offset = motion.Target;

        return wheelDelta > 0
            ? offset > OffsetEpsilon
            : offset < MaxOffset(viewer) - OffsetEpsilon;
    }

    /// <summary>
    /// 滚动一格。<paramref name="step"/> 使用容器自身单位：
    /// 按行滚动（<see cref="ScrollViewer.CanContentScroll"/> 为 true）时是行数，否则是像素。
    /// 实际距离再乘以本次滚轮刻度（120 为一整格）。
    /// </summary>
    public static void ScrollWheel(ScrollViewer viewer, int wheelDelta, double step)
    {
        if (wheelDelta == 0 || step <= 0 || !IsFinite(viewer.VerticalOffset))
            return;

        var delta = -wheelDelta / WheelNotch * step;
        if (!IsFinite(delta) || Math.Abs(delta) < 0.0001)
            return;

        if (!AnimationsEnabled())
        {
            Stop(viewer);
            ApplyImmediate(viewer, delta);
            return;
        }

        var motion = GetMotion(viewer);
        if (viewer.CanContentScroll)
        {
            if (!EnsureRows(viewer, motion))
            {
                ApplyImmediate(viewer, delta);
                return;
            }

            motion.Preview = true;
            motion.Logical = true;
        }
        else
        {
            motion.Logical = false;
            motion.Preview = TryAttachPlainPreview(viewer, motion);
        }

        if (!motion.Active)
        {
            motion.BaseOffset = motion.Logical
                ? Math.Round(viewer.VerticalOffset, MidpointRounding.AwayFromZero)
                : viewer.VerticalOffset;
            motion.Target = motion.BaseOffset;
            if (motion.Preview && motion.Shift is not null)
                motion.Shift.Y = 0;
        }

        var max = MaxOffset(viewer);
        if (motion.Logical)
        {
            motion.Pending += delta;
            var whole = (int)Math.Truncate(motion.Pending);
            if (whole == 0)
                return;

            motion.Pending -= whole;
            var next = Math.Clamp(motion.Target + whole, 0, max);
            if (Math.Abs(next - motion.Target) < 0.001)
            {
                motion.Pending = 0;
                return;
            }

            if (Math.Abs(motion.Target + whole - next) > 0.001)
                motion.Pending = 0;

            motion.Target = next;
        }
        else
        {
            var next = Math.Clamp(motion.Target + delta, 0, max);
            if (!motion.Active && Math.Abs(next - viewer.VerticalOffset) < 0.05)
                return;

            motion.Target = next;
        }

        if (!motion.Active)
            motion.LastTicks = 0;

        motion.Active = true;
        EnsureRendering();
    }

    private static bool AnimationsEnabled() =>
        SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    private static void ApplyImmediate(ScrollViewer viewer, double delta)
    {
        var next = viewer.VerticalOffset + delta;
        next = viewer.CanContentScroll
            ? Math.Round(next, MidpointRounding.AwayFromZero)
            : SnapDip(next, viewer);

        viewer.ScrollToVerticalOffset(Math.Clamp(next, 0, MaxOffset(viewer)));
    }

    private static Motion GetMotion(ScrollViewer viewer)
    {
        if (Motions.TryGetValue(viewer, out var motion))
            return motion;

        motion = new Motion();
        Motions[viewer] = motion;
        viewer.ScrollChanged += OnViewerScrollChanged;
        viewer.Unloaded += OnViewerUnloaded;
        return motion;
    }

    private static bool EnsureRows(ScrollViewer viewer, Motion motion)
    {
        if (motion.Host is DataGridRowsPresenter existing
            && existing.IsDescendantOf(viewer)
            && motion.RowPx > 1
            && motion.Shift is not null)
            return true;

        var presenter = FindDescendant<DataGridRowsPresenter>(viewer);
        if (presenter is null)
            return false;

        var rowPx = MeasureRow(presenter);
        if (rowPx <= 1)
            return false;

        motion.RowPx = rowPx;
        motion.Host = presenter;
        motion.Shift = EnsureShift(presenter);
        return true;
    }

    /// <summary>
    /// 整页和重复区的内容已经全部布局，可以整段平移。
    /// 诊断列表是虚拟化的，不走这条路径。
    /// </summary>
    private static bool TryAttachPlainPreview(ScrollViewer viewer, Motion motion)
    {
        if (viewer.Content is not FrameworkElement content)
            return false;
        if (content is not Panel && content is not ItemsControl)
            return false;

        if (motion.Host == content && motion.Shift is not null)
            return true;

        motion.RowPx = 1;
        motion.Host = content;
        motion.Shift = EnsureShift(content);
        return true;
    }

    private static TranslateTransform EnsureShift(FrameworkElement host)
    {
        if (host.RenderTransform is TranslateTransform shift && !shift.IsFrozen)
            return shift;

        shift = new TranslateTransform();
        host.RenderTransform = shift;
        return shift;
    }

    private static double MeasureRow(DataGridRowsPresenter presenter)
    {
        DataGridRow? first = null;
        var count = VisualTreeHelper.GetChildrenCount(presenter);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(presenter, i) is not DataGridRow row || row.ActualHeight <= 1)
                continue;

            if (first is null)
            {
                first = row;
                continue;
            }

            var dy = Math.Abs(row.TranslatePoint(new Point(0, 0), first).Y);
            if (dy > 1)
                return dy;
        }

        if (first is not null)
            return first.ActualHeight;

        for (DependencyObject? current = presenter; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is DataGrid grid && !double.IsNaN(grid.RowHeight) && grid.RowHeight > 1)
                return grid.RowHeight;
        }

        return 0;
    }

    private static void EnsureRendering()
    {
        if (_renderingHooked)
            return;

        _renderingHooked = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private static void OnRendering(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        foreach (var pair in Motions.ToArray())
        {
            var viewer = pair.Key;
            var motion = pair.Value;
            if (!motion.Active)
                continue;

            if (!viewer.IsVisible)
            {
                Settle(viewer, motion);
                continue;
            }

            var dt = motion.LastTicks == 0
                ? 1.0 / 60
                : Math.Clamp((now - motion.LastTicks) / (double)Stopwatch.Frequency, 0.001, 0.05);
            motion.LastTicks = now;

            if (motion.Preview)
                AdvancePreview(viewer, motion, dt);
            else
                AdvancePixels(viewer, motion, dt);
        }

        if (Motions.Values.All(item => !item.Active))
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }

    /// <summary>诊断列表等小区域：直接移动偏移，每帧对齐设备像素。</summary>
    private static void AdvancePixels(ScrollViewer viewer, Motion motion, double dt)
    {
        var destination = Math.Clamp(SnapDip(motion.Target, viewer), 0, MaxOffset(viewer));
        var current = viewer.VerticalOffset;
        var unit = DeviceUnit(viewer);
        var next = MoveToward(current, destination, dt, unit);
        if (Math.Abs(destination - next) <= unit * 0.5)
            next = destination;

        if (Math.Abs(next - current) < 0.001)
        {
            if (Math.Abs(destination - current) <= unit)
            {
                motion.Active = false;
                motion.BaseOffset = current;
            }

            return;
        }

        motion.Expecting = true;
        motion.ExpectedOffset = next;
        viewer.ScrollToVerticalOffset(next);
    }

    /// <summary>
    /// 平移已经布局好的内容。文字区一次滑完全程；表格一次最多 4 行，
    /// 到段末再改滚动偏移，避免虚表每帧重新生成行。
    /// </summary>
    private static void AdvancePreview(ScrollViewer viewer, Motion motion, double dt)
    {
        if (motion.Shift is null || motion.Host is null)
            return;

        if (motion.CommitPending)
        {
            if (Math.Abs(viewer.VerticalOffset - motion.ExpectedOffset) <= 0.75)
                CompletePreview(viewer, motion);
            else if (++motion.CommitWait > 2)
                motion.CommitPending = false;

            if (motion.CommitPending || !motion.Active)
                return;
        }

        motion.CommitWait = 0;

        var destination = PreviewDestination(motion, viewer, out var partial);
        var unit = motion.Logical ? motion.RowPx : 1;
        if (unit <= 1 && motion.Logical)
            return;

        var exactShift = -(destination - motion.BaseOffset) * unit;
        var shown = MoveToward(motion.Shift.Y, exactShift, dt, DeviceUnit(motion.Host));
        if (Math.Abs(exactShift - shown) <= DeviceUnit(motion.Host) * 0.5)
            shown = exactShift;

        motion.Shift.Y = shown;
        if (Math.Abs(shown - exactShift) > 0.05)
            return;

        motion.ContinueAfterCommit = partial;
        motion.CommitPending = true;
        motion.Expecting = true;
        motion.ExpectedOffset = destination;
        if (Math.Abs(viewer.VerticalOffset - destination) <= 0.01)
        {
            CompletePreview(viewer, motion);
            return;
        }

        viewer.ScrollToVerticalOffset(destination);
    }

    private static double PreviewDestination(Motion motion, ScrollViewer viewer, out bool partial)
    {
        var max = MaxOffset(viewer);
        if (!motion.Logical)
        {
            partial = false;
            // 对齐到滚动条自己的屏幕位置。内容正在被平移，不能拿它做参照，否则目标会跟着抖。
            return Math.Clamp(SnapDip(motion.Target, viewer), 0, max);
        }

        var target = Math.Clamp(Math.Round(motion.Target, MidpointRounding.AwayFromZero), 0, max);
        var lead = target - motion.BaseOffset;
        partial = Math.Abs(lead) > MaxLogicalLeadRows + 0.01;
        if (!partial)
            return target;

        var destination = motion.BaseOffset + Math.Sign(lead) * MaxLogicalLeadRows;
        return Math.Clamp(Math.Round(destination, MidpointRounding.AwayFromZero), 0, max);
    }

    private static double MoveToward(double current, double desired, double dt, double unit)
    {
        if (unit <= 0)
            unit = 1;

        var delta = desired - current;
        if (Math.Abs(delta) <= 0.001)
            return desired;

        var step = delta * (1 - Math.Exp(-dt / SmoothSeconds));
        if (Math.Abs(step) < unit && Math.Abs(delta) >= unit * 0.5)
            step = Math.Sign(delta) * unit;
        if (Math.Abs(step) > Math.Abs(delta))
            return desired;

        var next = Math.Round((current + step) / unit) * unit;
        if (Math.Sign(delta) != 0 && Math.Sign(delta) != Math.Sign(desired - next))
            return desired;

        return next;
    }

    private static void OnViewerScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !Motions.TryGetValue(viewer, out var motion))
            return;

        // ScrollChanged 会向上冒泡。外层页的监听不能把内层表格的滚动当成自己的拖动。
        if (!ReferenceEquals(e.OriginalSource, viewer))
            return;

        if (Math.Abs(e.VerticalChange) < 0.001)
            return;

        if (motion.Expecting && Math.Abs(viewer.VerticalOffset - motion.ExpectedOffset) <= 0.75)
        {
            motion.Expecting = false;
            if (motion.Preview)
                CompletePreview(viewer, motion);
            return;
        }

        if (motion.Shift is not null)
            motion.Shift.Y = 0;

        motion.BaseOffset = viewer.VerticalOffset;
        motion.Target = motion.BaseOffset;
        motion.Pending = 0;
        motion.Active = false;
        motion.Expecting = false;
        motion.CommitPending = false;
        motion.ContinueAfterCommit = false;
    }

    private static void CompletePreview(ScrollViewer viewer, Motion motion)
    {
        if (motion.Shift is not null)
            motion.Shift.Y = 0;

        motion.BaseOffset = viewer.VerticalOffset;
        motion.Expecting = false;
        motion.CommitPending = false;
        motion.CommitWait = 0;
        var cont = motion.ContinueAfterCommit;
        motion.ContinueAfterCommit = false;

        var snapped = motion.Logical
            ? Math.Clamp(Math.Round(motion.Target, MidpointRounding.AwayFromZero), 0, MaxOffset(viewer))
            : Math.Clamp(SnapDip(motion.Target, viewer), 0, MaxOffset(viewer));
        var slack = motion.Logical ? 0.01 : DeviceUnit(viewer);
        motion.Active = cont || Math.Abs(snapped - motion.BaseOffset) > slack;
        if (!motion.Active)
            motion.LastTicks = 0;
        else
            EnsureRendering();
    }

    private static void Settle(ScrollViewer viewer, Motion motion)
    {
        var destination = motion.Logical
            ? Math.Clamp(Math.Round(motion.Target, MidpointRounding.AwayFromZero), 0, MaxOffset(viewer))
            : Math.Clamp(SnapDip(motion.Target, viewer), 0, MaxOffset(viewer));

        if (motion.Shift is not null)
            motion.Shift.Y = 0;

        motion.Active = false;
        motion.Expecting = false;
        motion.CommitPending = false;
        motion.ContinueAfterCommit = false;
        motion.Pending = 0;
        motion.BaseOffset = destination;
        motion.Target = destination;
        if (Math.Abs(viewer.VerticalOffset - destination) > 0.01)
            viewer.ScrollToVerticalOffset(destination);
    }

    private static void Stop(ScrollViewer viewer)
    {
        if (!Motions.TryGetValue(viewer, out var motion))
            return;

        if (motion.Shift is not null)
            motion.Shift.Y = 0;

        motion.Active = false;
        motion.Pending = 0;
        motion.CommitPending = false;
        motion.Expecting = false;
        motion.ContinueAfterCommit = false;
    }

    private static void OnViewerUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || !Motions.Remove(viewer, out var motion))
            return;

        if (motion.Shift is not null)
            motion.Shift.Y = 0;

        viewer.ScrollChanged -= OnViewerScrollChanged;
        viewer.Unloaded -= OnViewerUnloaded;
    }

    private static double MaxOffset(ScrollViewer viewer)
    {
        var max = viewer.ScrollableHeight;
        return double.IsFinite(max) && max > 0 ? max : 0;
    }

    private static double DeviceUnit(Visual visual)
    {
        var scale = SafeScale(visual);
        return scale > 0 ? 1.0 / scale : 1;
    }

    /// <summary>把 DIP 位置对齐到设备像素，避免文字停在半像素上发虚。</summary>
    private static double SnapDip(double dip, Visual visual)
    {
        try
        {
            var scale = SafeScale(visual);
            var screenY = visual.PointToScreen(new Point(0, 0)).Y;
            var physical = screenY + dip * scale;
            return (Math.Round(physical) - screenY) / scale;
        }
        catch (InvalidOperationException)
        {
            return Math.Round(dip);
        }
    }

    private static double SafeScale(Visual visual)
    {
        var scale = VisualTreeHelper.GetDpi(visual).PixelsPerDip;
        return scale > 0 ? scale : 1;
    }

    private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(parent);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;

            var nested = FindDescendant<T>(child);
            if (nested is not null)
                return nested;
        }

        return null;
    }

    private sealed class Motion
    {
        public bool Preview;
        public bool Logical;
        public bool Active;
        public bool Expecting;
        public bool CommitPending;
        public int CommitWait;
        public bool ContinueAfterCommit;
        public double RowPx = 1;
        public double BaseOffset;
        public double Target;
        public double Pending;
        public double ExpectedOffset;
        public long LastTicks;
        public FrameworkElement? Host;
        public TranslateTransform? Shift;
    }
}
