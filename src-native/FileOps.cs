using System.Runtime.InteropServices;

namespace PictureButler;

/// <summary>文件操作工具：回收站删除等（Shell API）</summary>
public static class FileOps
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public IntPtr pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    private const uint FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40;   // 进回收站
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_SILENT = 0x4;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>
    /// 删除文件到回收站。
    /// **ownerHwnd 必须传调用方窗口句柄**：SHFileOperation 在没有所有者窗口时会去激活
    /// Explorer / 桌面，导致本程序窗口被压到其他窗口之后（删除后界面"掉到底层"，0.62.1 修复）。
    /// 传了句柄后 Shell 有正确的所有者，既不会抢前台，必要时弹出的确认框也模态挂在本窗口上。
    /// </summary>
    public static void DeleteToRecycleBin(string path, IntPtr ownerHwnd = default)
    {
        var op = new SHFILEOPSTRUCT
        {
            hwnd = ownerHwnd,
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",   // 双 null 结尾
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
        };
        int ret = SHFileOperation(ref op);
        if (ret != 0)
            throw new System.ComponentModel.Win32Exception(ret, "移入回收站失败");
    }
}
