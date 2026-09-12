/*
 * 功能说明：本机数据目录（Cookie、汇率设置、运费缓存），与旧工具隔离。
 * 创建日期：2026-08-14
 */

using System.IO;

namespace EcommerceWorkbench;

/// <summary>
/// 工作台本地数据路径。
/// </summary>
public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "EcommerceWorkbench");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
