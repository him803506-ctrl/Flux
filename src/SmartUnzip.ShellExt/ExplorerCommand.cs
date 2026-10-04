// Flux Shell 扩展 —— COM 对象模型
//
// ── Windows 11 一级（紧凑）右键菜单的完整要求，本文件即答案 ──────────
//
// 一级菜单**不是**只用 IExplorerCommand 就能进去的。本对象必须同时暴露
// 四个接口，缺任何一个 shell 都会**静默丢弃**整条命令
// （表现为：一级菜单看不到，但"显示更多选项"里一切正常）。
//
// COM 多重继承布局（x64）：
//
//      pObj ──▶ ┌──────────────────────────┐  0x00  ← QI(IUnknown)
//               │ pVtblExplorerCommand     │           QI(IExplorerCommand)
//               ├──────────────────────────┤  0x08  ← QI(IObjectWithSite)
//               │ pVtblObjectWithSite      │
//               ├──────────────────────────┤  0x10  ← QI(IShellExtInit)
//               │ pVtblShellExtInit        │
//               ├──────────────────────────┤  0x18  ← QI(IContextMenu)
//               │ pVtblContextMenu         │
//               ├──────────────────────────┤  0x20
//               │ GCHandle (self)          │
//               └──────────────────────────┘
//
// 为什么这四个都要有 —— 实测对照（本机 2026-10-04）：
//
//   接口                QQ DLL   RarExt.dll   我们(旧)   我们(新)
//   IExplorerCommand      ✓         ✓           ✓         ✓
//   IEnumExplorerCommand  ✓         ✓           ✓         ✓
//   IObjectWithSite       ✓         ✓           ✗         ✓
//   IShellExtInit         ✓         ✓           ✗         ✓
//   IContextMenu          ✓         ✓           ✗         ✓
//
//   * QQ  = C:\life\extension\qq_explorer_command.dll（一级菜单可见）
//   * Rar = C:\Program Files\WinRAR\RarExt.dll      （一级菜单可见）
//
// trace 日志逐步佐证 shell 的探测顺序：
//   QI(A08CE4D0 /* IExplorerCommand */)  -> 必须 S_OK
//   QI(FC4801A3 /* IObjectWithSite  */)  -> 必须 S_OK，随后 SetSite 被调用
//   QI(000214E8 /* IShellExtInit    */)  -> 必须 S_OK
//   QI(000214E4 /* IContextMenu     */)  -> 必须 S_OK

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SmartUnzip.ShellExt;

/// <summary>
/// 实现 IExplorerCommand + IObjectWithSite + IShellExtInit + IContextMenu 的 COM 对象。
/// </summary>
internal sealed unsafe class ExplorerCommand
{
    /// <summary>对象内存首地址（pObj）。</summary>
    private nint* _obj;

    /// <summary>对象头里的机器字数量：4 个 vtable 指针 + 1 个 GCHandle。</summary>
    private const int ObjWords = 5;

    /// <summary>被选中的 ShellItemArray（由 IShellExtInit::Initialize 注入）。</summary>
    private nint _psiItemArray;

    private static nint* _vtblExplorerCommand;
    private static nint* _vtblObjectWithSite;
    private static nint* _vtblShellExtInit;
    private static nint* _vtblContextMenu;

    /// <summary>创建并返回**对象指针**（不是 vtable 指针）。</summary>
    public static nint Create()
    {
        var obj = new ExplorerCommand();
        obj.Allocate();
        return (nint)obj._obj;
    }

    private void Allocate()
    {
        BuildVtables();

        _obj = (nint*)NativeMemory.AllocZeroed(ObjWords, (nuint)sizeof(nint));
        _obj[0] = (nint)_vtblExplorerCommand;
        _obj[1] = (nint)_vtblObjectWithSite;
        _obj[2] = (nint)_vtblShellExtInit;
        _obj[3] = (nint)_vtblContextMenu;

        var handle = GCHandle.Alloc(this, GCHandleType.Normal);
        _obj[4] = GCHandle.ToIntPtr(handle);
    }

    /// <summary>从任意接口指针取回托管对象。</summary>
    private static ExplorerCommand From(nint pAny)
    {
        nint* basePtr = ResolveBase(pAny);
        return (ExplorerCommand)GCHandle.FromIntPtr(basePtr[4]).Target!;
    }

