using System.IO;
using System.Runtime.InteropServices;

namespace HexoBlogManager.Services;

/// <summary>
/// 把文件移入 Windows 回收站（而不是直接永久删除）。
///
/// 为什么不用 Microsoft.VisualBasic.FileIO.FileSystem？
/// 那需要额外引用 Microsoft.VisualBasic 程序集，而这里只需要一个 API，
/// 直接用 shell32 的 SHFileOperation 更轻。
///
/// 注意：网络共享路径不支持回收站，调用方需要处理失败并决定是否强删。
/// </summary>
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;

        public ushort fFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        public IntPtr hNameMappings;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    private const uint FO_DELETE = 3;
    private const ushort FOF_SILENT = 0x0004;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOERRORUI = 0x0400;

    /// <summary>删除到回收站。返回是否成功，失败时通过 error 输出原因。</summary>
    public static bool Delete(string path, out string error)
    {
        error = string.Empty;

        if (!File.Exists(path))
        {
            error = "文件不存在。";
            return false;
        }

        try
        {
            var op = new SHFILEOPSTRUCT
            {
                hwnd = IntPtr.Zero,
                wFunc = FO_DELETE,
                // 路径必须以双 null 结尾，这是 SHFileOperation 的硬性要求
                pFrom = path + "\0\0",
                pTo = null,
                fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI
            };

            int code = SHFileOperation(ref op);

            if (code != 0)
            {
                error = $"无法移入回收站（错误码 {code}），该位置可能不支持回收站。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
