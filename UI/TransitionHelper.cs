/*
 * 功能说明：为工作台页面和状态提示提供尊重系统设置的轻量过渡动画。
 * 主要职责：统一动画开关、页面入场和状态提示反馈；不对 WebView2 或表格行执行动画。
 * 创建日期：2026-10-02
 */

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace EcommerceWorkbench.UI;

public static class TransitionHelper
{
    public static bool AnimationsEnabled =>
        SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;

    public static void Reveal(FrameworkElement element)
    {
        if (!AnimationsEnabled)
        {
            element.Opacity = 1;
            element.RenderTransform = Transform.Identity;
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = 0;
        var translate = element.RenderTransform as TranslateTransform ?? new TranslateTransform();
        element.RenderTransform = translate;
        translate.Y = 8;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        translate.BeginAnimation(
            TranslateTransform.YProperty,
            new DoubleAnimation(8, 0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
    }

    public static void Acknowledge(FrameworkElement element)
    {
        if (!AnimationsEnabled)
            return;

        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.BeginAnimation(
            UIElement.OpacityProperty,
            new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(220),
                KeyFrames =
                {
                    new DiscreteDoubleKeyFrame(0.55, KeyTime.FromPercent(0)),
                    new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1),
                        new CubicEase { EasingMode = EasingMode.EaseOut })
                }
            });
    }
}
