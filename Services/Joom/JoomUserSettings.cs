/*
 * 功能说明：JOOM 页本地设置（产品详情链接、SKU 后缀列表等）。
 * 创建日期：2026-09-05
 * 修改记录：2026-09-29 增加 SKU 通用后缀与颜色后缀
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

    /// <summary>产品重复时按顺序拼接的通用后缀；原默认值里 <c>-AS001</c> 重复了两次，已去重。</summary>
    public const string DefaultGeneralSkuSuffixes = "-1，-01，-001，-0001，-00001，AS，AS01，-AS001，-AS0001，-AS00001";

    /// <summary>启用颜色后缀时可选的颜色；原默认值里 <c>-black</c> 重复了两次，已去重。</summary>
    public const string DefaultColorSkuSuffixes = "-grey，-red，-green，-yellow，-black，-blue，-white";

    public string ProductEditUrl { get; set; } = "";

    /// <summary>产品重复时按顺序拼接的通用后缀，中英文逗号分隔。</summary>
    public string GeneralSkuSuffixes { get; set; } = DefaultGeneralSkuSuffixes;

    /// <summary>启用颜色后缀时可选的颜色，中英文逗号分隔，拼在通用后缀之前。</summary>
    public string ColorSkuSuffixes { get; set; } = DefaultColorSkuSuffixes;

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
        AtomicFile.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
