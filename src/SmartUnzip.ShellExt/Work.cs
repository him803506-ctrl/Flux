// Flux Shell 扩展 —— 选中项解析与进程启动

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartUnzip.ShellExt;

/// <summary>
/// 处理 Invoke：从 IShellItemArray 取出文件路径，启动 Flux.exe。
/// </summary>
internal static unsafe class Work
{
    // IShellItemArray vtable 槽位
    private const int SIA_BindToHandler = 3;
    private const int SIA_GetPropertyStore = 4;
    private const int SIA_GetPropertyDescriptionList = 5;
    private const int SIA_GetAttributes = 6;
    private const int SIA_GetCount = 7;
    private const int SIA_GetItemAt = 8;
    private const int SIA_EnumItems = 9;

    // IShellItem vtable 槽位
    private const int SI_BindToHandler = 3;
    private const int SI_GetParent = 4;
    private const int SI_GetDisplayName = 5;
    private const int SI_GetAttributes = 6;
    private const int SI_Compare = 7;

    private static readonly Guid BHID_DataObject = new("b8c0bd9f-ed24-455c-83e6-d5390c4fe8c4");
    private static readonly Guid BHID_SFUIObject = new("3981e224-f559-11d3-8e3a-00c04f6837d5");

    private const uint SIGDN_FILESYSPATH = 0x80058000;

    /// <summary>取 IShellItemArray 中的元素个数。</summary>
    internal static uint GetItemCount(nint psiItemArray)
    {
        if (psiItemArray == 0) return 0;
        try
        {
            var vt = *(nint***)psiItemArray;
            var fn = (delegate* unmanaged<nint, uint*, int>)vt[SIA_GetCount];
            uint count;
            if (fn(psiItemArray, &count) < 0) return 0;
            return count;
        }
        catch { return 0; }
    }

    // ---------------------------------------------- 经典路径（CF_HDROP）

    /// <summary>统计 HDROP 里的文件数。hDrop 来自 DROPFILES 结构的 pFiles 偏移。</summary>
    internal static int CountDropFiles(nint hDrop)
    {
        if (hDrop == 0) return 0;
        try
        {
            var p = GlobalLock(hDrop);
            if (p == 0) return 0;
            try
            {
                // DROPFILES { DWORD pFiles; POINT pt; BOOL fNC; BOOL fWide; }
                uint off = *(uint*)p;
                int fWide = *(int*)(p + 16);
                return CountStrings(p + (nint)off, fWide != 0);
            }
            finally { GlobalUnlock(hDrop); }
        }
        catch { return 0; }
    }

    /// <summary>从 HDROP 解析文件名列表并启动 Flux.exe。</summary>
    internal static int InvokeDropHandle(nint hDrop)
    {
        try
        {
            if (hDrop == 0) return Invoke(0);
            var files = ReadDropFiles(hDrop);
            if (files.Count == 0) return Hr.E_FAIL;

            var exe = ExplorerCommand.HostExe();
            if (string.IsNullOrEmpty(exe))
            {
                MessageBoxW(0,
                    "未找到 Flux.exe。\n\n请重新运行 Flux 安装程序。",
                    "Flux", 0x00000030);
                return Hr.E_FAIL;
            }
            Launch(exe, files);
            return Hr.S_OK;
        }
        catch (Exception ex)
        {
            try { LogCrash(ex.ToString()); } catch { }
            return Hr.E_FAIL;
        }
    }

    private static int CountStrings(nint p, bool wide)
    {
        int n = 0;
        if (wide)
        {
            var s = (char*)p;
            while (*s != 0) { while (*s != 0) s++; s++; n++; }
        }
        else
        {
            var s = (byte*)p;
            while (*s != 0) { while (*s != 0) s++; s++; n++; }
        }
        return n;
    }

