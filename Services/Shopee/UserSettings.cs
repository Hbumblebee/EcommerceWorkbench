/*
 * 功能说明：用户可手动维护的本地设置（汇率、默认净利润率等），持久化到本机。
 * 创建日期：2026-07-30
 * 修改记录：
 *   2026-08-14 迁入 EcommerceWorkbench，增加默认预期净利润
 *   2026-08-14 默认汇率改为 4.77；默认值改为预期净利润率
 */
using System.IO;
using System.Text.Json;

namespace EcommerceWorkbench.Services.Shopee;

public sealed class UserSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public const decimal DefaultExchangeRate = 4.77m;

    public decimal ExchangeRate { get; set; } = DefaultExchangeRate;
    public decimal DiscountPercent { get; set; } = 30m;
    public decimal WithinBorderSf { get; set; } = 1m;
    public decimal ActivityScRatePercent { get; set; } = 1m;
    public decimal WithdrawHfRatePercent { get; set; } = 1m;
    public decimal? DefaultExpectedProfitRate { get; set; }
    public bool AutoCalc { get; set; } = true;

    public static string GetFilePath() => Path.Combine(AppPaths.DataDirectory, "user-settings.json");

    public static UserSettings Load()
    {
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
                return new UserSettings();

            var json = File.ReadAllText(path);
            var loaded = JsonSerializer.Deserialize<UserSettings>(json, JsonOptions) ?? new UserSettings();
            if (loaded.ExchangeRate == 4.3m)
                loaded.ExchangeRate = DefaultExchangeRate;
            return loaded;
        }
        catch
        {
            return new UserSettings();
        }
    }

    public void Save()
    {
        var path = GetFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
