/*
 * 功能说明：速卖通全托页本地设置（产品详情链接、通用 SKU 后缀、查重范围与店铺、挂号包邮公式参数）。
 * 创建日期：2026-10-22
 * 修改记录：2026-10-22 保存普货挂号包邮公式参数
 */

using System.IO;
using System.Text.Json;
using EcommerceWorkbench.Services.Joom;

namespace EcommerceWorkbench.Services.SmtFull;

public sealed class SmtFullUserSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public const string DefaultGeneralSkuSuffixes = JoomUserSettings.DefaultGeneralSkuSuffixes;

    public string ProductEditUrl { get; set; } = "";

    /// <summary>中英文逗号分隔。查重发现重复时按顺序拼接出未占用的新 SKU。</summary>
    public string GeneralSkuSuffixes { get; set; } = DefaultGeneralSkuSuffixes;

    /// <summary>启用颜色后缀时可选的颜色。</summary>
    public string OccupancyColorSuffixes { get; set; } = JoomUserSettings.DefaultColorSkuSuffixes;

    public bool OccupancyUseColorSuffix { get; set; }

    /// <summary>查重范围：all / online / offline / draft。</summary>
    public string OccupancyScope { get; set; } = "all";

    /// <summary>查重店铺：auto=跟随当前产品所属店铺，否则为具体 shopId。</summary>
    public string OccupancyShopId { get; set; } = "auto";

    /// <summary>普货挂号包邮公式参数。空时用计算表默认值。</summary>
    public SmtQuoteFormula? Formula { get; set; }

    public static string GetFilePath() => Path.Combine(AppPaths.DataDirectory, "smt-full-settings.json");

    public static SmtFullUserSettings Load()
    {
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
                return WithDefaults(new SmtFullUserSettings());

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<SmtFullUserSettings>(json, JsonOptions) ?? new SmtFullUserSettings();
            return WithDefaults(loaded);
        }
        catch
        {
            return WithDefaults(new SmtFullUserSettings());
        }
    }

    public void Save()
    {
        var path = GetFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    private static SmtFullUserSettings WithDefaults(SmtFullUserSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.GeneralSkuSuffixes))
            settings.GeneralSkuSuffixes = DefaultGeneralSkuSuffixes;
        if (string.IsNullOrWhiteSpace(settings.OccupancyColorSuffixes))
            settings.OccupancyColorSuffixes = JoomUserSettings.DefaultColorSkuSuffixes;
        if (string.IsNullOrWhiteSpace(settings.OccupancyScope))
            settings.OccupancyScope = "all";
        if (string.IsNullOrWhiteSpace(settings.OccupancyShopId))
            settings.OccupancyShopId = "auto";
        settings.Formula ??= SmtQuoteFormula.CreateDefault();
        if (settings.Formula.FreightCountries.Count == 0)
            settings.Formula.FreightCountries = SmtQuoteFormula.CreateDefault().FreightCountries;
        if (settings.Formula.PercentCountries.Count == 0)
            settings.Formula.PercentCountries = SmtQuoteFormula.CreateDefault().PercentCountries;
        return settings;
    }
}
