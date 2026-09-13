/*
 * 功能说明：速卖通7 页本地设置（产品详情链接与全托2.0 分档参数）。
 * 创建日期：2026-09-13
 * 更新日期：2026-09-13 持久化成本/重量分档参数
 */
using System.IO;
using System.Text.Json;

namespace EcommerceWorkbench.Services.Smt7;

public sealed class Smt7UserSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string ProductEditUrl { get; set; } = "";
    public List<Smt7CostTier> CostTiers { get; set; } = [];
    public List<Smt7WeightTier> WeightTiers { get; set; } = [];

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
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
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
