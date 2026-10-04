// Flux Shell 扩展 —— IEnumExplorerCommand 实现
//
// 为什么需要它：
//   Windows 11 一级（紧凑）右键菜单**不会**把一个"扁平"的 IExplorerCommand
//   直接摆到主菜单上。它会先调用顶层命令的 EnumSubCommands，
//   把同应用的命令收进一个带 › 的应用折叠项。
//   因此顶层命令必须：
//       GetFlags()  -> ECF_HASSUBCOMMANDS
//       EnumSubCommands() -> 返回本枚举器
//   枚举器再逐项给出真正要展示/执行的命令。
//
// 内存布局与 ExplorerCommand 一致：
//      pObj ──▶ [ pVtable | GCHandle ]
//
// IEnumExplorerCommand: IUnknown(3) + Next/ Skip/ Reset/ Clone(4) = 7 槽

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SmartUnzip.ShellExt;

/// <summary>IEnumExplorerCommand：只包含一个子命令。</summary>
internal sealed unsafe class SubCommandEnum
{
    private nint* _obj;
    private static nint* _vtable;

    /// <summary>已经产出的子命令个数（本实现只有一个）。</summary>
    private int _index;

    public static nint Create()
    {
        var e = new SubCommandEnum();
        e.Allocate();
        return (nint)e._obj;
    }

    private void Allocate()
    {
        BuildVtable();
        _obj = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        _obj[0] = (nint)_vtable;
        _obj[1] = GCHandle.ToIntPtr(GCHandle.Alloc(this, GCHandleType.Normal));
    }

    private static SubCommandEnum From(nint pObj)
    {
        nint* o = (nint*)pObj;
        return (SubCommandEnum)GCHandle.FromIntPtr(o[1]).Target!;
    }

    private static void Free(nint pObj)
    {
        nint* o = (nint*)pObj;
        var h = GCHandle.FromIntPtr(o[1]);
        if (h.IsAllocated) h.Free();
        NativeMemory.Free(o);
    }

    private int _refCount = 1;

    private static void BuildVtable()
    {
        if (_vtable != null) return;

        var mem = (nint*)NativeMemory.AllocZeroed(7, (nuint)sizeof(nint));
        mem[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        mem[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        mem[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        mem[3] = (nint)(delegate* unmanaged<nint, uint, nint*, uint*, int>)&Next;
        mem[4] = (nint)(delegate* unmanaged<nint, uint, int>)&Skip;
        mem[5] = (nint)(delegate* unmanaged<nint, int>)&Reset;
        mem[6] = (nint)(delegate* unmanaged<nint, nint*, int>)&Clone;

        _vtable = mem;
    }

    // ------------------------------------------------------------- IUnknown

    [UnmanagedCallersOnly]
    private static int QueryInterface(nint pObj, Guid* riid, nint* ppv)
    {
        if (ppv == null || riid == null) return Hr.E_POINTER;
        *ppv = 0;
        var e = From(pObj);
        if (*riid == Iid.IUnknown || *riid == Iid.IEnumExplorerCommand)
        {
            e._refCount++;
            *ppv = pObj;
            return Hr.S_OK;
        }
        return Hr.E_NOINTERFACE;
    }

    [UnmanagedCallersOnly]
    private static uint AddRef(nint pObj) => (uint)++From(pObj)._refCount;

    [UnmanagedCallersOnly]
    private static uint Release(nint pObj)
    {
        var e = From(pObj);
        var n = --e._refCount;
        if (n == 0) Free(pObj);
        return (uint)n;
    }

    // ------------------------------------------------- IEnumExplorerCommand

    /// <summary>产出子命令。本实现只有 1 个。</summary>
    [UnmanagedCallersOnly]
    private static int Next(nint pObj, uint celt, nint* rgelt, uint* pceltFetched)
    {
        if (pceltFetched != null) *pceltFetched = 0;
        if (celt == 0) return Hr.S_OK;
        if (rgelt == null) return Hr.E_POINTER;

        var e = From(pObj);
        uint produced = 0;

        while (produced < celt && e._index < 1)
        {
            // 每个子命令是一个新的 ExplorerCommand 实例；
            // 它会以"子项"身份被调用 GetTitle / GetState / Invoke。
            rgelt[produced] = ExplorerCommand.Create();
            produced++;
            e._index++;
        }

        if (pceltFetched != null) *pceltFetched = produced;

        // 不足 celt 时按 COM 约定返回 S_FALSE
        return produced == celt ? Hr.S_OK : Hr.S_FALSE;
    }

    [UnmanagedCallersOnly]
    private static int Skip(nint pObj, uint celt)
    {
        var e = From(pObj);
        e._index += (int)celt;
        return e._index > 1 ? Hr.S_FALSE : Hr.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int Reset(nint pObj)
    {
        From(pObj)._index = 0;
        return Hr.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int Clone(nint pObj, nint* ppEnum)
    {
        if (ppEnum == null) return Hr.E_POINTER;
        *ppEnum = 0;
        return Hr.E_NOTIMPL;
    }
}