    private static void Free(nint pAny)
    {
        nint* basePtr = ResolveBase(pAny);
        var handle = GCHandle.FromIntPtr(basePtr[4]);
        if (handle.IsAllocated) handle.Free();
        NativeMemory.Free(basePtr);
    }

    /// <summary>
    /// 把任意接口指针还原成对象首地址。
    /// 四个 vtable 槽的地址是连续分布的，任意指针减去它所属槽的索引即可。
    /// </summary>
    private static nint* ResolveBase(nint pAny)
    {
        nint* o = (nint*)pAny;
        // 依次比对每个槽里存的 vtable 指针，命中的下标就是槽号。
        if (o[0] == (nint)_vtblExplorerCommand) return o;
        if (o[0] == (nint)_vtblObjectWithSite) return o - 1;
        if (o[0] == (nint)_vtblShellExtInit) return o - 2;
        if (o[0] == (nint)_vtblContextMenu) return o - 3;
        // 兜底：按 IExplorerCommand 处理
        return o;
    }

    /// <summary>引用计数（每个实例独立）。</summary>
    private int _refCount = 1;

    /// <summary>shell 注入的 site（IUnknown）。</summary>
    private nint _site;

    /// <summary>系统 Free-Threaded Marshaler（惰性创建）。</summary>
    private nint _ftm;

    /// <summary>
    /// 惰性创建 Free-Threaded Marshaler 并绑定到本对象。
    /// CoCreateFreeThreadedMarshaler 会把 pUnkOuter 登记为"自由线程"对象，
    /// 之后任何跨套间/跨进程接口请求都由 FTM 直接转发，无需我们自己写 IMarshal。
    /// </summary>
    private nint EnsureFtm()
    {
        if (_ftm != 0) return _ftm;
        try
        {
            nint outer = (nint)_obj;      // 本对象作为 outer
            nint ftm = 0;
            int hr = CoCreateFreeThreadedMarshaler(outer, &ftm);
            if (hr >= 0 && ftm != 0) _ftm = ftm;
            return _ftm;
        }
        catch (Exception ex)
        {
            return 0;
        }
    }

    [DllImport("ole32.dll", SetLastError = false)]
    private static extern int CoCreateFreeThreadedMarshaler(nint punkOuter, nint* ppunkMarshal);

