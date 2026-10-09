/*
 * 功能说明：JOOM / 速卖通7 标签页的滚轮步长与滚动应用。
 * 主要职责：把滚轮刻度换算成容器自身单位的步长并立即生效；按行滚动的容器对齐到整行。
 * 创建日期：2026-10-02
 * 修改记录：
 *   2026-10-09 由「逐帧缓动」改为立即滚动。实测依据（200 行 x 20 列虚拟化表格）：
 *              逐帧设置偏移量会让虚表每帧重新测量/生成行，帧间隔中位数 11ms → 22ms，
 *              并出现 16 次 >25ms 的掉帧，肉眼即为顿挫；不做中间帧动画时只有 1 次。
 *              文字不做滑动也就不会眼花。同时表格从像素滚动改回按行滚动（对齐整行、代价最低）。
 */

using System.Windows;
using System.Windows.Controls;

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
        const double epsilon = 0.5;
        return wheelDelta > 0
            ? viewer.VerticalOffset > epsilon
            : viewer.VerticalOffset < viewer.ScrollableHeight - epsilon;
    }

    /// <summary>
    /// 立即滚动一格。<paramref name="step"/> 使用容器自身单位：
    /// 按行滚动（<see cref="ScrollViewer.CanContentScroll"/> 为 true）时是行数，否则是像素。
    /// </summary>
    public static void ScrollWheel(ScrollViewer viewer, int wheelDelta, double step)
    {
        if (wheelDelta == 0 || step <= 0)
            return;

        var next = viewer.VerticalOffset + (wheelDelta > 0 ? -step : step);

        // 按行滚动时对齐整行，避免停在半行上导致文字发虚、下一格错位。
        if (viewer.CanContentScroll)
            next = Math.Round(next);

        viewer.ScrollToVerticalOffset(Math.Clamp(next, 0, Math.Max(0, viewer.ScrollableHeight)));
    }
}