    private static List<string> ReadDropFiles(nint hDrop)
    {
        var result = new List<string>();
        var p = GlobalLock(hDrop);
        if (p == 0) return result;
        try
        {
            uint off = *(uint*)p;
            int fWide = *(int*)(p + 16);
            nint basePtr = p + (nint)off;

            if (fWide != 0)
            {
                var s = (char*)basePtr;
                while (*s != 0)
                {
                    int len = 0;
                    while (s[len] != 0) len++;
                    result.Add(new string(s, 0, len));
                    s += len + 1;
                }
            }
            else
            {
                var s = (byte*)basePtr;
                while (*s != 0)
                {
                    int len = 0;
                    while (s[len] != 0) len++;
                    result.Add(Encoding.Default.GetString(s, len));
                    s += len + 1;
                }
            }
        }
        finally { GlobalUnlock(hDrop); }
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern nint GlobalLock(nint hMem);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern int GlobalUnlock(nint hMem);

    /// <summary>Invoke 主流程。</summary>
    internal static int Invoke(nint psiItemArray)
    {
        try
        {
            var files = CollectPaths(psiItemArray);
            if (files.Count == 0) return Hr.E_FAIL;

            var exe = ExplorerCommand.HostExe();
            if (string.IsNullOrEmpty(exe))
            {
                // 找不到宿主程序：给出明确提示，而不是静默失败
                MessageBoxW(0,
                    "未找到 Flux.exe。\n\n请重新运行 Flux 安装程序。",
                    "Flux", 0x00000030 /* MB_ICONWARNING */);
                return Hr.E_FAIL;
            }

            Launch(exe, files);
            return Hr.S_OK;
        }
        catch (Exception ex)
        {
            try { LogCrash(ex.ToString()); } catch { }
            return Hr.E_FAIL;
        }
    }

    /// <summary>枚举 IShellItemArray，取出所有本机文件系统路径。</summary>
    private static List<string> CollectPaths(nint psiItemArray)
    {
        var result = new List<string>();
        var count = GetItemCount(psiItemArray);
        if (count == 0) return result;

        var vt = *(nint***)psiItemArray;
        var getItemAt = (delegate* unmanaged<nint, uint, nint*, int>)vt[SIA_GetItemAt];

        for (uint i = 0; i < count; i++)
        {
            nint psi = 0;
            if (getItemAt(psiItemArray, i, &psi) < 0 || psi == 0) continue;
            try
            {
                var path = GetFileSystemPath(psi);
                if (!string.IsNullOrEmpty(path)) result.Add(path);
            }
            finally
            {
                Release(psi);
            }
        }
        return result;
    }

    /// <summary>IShellItem → 文件系统路径。</summary>
    private static string GetFileSystemPath(nint psi)
    {
        var vt = *(nint***)psi;
        var getDisplayName = (delegate* unmanaged<nint, uint, nint*, int>)vt[SI_GetDisplayName];

        nint psz = 0;
        if (getDisplayName(psi, SIGDN_FILESYSPATH, &psz) < 0 || psz == 0)
            return string.Empty;

        try
        {
            var s = Marshal.PtrToStringUni(psz) ?? string.Empty;
            return s;
        }
        finally
        {
            Marshal.FreeCoTaskMem(psz);
        }
    }

    private static void Release(nint p)
    {
        if (p == 0) return;
        var vt = *(nint***)p;
        var release = (delegate* unmanaged<nint, uint>)vt[2];
        release(p);
    }

    /// <summary>启动 Flux.exe，参数为 --extract "<file1>" "<file2>" ...</summary>
    private static void Launch(string exe, List<string> files)
    {
        var sb = new StringBuilder();
        sb.Append('"').Append(exe).Append('"');
        sb.Append(' ').Append(ShellExtConfig.ExtractSwitch);
        foreach (var f in files)
        {
            sb.Append(' ').Append('"').Append(f.Replace("\"", "\\\"")).Append('"');
        }

        var dir = Path.GetDirectoryName(exe);
        var argsPtr = Marshal.StringToHGlobalUni(sb.ToString());
        var exePtr = Marshal.StringToHGlobalUni(exe);
        var dirPtr = string.IsNullOrEmpty(dir) ? 0 : Marshal.StringToHGlobalUni(dir);
        try
        {
            // SW_SHOWNORMAL = 1，让 Flux 主窗口正常显示并立即开始解压
            var r = ShellExecuteW(0, "open", exePtr, argsPtr, dirPtr, 1);
            if (r <= 32) throw new InvalidOperationException("ShellExecuteW 失败，返回 " + r);
        }
        finally
        {
            Marshal.FreeHGlobal(argsPtr);
            Marshal.FreeHGlobal(exePtr);
            if (dirPtr != 0) Marshal.FreeHGlobal(dirPtr);
        }
    }

    /// <summary>崩溃日志写到 %TEMP%\Flux.ShellExt.log，便于排查。</summary>
    private static void LogCrash(string text)
    {
        var p = Path.Combine(Path.GetTempPath(), "Flux.ShellExt.log");
        File.AppendAllText(p, DateTime.Now.ToString("s") + " " + text + Environment.NewLine);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern nint ShellExecuteW(nint hwnd, string lpOperation,
        nint lpFile, nint lpParameters, nint lpDirectory, int nShowCmd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint hWnd, string lpText, string lpCaption, uint uType);
}
