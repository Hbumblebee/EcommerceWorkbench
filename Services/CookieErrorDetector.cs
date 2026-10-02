/*
 * 功能说明：Cookie 失效/未登录的统一判定。
 * 主要职责：收敛原来分散在搜索页、JOOM、速卖通7 与 JoomProductService 的四份逐字副本，
 *           避免规则不同步；判定命中后由调用方触发重新登录流程。
 * 创建日期：2026-10-03
 */

namespace EcommerceWorkbench.Services;

/// <summary>
/// 从接口/页面错误文案判断是否为「未登录 / Cookie 失效」。
/// </summary>
public static class CookieErrorDetector
{
    public static bool IsAuthFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("code=2001", StringComparison.OrdinalIgnoreCase)
               || message.Contains("验证失败", StringComparison.OrdinalIgnoreCase)
               || message.Contains("未登录", StringComparison.OrdinalIgnoreCase)
               || message.Contains("请先登录", StringComparison.OrdinalIgnoreCase)
               || message.Contains("登录已失效", StringComparison.OrdinalIgnoreCase)
               || (message.Contains("登录", StringComparison.OrdinalIgnoreCase)
                   && message.Contains("失效", StringComparison.OrdinalIgnoreCase));
    }
}
