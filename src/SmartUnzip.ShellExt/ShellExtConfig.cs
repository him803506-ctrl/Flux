// Flux Windows 11 一级右键菜单 Shell 扩展 —— 公共常量与定义
//
// 背景：Windows 11 的紧凑右键菜单只接受**具有包标识（Package Identity）**
//       的应用注册的命令。传统注册表菜单（HKCR\*\shell\...\command）会被
//       折叠进"显示更多选项"。WinRAR 6.10 正是改为 IExplorerCommand +
//       稀疏包（Sparse Package）后才重新回到一级菜单的。
//
// 本工程用 NativeAOT 手写 COM vtable，输出一个无运行时依赖的原生 DLL。

using System.Runtime.InteropServices;

namespace SmartUnzip.ShellExt;

/// <summary>HRESULT 常量。</summary>
internal static class Hr
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_NOTIMPL = unchecked((int)0x80004001);
    public const int E_NOINTERFACE = unchecked((int)0x80004002);
    public const int E_POINTER = unchecked((int)0x80004003);
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
    public const int CLASS_E_NOAGGREGATION = unchecked((int)0x80040110);
    public const int CLASS_E_CLASSNOTAVAILABLE = unchecked((int)0x80040111);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);
}

/// <summary>EXPCMDSTATE —— GetState 返回值。</summary>
internal static class ExpCmdState
{
    public const uint ECS_ENABLED = 0x00000000;
    public const uint ECS_DISABLED = 0x00000001;
    public const uint ECS_HIDDEN = 0x00000002;
}

/// <summary>EXPCMDFLAGS —— GetFlags 返回值。</summary>
internal static class ExpCmdFlags
{
    public const uint ECF_DEFAULT = 0x00000000;
    public const uint ECF_HASSUBCOMMANDS = 0x00000001;
    public const uint ECF_HASSPLITBUTTON = 0x00000002;
    public const uint ECF_HIDELABEL = 0x00000004;
    public const uint ECF_ISSEPARATOR = 0x00000008;
    public const uint ECF_HASLUASHIELD = 0x00000010;
    public const uint ECF_SEPARATORBEFORE = 0x00000020;
    public const uint ECF_SEPARATORAFTER = 0x00000040;
    public const uint ECF_ISDROPDOWN = 0x00000080;
}

/// <summary>接口 IID。</summary>
internal static class Iid
{
    public static readonly Guid IUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IClassFactory = new("00000001-0000-0000-C000-000000000046");
    public static readonly Guid IExplorerCommand = new("A08CE4D0-FA25-44AB-B57C-C7B1C323E0B9");
    public static readonly Guid IObjectArray = new("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9");
    public static readonly Guid IEnumExplorerCommand = new("A88826F8-186F-4987-AADE-EA0CEF8FBFE8");

    /// <summary>
    /// IObjectWithSite —— Windows 11 一级右键菜单的**硬性要求**。
    /// shell 会在拿到 IExplorerCommand 之后立刻 QI 这个接口，
    /// 用于把宿主 site 注入扩展。若返回 E_NOINTERFACE，
    /// shell 会直接丢弃该命令（表现为顶层菜单里看不到，但经典菜单正常）。
    /// 实测：QQExtension 与 WinRAR 的扩展 DLL 都实现了它。
    /// </summary>
    public static readonly Guid IObjectWithSite = new("FC4801A3-2BA9-11CF-A229-00AA003D7352");

    /// <summary>
    /// IShellExtInit —— 经典 shell 扩展初始化接口。
    /// 实测：QQExtension（C:\life\extension\qq_explorer_command.dll）与
    /// WinRAR（RarExt.dll）**都实现了它**，而只实现 IExplorerCommand
    /// 的扩展无法进入 Windows 11 一级菜单。
    /// shell 用它把选中的 ShellItemArray（IDataObject）交给我们。
    /// </summary>
    public static readonly Guid IShellExtInit = new("000214E8-0000-0000-C000-000000000046");

    /// <summary>
    /// IContextMenu —— 经典右键菜单构建接口。
    /// 与 IShellExtInit 成对出现；一级菜单的底层仍走这条链路。
    /// </summary>
    public static readonly Guid IContextMenu = new("000214E4-0000-0000-C000-000000000046");

    /// <summary>
    /// IMarshal —— 跨进程列集接口。
    /// shell 在 dllhost.exe（surrogate）里创建我们的对象，但菜单渲染在
    /// explorer.exe，因此必须经过 COM 封送。RarExt.dll（WinRAR）含此接口。
    /// </summary>
    public static readonly Guid IMarshal = new("00000003-0000-0000-C000-000000000046");

    /// <summary>ICallFactory —— 列集 call 对象工厂。</summary>
    public static readonly Guid ICallFactory = new("1C733A30-2A1C-11CE-ADE5-00AA0044773D");

    /// <summary>IStdMarshalInfo —— 标准列集信息（配合 IMarshal 使用）。</summary>
    public static readonly Guid IStdMarshalInfo = new("00000018-0000-0000-C000-000000000046");

    /// <summary>
    /// Free-Threaded Marshaler (FTM) 的 CLSID —— 由系统提供。
    /// 我们直接把它当作 IMarshal 的实现（业界经典做法）。
    /// </summary>
    public static readonly Guid ClsidFreeThreadedMarshaler = new("0000033A-0000-0000-C000-000000000046");
}

/// <summary>
/// 扩展的固定配置：CLSID、菜单文案、目标可执行文件。
/// </summary>
internal static class ShellExtConfig
{
    /// <summary>本 Shell 扩展的 COM 组件 CLSID（与 AppxManifest 中 desktop5:Verb/@Clsid 必须一致）。</summary>
    public const string ClsidString = "F10A5E70-3C42-4B18-9D6E-7A25B8C4E013";

    public static readonly Guid Clsid = new(ClsidString);

    /// <summary>菜单项显示文字。</summary>
    public const string MenuTitle = "使用 Flux 解压";

    /// <summary>经典 IContextMenu 路径下使用的 verb 名（GetCommandString 返回）。</summary>
    public const string VerbName = "FluxExtract";

    /// <summary>菜项提示（鼠标悬停）。</summary>
    public const string MenuToolTip = "自动识别真实格式并匹配密码，用 7-Zip 安全解压";

    /// <summary>传给 Flux.exe 的开关。</summary>
    public const string ExtractSwitch = "--extract";

    /// <summary>Flux.exe 的文件名（与 ShellExt DLL 同目录或以安装路径注册）。</summary>
    public const string HostExeName = "Flux.exe";

    /// <summary>本 DLL 的文件名（仅用于诊断日志）。</summary>
    public const string DllName = "Flux.ShellExt.dll";
}