    private static void BuildVtables()
    {
        if (_vtblExplorerCommand != null) return;

        // ---- 表 A：IUnknown(3) + IExplorerCommand(8) = 11 槽
        //
        // ★ 槽位顺序必须严格遵循 SDK 里的 IExplorerCommand 定义：
        //     3  GetTitle(IShellItemArray*, LPWSTR*)
        //     4  GetIcon(IShellItemArray*, LPWSTR*)
        //     5  GetToolTip(IShellItemArray*, LPWSTR*)
        //     6  GetCanonicalName(GUID*)
        //     7  GetState(IShellItemArray*, BOOL fOkToBeSlow, EXPCMDSTATE*)
        //     8  Invoke(IShellItemArray*, IBindCtx*)
        //     9  GetFlags(EXPCMDFLAGS*)
        //    10  EnumSubCommands(IEnumExplorerCommand**)
        //
        // 之前把 Invoke 与 GetFlags 写反、且 GetState 少了一个参数，
        // 导致 shell 调用时参数错位（把 IBindCtx/BOOL 当成指针写内存），
        // dllhost 随即崩溃，shell 便丢弃整条命令 —— 一级菜单看不到。
        var a = (nint*)NativeMemory.AllocZeroed(11, (nuint)sizeof(nint));
        a[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        a[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        a[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        a[3] = (nint)(delegate* unmanaged<nint, nint, nint*, int>)&GetTitle;
        a[4] = (nint)(delegate* unmanaged<nint, nint, nint*, int>)&GetIcon;
        a[5] = (nint)(delegate* unmanaged<nint, nint, nint*, int>)&GetToolTip;
        a[6] = (nint)(delegate* unmanaged<nint, Guid*, int>)&GetCanonicalName;
        a[7] = (nint)(delegate* unmanaged<nint, nint, uint, uint*, int>)&GetState;
        a[8] = (nint)(delegate* unmanaged<nint, nint, nint, int>)&Invoke;
        a[9] = (nint)(delegate* unmanaged<nint, uint*, int>)&GetFlags;
        a[10] = (nint)(delegate* unmanaged<nint, nint*, int>)&EnumSubCommands;
        _vtblExplorerCommand = a;

        // ---- 表 B：IUnknown(3) + IObjectWithSite(2) = 5 槽
        var b = (nint*)NativeMemory.AllocZeroed(5, (nuint)sizeof(nint));
        b[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        b[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        b[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        b[3] = (nint)(delegate* unmanaged<nint, nint, int>)&SetSite;
        b[4] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&GetSite;
        _vtblObjectWithSite = b;

        // ---- 表 C：IUnknown(3) + IShellExtInit(1) = 4 槽
        var c = (nint*)NativeMemory.AllocZeroed(4, (nuint)sizeof(nint));
        c[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        c[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        c[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        c[3] = (nint)(delegate* unmanaged<nint, nint, nint, nint, int>)&Initialize;
        _vtblShellExtInit = c;

        // ---- 表 D：IUnknown(3) + IContextMenu(3) = 6 槽
        var d = (nint*)NativeMemory.AllocZeroed(6, (nuint)sizeof(nint));
        d[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        d[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        d[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        d[3] = (nint)(delegate* unmanaged<nint, nint, uint, uint, uint, uint, int>)&QueryContextMenu;
        d[4] = (nint)(delegate* unmanaged<nint, nint, int>)&InvokeCommand;
        d[5] = (nint)(delegate* unmanaged<nint, nint, uint, nint, nint, uint, int>)&GetCommandString;
        _vtblContextMenu = d;
    }

    // ------------------------------------------------------------- IUnknown

    [UnmanagedCallersOnly]
    private static int QueryInterface(nint pAny, Guid* riid, nint* ppv)
    {
        if (ppv == null || riid == null) return Hr.E_POINTER;
        *ppv = 0;

        var obj = From(pAny);
        nint basePtr = (nint)ResolveBase(pAny);

        if (*riid == Iid.IUnknown || *riid == Iid.IExplorerCommand)
        {
            obj._refCount++;
            *ppv = basePtr;
            return Hr.S_OK;
        }

        // ★ 一级菜单四大必需接口，任何一个返回 E_NOINTERFACE，
        //    shell 都会静默丢弃整条命令。
        if (*riid == Iid.IObjectWithSite)
        {
            obj._refCount++;
            *ppv = basePtr + 8;          // 槽 1
            return Hr.S_OK;
        }

        if (*riid == Iid.IShellExtInit)
        {
            obj._refCount++;
            *ppv = basePtr + 16;         // 槽 2
            return Hr.S_OK;
        }

        if (*riid == Iid.IContextMenu)
        {
            obj._refCount++;
            *ppv = basePtr + 24;         // 槽 3
            return Hr.S_OK;
        }

        // ★ 跨进程封送：直接委托给系统自带的 Free-Threaded Marshaler。
        //   shell 在 dllhost.exe 里创建我们、在 explorer.exe 里使用我们，
        //   因此 QI(IMarshal) 必须有实现。RarExt.dll（WinRAR）含此接口。
        if (*riid == Iid.IMarshal)
        {
            var ftm = obj.EnsureFtm();
            if (ftm == 0)
            {
                return Hr.E_NOINTERFACE;
            }
            var ftmVtbl = *(nint**)ftm;
            var ftmQi = (delegate* unmanaged<nint, Guid*, nint*, int>)ftmVtbl[0];
            int hr2 = ftmQi(ftm, riid, ppv);
            return hr2;
        }

        return Hr.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(nint pAny)
    {
        var obj = From(pAny);
        var n = ++obj._refCount;
        return (uint)n;
    }

    [UnmanagedCallersOnly]
    private static uint Release(nint pAny)
    {
        var obj = From(pAny);
        var n = --obj._refCount;
        if (n == 0)
        {
            // site 不持有引用，直接清空
            obj._site = 0;
            Free(pAny);
        }
        return (uint)n;
    }

    // -------------------------------------------------- IObjectWithSite

    [UnmanagedCallersOnly]
    private static int SetSite(nint pAny, nint pUnkSite)
    {
        try
        {
            var obj = From(pAny);

            // 释放旧 site
            if (obj._site != 0)
            {
                var oldVtbl = *(nint**)obj._site;
                var oldRel = (delegate* unmanaged<nint, uint>)oldVtbl[2];
                oldRel(obj._site);
                obj._site = 0;
            }

            obj._site = pUnkSite;

            if (pUnkSite != 0)
            {
                var vtbl = *(nint**)pUnkSite;
                var addRef = (delegate* unmanaged<nint, uint>)vtbl[1];
                addRef(pUnkSite);
            }
            return Hr.S_OK;
        }
        catch
        {
            return Hr.E_FAIL;
        }
    }

    /// <summary>
    /// IObjectWithSite::GetSite —— 把请求转发给 shell 注入的 site。
    /// shell 会用它索要宿主服务（如 IServiceProvider / IShellBrowser）。
    /// </summary>
    [UnmanagedCallersOnly]
    private static int GetSite(nint pAny, Guid* riid, nint* ppvSite)
    {
        if (ppvSite == null) return Hr.E_POINTER;
        *ppvSite = 0;

        try
        {
            var obj = From(pAny);
            if (obj._site == 0) return Hr.E_FAIL;

            var siteVtbl = *(nint**)obj._site;
            var siteQi = (delegate* unmanaged<nint, Guid*, nint*, int>)siteVtbl[0];
            return siteQi(obj._site, riid, ppvSite);
        }
        catch
        {
            return Hr.E_FAIL;
        }
    }

    // -------------------------------------------------------- IShellExtInit

    /// <summary>
    /// IShellExtInit::Initialize —— shell 把当前选中的项交给我们。
    ///
    /// pidlFolder : 所在文件夹的 PIDL（可能为 NULL）
    /// pdtobj     : IDataObject，可从里面取 CF_HDROP 或 IShellItemArray
    /// hkeyProgID : 文件类型注册键（可能为 0）
    /// </summary>
    [UnmanagedCallersOnly]
    private static int Initialize(nint pAny, nint pidlFolder, nint pdtobj, nint hkeyProgID)
    {
        var obj = From(pAny);

        // 尝试从 IDataObject 里拿到 IShellItemArray（shell 现代菜单会提供）
        if (pdtobj != 0)
        {
            obj._psiItemArray = TryGetItemArray(pdtobj);
        }
        return Hr.S_OK;
    }

    /// <summary>
    /// 从 IDataObject 取 SHELLIDLIST / IShellItemArray。
    /// 取不到不报错 —— 后续 IExplorerCommand 会收到 psiItemArray 参数。
    /// </summary>
    private static nint TryGetItemArray(nint pdtobj)
    {
        try
        {
            var vtbl = *(nint**)pdtobj;
            // IDataObject: IUnknown(3) + GetData(3) + GetDataHere(4) + QueryGetData(5) + ...
            var queryGetData = (delegate* unmanaged<nint, nint, int>)vtbl[5];
            var getData = (delegate* unmanaged<nint, nint, nint, int>)vtbl[3];

            // FORMATETC { cfFormat=CF_HDROP(15); ptd=NULL; dwAspect=DVASPECT_CONTENT(1); lindex=-1; tymed=TYMED_HGLOBAL(1) }
            byte* fe = stackalloc byte[32];
            for (int i = 0; i < 32; i++) fe[i] = 0;
            *(ushort*)(fe + 0) = 15;              // cfFormat
            *(uint*)(fe + 8) = 1;                 // dwAspect
            *(int*)(fe + 16) = -1;                // lindex
            *(uint*)(fe + 20) = 1;                // tymed

            if (queryGetData(pdtobj, (nint)fe) != Hr.S_OK) return 0;

            // STGMEDIUM（32 字节）
            byte* stgm = stackalloc byte[32];
            for (int i = 0; i < 32; i++) stgm[i] = 0;
            if (getData(pdtobj, (nint)fe, (nint)stgm) != Hr.S_OK) return 0;

            // tymed 在 STGMEDIUM 的偏移 16（union 之后的 DWORD）
            uint tymed = *(uint*)(stgm + 16);
            nint hGlobal = *(nint*)stgm;

            if (tymed == 1 && hGlobal != 0)   // TYMED_HGLOBAL
            {
                // CF_HDROP: DROPFILES 头 + 宽字符文件名列表
                int count = Work.CountDropFiles(hGlobal);
                if (count > 0) return hGlobal;   // 记录句柄，供 Invoke 回退使用
            }
        }
        catch { /* 初始化失败不应崩溃 */ }
        return 0;
    }

    // --------------------------------------------------------- IContextMenu

    /// <summary>
    /// IContextMenu::QueryContextMenu —— 经典菜单构建入口。
    /// 一级菜单场景下 shell 会调用它（顶层项也走这条链路）。
    /// </summary>
    [UnmanagedCallersOnly]
    private static int QueryContextMenu(nint pAny, nint hmenu, uint indexMenu,
                                        uint idCmdFirst, uint idCmdLast, uint uFlags)
    {

        var obj = From(pAny);

        // MF_STRING = 0x0，插到指定位置
        var text = ShellExtConfig.MenuTitle;
        int n = AppendMenu(hmenu, 0x0, (nint)(idCmdFirst + 1), text);

        // 返回"已使用的 id 数 - 1"
        return n == 0 ? Hr.E_FAIL : 1;
    }

    /// <summary>IContextMenu::InvokeCommand —— 用户点了我们的菜单项。</summary>
    [UnmanagedCallersOnly]
    private static int InvokeCommand(nint pAny, nint pici)
    {
        if (pici == 0) return Hr.E_POINTER;
        var obj = From(pAny);

        // CMINVOKECOMMANDINFO 布局（x64）：
        //   DWORD cbSize; DWORD fMask; HWND hwnd;
        //   LPCSTR lpVerb; LPCSTR lpParameters; LPCSTR lpDirectory;
        //   int nShow; DWORD dwHotKey; HANDLE hIcon;
        nint lpVerb = *(nint*)(pici + 24);
        bool isMine = false;
        if (lpVerb == 0)
        {
            // 用 id 调用：wVerb 在偏移 8 的低 16 位
            int id = (int)(*(nint*)(pici + 8) & 0xFFFF);
            isMine = id == 1;
        }
        else
        {
            // 用字符串 verb 调用；MAKEINTRESOURCE 形式时指针值很小
            if (lpVerb < 0x10000) { isMine = true; }
        }

        if (!isMine) return Hr.S_OK;

        nint items = obj._psiItemArray;

        // 经典路径下 _psiItemArray 存的是 CF_HDROP 句柄，直接在 Work 里解
        return Work.InvokeDropHandle(items);
    }

    /// <summary>IContextMenu::GetCommandString —— 供 shell 取提示/verb。</summary>
    [UnmanagedCallersOnly]
    private static int GetCommandString(nint pAny, nint idCmd, uint uType,
                                        nint pReserved, nint pszName, uint cchMax)
    {
        if (pszName == 0) return Hr.E_POINTER;

        // GCS_VERBW = 4, GCS_VERBA = 0
        if (uType == 4)
        {
            // 宽字符 verb
            var span = new Span<char>((void*)pszName, (int)Math.Min(cchMax, 64u));
            var src = ShellExtConfig.VerbName.AsSpan();
            src[..Math.Min(src.Length, span.Length - 1)].CopyTo(span);
            span[Math.Min(src.Length, span.Length - 1)] = '\0';
            return Hr.S_OK;
        }
        if (uType == 0)
        {
            // ANSI verb
            var bytes = System.Text.Encoding.ASCII.GetBytes(ShellExtConfig.VerbName + "\0");
            var dst = new Span<byte>((void*)pszName, (int)Math.Min(cchMax, (uint)bytes.Length));
            bytes.AsSpan(0, dst.Length).CopyTo(dst);
            return Hr.S_OK;
        }
        return Hr.E_NOTIMPL;
    }

    // ------------------------------------------------------ IExplorerCommand

    [UnmanagedCallersOnly]
    private static int GetTitle(nint pObj, nint psiItemArray, nint* ppszName)
    {
        if (ppszName == null) return Hr.E_POINTER;
        *ppszName = CoTaskMemAllocString(ShellExtConfig.MenuTitle);
        return *ppszName == 0 ? Hr.E_OUTOFMEMORY : Hr.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetIcon(nint pObj, nint psiItemArray, nint* ppszIcon)
    {
        if (ppszIcon == null) return Hr.E_POINTER;
        *ppszIcon = 0;
        var exe = HostExe();
        if (string.IsNullOrEmpty(exe)) return Hr.S_FALSE;
        *ppszIcon = CoTaskMemAllocString(exe + ",0");
        return *ppszIcon == 0 ? Hr.E_OUTOFMEMORY : Hr.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetToolTip(nint pObj, nint psiItemArray, nint* ppszInfotip)
    {
        if (ppszInfotip == null) return Hr.E_POINTER;
        *ppszInfotip = CoTaskMemAllocString(ShellExtConfig.MenuToolTip);
        return *ppszInfotip == 0 ? Hr.E_OUTOFMEMORY : Hr.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetCanonicalName(nint pObj, Guid* pguidCommandName)
    {
        if (pguidCommandName == null) return Hr.E_POINTER;
        *pguidCommandName = ShellExtConfig.Clsid;
        return Hr.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetState(nint pObj, nint psiItemArray, uint fOkToBeSlow, uint* pFlags)
    {
        if (pFlags == null)
        {
            return Hr.E_POINTER;
        }

        // 关键：Windows 11 一级（紧凑）菜单会在**选中项未知**时用
        // psiItemArray == NULL 调 GetState，用来决定该命令是否进入菜单。
        // 此时必须返回 ECS_ENABLED，绝不能返回 ECS_HIDDEN。
        if (psiItemArray == 0)
        {
            *pFlags = ExpCmdState.ECS_ENABLED;
            return Hr.S_OK;
        }

        var n = Work.GetItemCount(psiItemArray);
        *pFlags = n > 0 ? ExpCmdState.ECS_ENABLED : ExpCmdState.ECS_HIDDEN;
        return Hr.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int GetFlags(nint pObj, uint* pFlags)
    {
        if (pFlags == null) return Hr.E_POINTER;
        // 平铺单项：命令自身就是可点的一级菜单项（不带 › 折叠子菜单）。
        // 声明 ECF_HASSUBCOMMANDS 会让 shell 去枚举子命令，若枚举器不完善
        // 就会一直卡在"正在加载…"。这里只需要一个直接项，故用 ECF_DEFAULT。
        *pFlags = ExpCmdFlags.ECF_DEFAULT;
        return Hr.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int Invoke(nint pObj, nint psiItemArray, nint pbc)
    {
        return Work.Invoke(psiItemArray);
    }

    [UnmanagedCallersOnly]
    private static int EnumSubCommands(nint pObj, nint* ppEnum)
    {
        if (ppEnum == null) return Hr.E_POINTER;
        *ppEnum = SubCommandEnum.Create();
        return *ppEnum == 0 ? Hr.E_OUTOFMEMORY : Hr.S_OK;
    }

    // ---------------------------------------------------------------- 辅助

    /// <summary>定位 Flux.exe：优先与本 DLL 同目录。</summary>
    internal static string HostExe()
    {
        var dllPath = typeof(ExplorerCommand).Assembly.Location;
        var dir = string.IsNullOrEmpty(dllPath)
            ? AppContext.BaseDirectory
            : Path.GetDirectoryName(dllPath) ?? AppContext.BaseDirectory;

        if (!string.IsNullOrEmpty(dir))
        {
            var p = Path.Combine(dir, ShellExtConfig.HostExeName);
            if (File.Exists(p)) return p;
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var p = Path.Combine(root, "Flux", ShellExtConfig.HostExeName);
            if (File.Exists(p)) return p;
        }
        return string.Empty;
    }

    /// <summary>用 COM 任务内存分配一个 LPWSTR。</summary>
    internal static nint CoTaskMemAllocString(string s)
    {
        var p = Marshal.AllocCoTaskMem((s.Length + 1) * 2);
        var span = new Span<char>((void*)p, s.Length + 1);
        s.AsSpan().CopyTo(span);
        span[s.Length] = '\0';
        return p;
    }

    /// <summary>HMENU 插入菜单项（IContextMenu::QueryContextMenu 用）。</summary>
    private static int AppendMenu(nint hmenu, uint flags, nint idNewItem, string text)
    {
        var p = Marshal.StringToHGlobalUni(text);
        try
        {
            return AppendMenuW(hmenu, flags, idNewItem, p);
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [DllImport("user32.dll", EntryPoint = "AppendMenuW", CharSet = CharSet.Unicode)]
    private static extern int AppendMenuW(nint hMenu, uint uFlags, nint uIDNewItem, nint lpNewItem);
}
