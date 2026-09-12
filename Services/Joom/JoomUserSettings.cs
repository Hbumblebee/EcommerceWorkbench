/*
 * 功能说明：JOOM 页本地设置（产品详情链接等）。
 * 创建日期：2026-09-05
 */
using System.IO;
using System.Text.Json;

namespace EcommerceWorkbench.Services.Joom;

public sealed class JoomUserSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string ProductEditUrl { get; set; } = "";

    public static string GetFilePath() => Path.Combine(AppPaths.DataDirectory, "joom-settings.json");

    public static JoomUserSettings Load()
    {
        try
        {
            var path = GetFilePath();
            if (!File.Exists(path))
                return new JoomUserSettings();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<JoomUserSettings>(json, JsonOptions) ?? new JoomUserSettings();
        }
        catch
        {
            return new JoomUserSettings();
        }
    }

    public void Save()
    {
        var path = GetFilePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
