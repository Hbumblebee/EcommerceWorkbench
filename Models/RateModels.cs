/*
 * 功能说明：运费费率与站点配置的数据模型，对应定价模拟器 API 结构。
 * 创建日期：2026-07-30
 * 修改记录：2026-08-14 迁入 EcommerceWorkbench
 */
using System.Text.Json.Serialization;

namespace EcommerceWorkbench.Models;

public sealed class EmbeddedRateBundle
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("site")]
    public SiteInfo Site { get; set; } = new();

    [JsonPropertyName("channel")]
    public ChannelSnapshot Channel { get; set; } = new();
}

public sealed class SiteInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("currency_symbol")]
    public string CurrencySymbol { get; set; } = "";

    [JsonPropertyName("currency_unit")]
    public string CurrencyUnit { get; set; } = "";

    [JsonPropertyName("cn_name")]
    public string CnName { get; set; } = "";

    [JsonPropertyName("platform_commission")]
    public string PlatformCommission { get; set; } = "0";

    [JsonPropertyName("handling_fee")]
    public string HandlingFee { get; set; } = "0";
}

public sealed class ChannelSnapshot
{
    [JsonPropertyName("site_name")]
    public string SiteName { get; set; } = "TW";

    [JsonPropertyName("site_cn")]
    public string SiteCn { get; set; } = "台湾站点";

    [JsonPropertyName("cargo")]
    public string Cargo { get; set; } = "Normal";

    [JsonPropertyName("cargo_cn")]
    public string CargoCn { get; set; } = "普货";

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = "蝦皮海外 - 7-11";

    [JsonPropertyName("channel_cn")]
    public string ChannelCn { get; set; } = "蝦皮海外 - 7-11";

    [JsonPropertyName("zone")]
    public string Zone { get; set; } = "所有地区";

    [JsonPropertyName("fee_modes")]
    public List<FeeMode> FeeModes { get; set; } = new();
}

public sealed class FeeMode
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("effective_date")]
    public string? EffectiveDate { get; set; }

    [JsonPropertyName("last_effective_date")]
    public string? LastEffectiveDate { get; set; }

    [JsonPropertyName("start_weight")]
    public double? StartWeight { get; set; }

    [JsonPropertyName("end_weight")]
    public double? EndWeight { get; set; }

    [JsonPropertyName("original_fee")]
    public double? OriginalFee { get; set; }

    [JsonPropertyName("increment_unit")]
    public double? IncrementUnit { get; set; }

    [JsonPropertyName("increment_amount")]
    public double? IncrementAmount { get; set; }

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";
}

public sealed class ApiResponse<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }
}

public sealed class SiteConfigData
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("site_info")]
    public List<SiteConfigSite> SiteInfo { get; set; } = new();
}

public sealed class SiteConfigSite
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("cn_name")]
    public string CnName { get; set; } = "";

    [JsonPropertyName("cargo_types")]
    public List<CargoType> CargoTypes { get; set; } = new();
}

public sealed class CargoType
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("cn_name")]
    public string CnName { get; set; } = "";

    [JsonPropertyName("channels")]
    public List<LogisticsChannel> Channels { get; set; } = new();
}

public sealed class LogisticsChannel
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("cn_name")]
    public string CnName { get; set; } = "";

    [JsonPropertyName("zones")]
    public List<ZoneInfo> Zones { get; set; } = new();
}

public sealed class ZoneInfo
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("cn_name")]
    public string CnName { get; set; } = "";

    [JsonPropertyName("fee_modes")]
    public List<FeeMode> FeeModes { get; set; } = new();
}
