/*
 * 功能说明：加载嵌入费率缓存，并支持从 Shopee 定价模拟器 API 在线刷新最新运费与佣金。
 * 创建日期：2026-07-30
 * 修改记录：
 *   2026-08-14 迁入 EcommerceWorkbench，缓存目录改为独立路径
 *   2026-08-14 默认汇率改为 4.77
 */
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using EcommerceWorkbench.Models;

namespace EcommerceWorkbench.Services.Shopee;

public sealed class RateService
{
    private const string ApiBase = "https://solutions.shopee.cn/sellers/pricing-simulator/api/";
    private const string EmbeddedResourceName = "EcommerceWorkbench.Data.tw-711-rates.json";
    private const string TargetChannelName = "蝦皮海外 - 7-11";
    private const string TargetCargoName = "Normal";
    private const string TargetSiteName = "TW";
    private const string TargetZoneName = "所有地区";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public EmbeddedRateBundle Current { get; private set; }

    public string SourceDescription { get; private set; } = "嵌入缓存";

    public RateService()
    {
        Current = LoadEmbedded();
        SourceDescription = $"嵌入缓存（费率日期 {Current.Date}）";
    }

    public EmbeddedRateBundle LoadEmbedded()
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"找不到嵌入资源: {EmbeddedResourceName}");
        using var reader = new StreamReader(stream);
        var json = reader.ReadToEnd();
        return JsonSerializer.Deserialize<EmbeddedRateBundle>(json, JsonOptions)
            ?? throw new InvalidOperationException("嵌入费率数据解析失败");
    }

    public async Task<(bool ok, string message)> RefreshFromApiAsync(CancellationToken ct = default)
    {
        try
        {
            var siteList = await GetAsync<List<SiteInfo>>("site", ct);
            var twSite = siteList?.FirstOrDefault(s => s.Name == TargetSiteName)
                ?? throw new InvalidOperationException("接口未返回台湾站点信息");

            var config = await GetAsync<SiteConfigData>("sls/site-config", ct)
                ?? throw new InvalidOperationException("接口未返回站点运费配置");

            var tw = config.SiteInfo.FirstOrDefault(s => s.Name == TargetSiteName)
                ?? throw new InvalidOperationException("配置中未找到台湾站点");

            var cargo = tw.CargoTypes.FirstOrDefault(c => c.Name == TargetCargoName)
                ?? throw new InvalidOperationException("配置中未找到普货");

            var channel = cargo.Channels
                .Where(c => c.CnName == TargetChannelName || c.Name == TargetChannelName)
                .OrderBy(c => c.CnName.Length)
                .FirstOrDefault()
                ?? throw new InvalidOperationException($"未找到渠道: {TargetChannelName}");

            var zone = channel.Zones.FirstOrDefault(z => z.Name == TargetZoneName || z.CnName == TargetZoneName)
                ?? channel.Zones.FirstOrDefault()
                ?? throw new InvalidOperationException("未找到地区费率");

            Current = new EmbeddedRateBundle
            {
                Date = string.IsNullOrWhiteSpace(config.Date) ? DateTime.Today.ToString("yyyy-MM-dd") : config.Date,
                Site = twSite,
                Channel = new ChannelSnapshot
                {
                    SiteName = tw.Name,
                    SiteCn = tw.CnName,
                    Cargo = cargo.Name,
                    CargoCn = cargo.CnName,
                    Channel = channel.Name,
                    ChannelCn = channel.CnName,
                    Zone = zone.Name,
                    FeeModes = zone.FeeModes
                }
            };

            try
            {
                var cachePath = GetLocalCachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await File.WriteAllTextAsync(cachePath, JsonSerializer.Serialize(Current, JsonOptions), ct);
            }
            catch
            {
                // 本地缓存失败不影响主流程
            }

            SourceDescription = $"在线刷新（费率日期 {Current.Date}）";
            return (true, $"已刷新最新费率：{Current.Date}");
        }
        catch (Exception ex)
        {
            return (false, $"刷新失败，继续使用当前费率。原因：{ex.Message}");
        }
    }

    public bool TryLoadLocalCache()
    {
        try
        {
            var path = GetLocalCachePath();
            if (!File.Exists(path))
                return false;

            var json = File.ReadAllText(path);
            var bundle = JsonSerializer.Deserialize<EmbeddedRateBundle>(json, JsonOptions);
            if (bundle?.Channel?.FeeModes == null || bundle.Channel.FeeModes.Count == 0)
                return false;

            Current = bundle;
            SourceDescription = $"本地缓存（费率日期 {Current.Date}）";
            return true;
        }
        catch
        {
            return false;
        }
    }

    public PricingInput CreateDefaultInput()
    {
        var site = Current.Site;
        return new PricingInput
        {
            ExchangeRate = (double)UserSettings.DefaultExchangeRate,
            DiscountPercent = 30,
            WithinBorderSf = 1,
            ActivityScRatePercent = 1,
            WithdrawHfRatePercent = 1,
            CommissionRatePercent = ParseRate(site.PlatformCommission, 14),
            TradeHfRatePercent = ParseRate(site.HandlingFee, 2.5)
        };
    }

    private async Task<T?> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(ApiBase + relativeUrl, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var wrapper = await JsonSerializer.DeserializeAsync<ApiResponse<T>>(stream, JsonOptions, ct);
        if (wrapper == null)
            throw new InvalidOperationException("空响应");

        if (!(wrapper.Code == 0 || (wrapper.Code >= 200000 && wrapper.Code <= 300000)))
            throw new InvalidOperationException(wrapper.Msg ?? $"接口错误码 {wrapper.Code}");

        return wrapper.Data;
    }

    private static string GetLocalCachePath() => Path.Combine(AppPaths.DataDirectory, "tw-711-rates.json");

    private static double ParseRate(string? text, double fallback)
        => double.TryParse(text, out var v) ? v : fallback;
}
