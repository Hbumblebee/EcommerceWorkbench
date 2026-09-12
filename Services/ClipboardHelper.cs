/*
 * 功能说明：稳健的剪贴板写入（Win32 + 重试），规避 CLIPBRD_E_CANT_OPEN。
 * 主要职责：将文本写入系统剪贴板；被其它进程占用时自动重试。
 * 创建日期：2026-07-23
 * 修改记录：2026-08-14 迁入 EcommerceWorkbench
 */

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace EcommerceWorkbench.Services;

/// <summary>
/// 剪贴板辅助：优先 Win32 Unicode 文本，失败时回退 WPF API。
/// </summary>
public static class ClipboardHelper
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    /// <summary>
    /// 尝试将文本写入剪贴板；默认重试多次。
    /// </summary>
    public static bool TrySetText(string text, Window? owner = null, int maxAttempts = 12)
    {
        var hwnd = owner is null
            ? IntPtr.Zero
            : new WindowInteropHelper(owner).Handle;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (TrySetTextWin32(text, hwnd))
            {
                return true;
            }

            if (hwnd != IntPtr.Zero && TrySetTextWin32(text, IntPtr.Zero))
            {
                return true;
            }

            if (TrySetTextWpf(text))
            {
                return true;
            }

            Thread.Sleep(40 * attempt);
        }

        return false;
    }

    private static bool TrySetTextWpf(string text)
    {
        try
        {
            var data = new DataObject();
            data.SetData(DataFormats.UnicodeText, text);
            Clipboard.SetDataObject(data, true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySetTextWin32(string text, IntPtr hwnd)
    {
        if (!OpenClipboard(hwnd))
        {
            return false;
        }

        try
        {
            if (!EmptyClipboard())
            {
                return false;
            }

            var bytes = (text.Length + 1) * 2;
            var hGlobal = GlobalAlloc(GmemMoveable, (UIntPtr)(uint)bytes);
            if (hGlobal == IntPtr.Zero)
            {
                return false;
            }

            var target = GlobalLock(hGlobal);
            if (target == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                Marshal.WriteInt16(target, text.Length * 2, 0);
            }
            finally
            {
                GlobalUnlock(hGlobal);
            }

            if (SetClipboardData(CfUnicodeText, hGlobal) == IntPtr.Zero)
            {
                return false;
            }

            hGlobal = IntPtr.Zero;
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(IntPtr hMem);
}
