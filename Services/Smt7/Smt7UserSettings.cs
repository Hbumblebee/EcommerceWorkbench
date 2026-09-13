/*
 * 功能说明：速卖通7 页本地设置（产品详情链接等）。
 * 创建日期：2026-09-13
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

    public static string GetFilePath() => Path.Combine(AppPaths.DataDirectory, "smt7-settings.json");

    public static Smt7UserSettings Load()
    {
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
                return new Smt7UserSettings();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Smt7UserSettings>(json, JsonOptions) ?? new Smt7UserSettings();
        }
        catch
        {
            return new Smt7UserSettings();
        }
    }

    public void Save()
    {
        var path = GetFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
