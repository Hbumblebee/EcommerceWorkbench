/*
 * 功能说明：使用已导出 Cookie 发起短期 HTTP 请求。
 * 主要职责：带 Cookie 调用店小秘等站点接口并返回响应文本。
 * 创建日期：2026-07-23
 * 修改记录：2026-08-14 迁入 EcommerceWorkbench
 */

using System.Net.Http;
using System.Text;

namespace EcommerceWorkbench.Services.Dianxiaomi;

/// <summary>
/// 带 Cookie 的简易 HTTP 客户端。
/// </summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _httpClient = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(60)
    };

    /// <summary>
    /// 发送请求并返回状态码与响应体。
    /// </summary>
    public async Task<(int StatusCode, string Body)> SendAsync(
        string method,
        string url,
        string? cookieHeader,
        string? body,
        string contentType,
        CancellationToken cancellationToken = default,
        string? referer = null)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        request.Headers.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.dianxiaomi.com");
        request.Headers.TryAddWithoutValidation(
            "Referer",
            string.IsNullOrWhiteSpace(referer)
                ? "https://www.dianxiaomi.com/web/dxmCommodityProduct/index"
                : referer);

        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase)
            && body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, contentType);
        }

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        return ((int)response.StatusCode, responseBody);
    }

    public void Dispose() => _httpClient.Dispose();
}
