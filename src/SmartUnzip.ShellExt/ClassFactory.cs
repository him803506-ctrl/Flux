// Flux Shell 扩展 —— IClassFactory + DLL 导出入口
//
// 内存布局与 ExplorerCommand 一致（见该文件顶部注释）：
//     pObj ──▶ [ pVtable | GCHandle ]
//
// Explorer 的加载序列：
//     LoadLibraryW(Flux.ShellExt.dll)
//       → DllGetClassObject(CLSID, IID_IClassFactory, &pcf)
//       → pcf->CreateInstance(NULL, IID_IExplorerCommand, &pCmd)
//       → pCmd->GetTitle / GetState / GetFlags / Invoke

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SmartUnzip.ShellExt;

/// <summary>IClassFactory 实现。</summary>
internal sealed unsafe class ClassFactory
{
    private nint* _obj;

    private static nint* _vtable;

    public static nint Create()
    {
        var f = new ClassFactory();
        f.Allocate();
        return (nint)f._obj;
    }

    private void Allocate()
    {
        BuildVtable();
        _obj = (nint*)NativeMemory.AllocZeroed(2, (nuint)sizeof(nint));
        _obj[0] = (nint)_vtable;
        _obj[1] = GCHandle.ToIntPtr(GCHandle.Alloc(this, GCHandleType.Normal));
    }

    private static ClassFactory From(nint pObj)
    {
        nint* o = (nint*)pObj;
        return (ClassFactory)GCHandle.FromIntPtr(o[1]).Target!;
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

        // IUnknown(3) + CreateInstance + LockServer = 5
        var mem = (nint*)NativeMemory.AllocZeroed(5, (nuint)sizeof(nint));

        mem[0] = (nint)(delegate* unmanaged<nint, Guid*, nint*, int>)&QueryInterface;
        mem[1] = (nint)(delegate* unmanaged<nint, uint>)&AddRef;
        mem[2] = (nint)(delegate* unmanaged<nint, uint>)&Release;
        mem[3] = (nint)(delegate* unmanaged<nint, nint, Guid*, nint*, int>)&CreateInstance;
        mem[4] = (nint)(delegate* unmanaged<nint, int, int>)&LockServer;

        _vtable = mem;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(nint pObj, Guid* riid, nint* ppv)
    {
        if (ppv == null || riid == null) return Hr.E_POINTER;
        *ppv = 0;
        var f = From(pObj);
        if (*riid == Iid.IUnknown || *riid == Iid.IClassFactory)
        {
            f._refCount++;
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
        var f = From(pObj);
        var n = --f._refCount;
        if (n == 0) Free(pObj);
        return (uint)n;
    }

    [UnmanagedCallersOnly]
    private static int CreateInstance(nint pObj, nint pUnkOuter, Guid* riid, nint* ppv)
    {
        if (ppv == null) return Hr.E_POINTER;
        *ppv = 0;
        if (pUnkOuter != 0) return Hr.CLASS_E_NOAGGREGATION;
        if (riid == null) return Hr.E_POINTER;

        if (*riid != Iid.IUnknown && *riid != Iid.IExplorerCommand)
            return Hr.E_NOINTERFACE;

        *ppv = ExplorerCommand.Create();
        return Hr.S_OK;
    }

    [UnmanagedCallersOnly]
    private static int LockServer(nint pObj, int fLock) => Hr.S_OK;
}

/// <summary>DLL 导出入口。</summary>
internal static unsafe class Exports
{
    /// <summary>必须与 AppxManifest.xml 里 com:Class/@Id 一致。</summary>
    [UnmanagedCallersOnly(EntryPoint = "DllGetClassObject")]
    public static int DllGetClassObject(Guid* rclsid, Guid* riid, nint* ppv)
    {
        if (ppv == null || rclsid == null || riid == null) return Hr.E_POINTER;
        *ppv = 0;


        if (*rclsid != ShellExtConfig.Clsid) return Hr.CLASS_E_CLASSNOTAVAILABLE;
        if (*riid != Iid.IUnknown && *riid != Iid.IClassFactory) return Hr.E_NOINTERFACE;

        *ppv = ClassFactory.Create();
        return Hr.S_OK;
    }

    [UnmanagedCallersOnly(EntryPoint = "DllCanUnloadNow")]
    public static int DllCanUnloadNow() => Hr.S_OK;
}
