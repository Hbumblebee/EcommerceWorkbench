/*
 * 功能说明：速卖通7 页本地设置（产品详情链接、通用 SKU 后缀与全托2.0 分档参数）。
 * 创建日期：2026-09-13
 * 更新日期：2026-09-13 持久化成本/重量分档参数
 *           2026-10-07 增加通用 SKU 后缀：打开详情后「搜索SKU」只去末尾通用后缀、保留颜色
 */
using System.IO;
using System.Text.Json;
using EcommerceWorkbench.Services.Joom;

namespace EcommerceWorkbench.Services.Smt7;

public sealed class Smt7UserSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// 「搜索SKU」要自动去掉的末尾后缀；与 JOOM 页共用同一份默认值
    /// （两个平台的货号后缀规则一致，商品库里的货号同样带颜色）。
    /// </summary>
    public const string DefaultGeneralSkuSuffixes = JoomUserSettings.DefaultGeneralSkuSuffixes;

    public string ProductEditUrl { get; set; } = "";
    public List<Smt7CostTier> CostTiers { get; set; } = [];
    public List<Smt7WeightTier> WeightTiers { get; set; } = [];

    /// <summary>中英文逗号分隔；打开详情页生成「搜索SKU」时去掉这些末尾后缀。</summary>
    public string GeneralSkuSuffixes { get; set; } = DefaultGeneralSkuSuffixes;

    /// <summary>启用颜色后缀时可选的颜色；与 JOOM 页共用同一份默认值。</summary>
    public string OccupancyColorSuffixes { get; set; } = JoomUserSettings.DefaultColorSkuSuffixes;

    /// <summary>查重时是否启用颜色后缀（组合 SKU 会按段各选一个颜色）。</summary>
    public bool OccupancyUseColorSuffix { get; set; }

    /// <summary>查重范围：all（采集箱+待发布+在线）/ online / offline / draft。</summary>
    public string OccupancyScope { get; set; } = "all";

    /// <summary>查重店铺：auto=跟随当前产品所属店铺，否则为具体 shopId。</summary>
    public string OccupancyShopId { get; set; } = "auto";

    public static string GetFilePath() => Path.Combine(AppPaths.DataDirectory, "smt7-settings.json");

    public static Smt7UserSettings Load()
    {
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
                return WithDefaults(new Smt7UserSettings());

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<Smt7UserSettings>(json, JsonOptions) ?? new Smt7UserSettings();
            return WithDefaults(loaded);
        }
        catch
        {
            return WithDefaults(new Smt7UserSettings());
        }
    }

    public void Save()
    {
        var path = GetFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    public Smt7Settings ToCalcSettings()
    {
        var settings = new Smt7Settings
        {
            CostTiers = CostTiers.Count > 0 ? CloneCost(CostTiers) : Smt7Settings.CreateDefault().CostTiers,
            WeightTiers = WeightTiers.Count > 0 ? CloneWeight(WeightTiers) : Smt7Settings.CreateDefault().WeightTiers
        };
        Smt7Calculator.ApplyIntervalNames(settings);
        return settings;
    }

    public void ApplyCalcSettings(Smt7Settings settings)
    {
        CostTiers = CloneCost(settings.CostTiers);
        WeightTiers = CloneWeight(settings.WeightTiers);
    }

    private static Smt7UserSettings WithDefaults(Smt7UserSettings settings)
    {
        var defaults = Smt7Settings.CreateDefault();
        if (settings.CostTiers.Count != defaults.CostTiers.Count)
            settings.CostTiers = CloneCost(defaults.CostTiers);
        if (settings.WeightTiers.Count != defaults.WeightTiers.Count)
            settings.WeightTiers = CloneWeight(defaults.WeightTiers);
        return settings;
    }

    private static List<Smt7CostTier> CloneCost(IEnumerable<Smt7CostTier> source) =>
        source.Select(t => new Smt7CostTier
        {
            Interval = t.Interval,
            MaxExclusiveCost = t.MaxExclusiveCost,
            MarginPercent = t.MarginPercent,
            Factor1 = t.Factor1,
            Constant = t.Constant,
            Factor2 = t.Factor2
        }).ToList();

    private static List<Smt7WeightTier> CloneWeight(IEnumerable<Smt7WeightTier> source) =>
        source.Select(t => new Smt7WeightTier
        {
            Label = t.Label,
            MaxExclusiveKg = t.MaxExclusiveKg,
            Markup = t.Markup
        }).ToList();
}
