/*
 * 功能说明：本地 JSON 配置文件的原子写入，避免断电/崩溃留下半截文件导致设置被静默重置。
 * 主要职责：写同目录临时文件 → 落盘 → 原子替换目标文件；替换失败时清理临时文件。
 * 创建日期：2026-10-03
 */

using System.IO;

namespace EcommerceWorkbench.Services;

/// <summary>
/// 原子文本写入工具。
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// 以 UTF-8 原子写入文本：先写 <c>目标名.tmp</c>，再替换目标文件。
    /// </summary>
    public static void WriteAllText(string path, string contents)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            if (File.Exists(path))
            {
                // File.Replace 会保留目标路径上的 ACL，并且是一次元数据级替换。
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败不影响调用方看到的原始异常
        }
    }
}
