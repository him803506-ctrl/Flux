// Flux —— Windows 11 一级右键菜单注册 / 卸载
//
// ── 原理 ─────────────────────────────────────────────────────────────
// Windows 11 的紧凑右键菜单只接受**具有包标识（Package Identity）**的应用
// 注册的命令。传统注册表菜单（HKCR\*\shell\...）会被折叠进"显示更多选项"。
//
// 因此我们把 Flux 的 IExplorerCommand 做成一个**稀疏包**
// （Sparse Package，官方叫 "packaging with external location"）：
//   * 包内只放 AppxManifest.xml + 图标
//   * 真正的 Flux.ShellExt.dll / Flux.exe 留在安装目录，
//     用 uap10:AllowExternalContent=true + -ExternalLocation 指向它们
//
// 本模块负责：
//   --register-shell    导入证书（如需） → Add-AppxPackage 安装稀疏包
//   --unregister-shell  Remove-AppxPackage 卸载
//   --shell-status      查询当前状态
//
// ── 两个必须遵守的约束（否则一级菜单永远不出现）────────────────────────
// 1. **绝不要**在 HKLM\SOFTWARE\Classes\CLSID\{<CLSID>} 下写 InProcServer32。
//    一旦存在该注册项，shell 会走经典进程内加载路径而忽略 PackagedCom，
//    表现就是：经典菜单（Shift+右键）里能看见，一级菜单里永远没有。
//    注册前会主动清理该键（有权限时）。
// 2. 证书必须已在「受信任的根证书颁发机构」。否则 Add-AppxPackage 会因
//    签名不受信任而失败。

using System.Diagnostics;
using System.IO;
using System.Text;

namespace SmartUnzip.Core;

/// <summary>右键菜单 Shell 扩展的注册状态。</summary>
public sealed class ShellRegistrationStatus
{
    public bool IsRegistered { get; init; }
    public string PackageFullName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 稀疏包（Sparse Package）注册器。
/// </summary>
public static class ShellRegistrar
{
    /// <summary>包清单里的 Identity/Name，必须与 AppxManifest.xml 一致。</summary>
    public const string PackageName = "Flux.ShellMenu";

    /// <summary>包清单里的 Identity/Publisher，必须与 AppxManifest.xml 及证书一致。</summary>
    public const string PackagePublisher = "CN=Flux";

    /// <summary>本扩展的 COM CLSID，必须与 AppxManifest.xml 里
    /// com:Class/@Id、desktop5:Verb/@Clsid 以及 C# ShellExtConfig.ClsidString 一致。</summary>
    public const string Clsid = "{F10A5E70-3C42-4B18-9D6E-7A25B8C4E013}";

    /// <summary>外壳扩展 DLL 文件名。</summary>
    public const string ShellDllName = "Flux.ShellExt.dll";

    /// <summary>稀疏包文件名。</summary>
    public const string PackageFileName = "Flux.ShellIntegration.msix";

    /// <summary>签名证书文件名（公钥部分）。</summary>
    public const string CertFileName = "Flux-Sparse.cer";

    /// <summary>安装目录（Flux.exe 所在目录），同时作为 ExternalLocation。</summary>
    public static string InstallDir =>
        AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

    /// <summary>稀疏包完整路径。</summary>
    public static string PackagePath => Path.Combine(InstallDir, PackageFileName);

    /// <summary>判断本机是否为 Windows 11（build >= 22000）。</summary>
    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>查询当前是否已注册。</summary>
    public static ShellRegistrationStatus GetStatus()
    {
        var script =
            "$n='" + PackageName + "';" +
            "$p = Get-AppxPackage -Name $n -ErrorAction SilentlyContinue;" +
            "if($p){ 'FOUND|' + $p.PackageFullName + '|' + $p.InstallLocation }" +
            "else{ 'NOTFOUND' }";

        var (ok, output) = RunPowerShell(script);
        if (!ok)
            return new ShellRegistrationStatus { Message = "查询失败：" + output };

        output = output.Trim();
        if (output.StartsWith("FOUND|", StringComparison.Ordinal))
        {
            var parts = output.Split('|');
            return new ShellRegistrationStatus
            {
                IsRegistered = true,
                PackageFullName = parts.Length > 1 ? parts[1] : string.Empty,
                Message = "已注册",
            };
        }

        return new ShellRegistrationStatus
        {
            IsRegistered = false,
            Message = string.IsNullOrEmpty(output) ? "未注册" : output,
        };
    }

    /// <summary>
    /// 注册稀疏包。返回 (成功, 日志)。
    /// </summary>
    public static (bool Ok, string Log) Register()
    {
        var log = new StringBuilder();

        var msix = PackagePath;
        if (!File.Exists(msix))
        {
            return (false,
                $"找不到稀疏包：{msix}\n\n" +
                "请确认安装目录下存在 " + PackageFileName + "，" +
                "或重新运行 Flux 安装程序。");
        }

        var dll = Path.Combine(InstallDir, ShellDllName);
        if (!File.Exists(dll))
            return (false, $"找不到外壳扩展 DLL：{dll}");

        if (!IsWindows11)
        {
            log.AppendLine("提示：当前系统低于 Windows 11，一级菜单不可用，" +
                           "菜单项会出现在「显示更多选项」中。");
        }

        log.AppendLine($"包文件：{msix}");
        log.AppendLine($"外部位置：{InstallDir}");

        // 1) 清理可能存在的"经典 CLSID 劫持"注册。
        //    这一步对一级菜单是**必需的**（见文件头说明），
        //    若无管理员权限则仅提示，不影响后续安装。
        CleanClassicClsid(log);

        // 2) 导入签名证书到「受信任的根证书颁发机构」。
        //    稀疏包（.msix）必须由受信任证书签名，否则 Add-AppxPackage
        //    会以"签名不受信任"失败。需要管理员权限。
        EnsureCertificateTrusted(log);

        // 3) 安装稀疏包（ExternalLocation 指向安装目录）
        var script =
            "$ErrorActionPreference='Stop';" +
            "$m = '" + msix.Replace("'", "''") + "';" +
            "$e = '" + InstallDir.Replace("'", "''") + "';" +
            "try{" +
            "  Add-AppxPackage -Path $m -ExternalLocation $e -ForceUpdateFromAnyVersion -ErrorAction Stop;" +
            "  $p = Get-AppxPackage -Name '" + PackageName + "';" +
            "  if($p){ 'OK|' + $p.PackageFullName } else { 'OK|?' }" +
            "}catch{" +
            "  'ERR|' + $_.Exception.Message" +
            "}";

        var (ok, output) = RunPowerShell(script);
        log.AppendLine("Add-AppxPackage 输出：" + output.Trim());

        if (!ok || output.Contains("ERR|"))
            return (false, log.ToString());

        // 4) 提示重启资源管理器（新注册的一级菜单项需要它重新读取）
        log.AppendLine();
        log.AppendLine("安装完成。若右键菜单里暂时看不到「使用 Flux 解压」，");
        log.AppendLine("请重启资源管理器，或注销后重新登录一次。");

        return (true, log.ToString());
    }

    /// <summary>卸载稀疏包。</summary>
    public static (bool Ok, string Log) Unregister()
    {
        var script =
            "$n='" + PackageName + "';" +
            "$p = Get-AppxPackage -Name $n -ErrorAction SilentlyContinue;" +
            "if(-not $p){ 'NONE'; exit 0 };" +
            "try{" +
            "  Remove-AppxPackage -Package $p.PackageFullName -ErrorAction Stop;" +
            "  'OK'" +
            "}catch{ 'ERR|' + $_.Exception.Message }";

        var (ok, output) = RunPowerShell(script);
        output = output.Trim();
        if (output.Contains("ERR|"))
            return (false, output);
        return (true, output);
    }

    /// <summary>
    /// 删除 HKLM\SOFTWARE\Classes\CLSID\{CLSID} 下的经典注册。
    /// 该注册项存在会劫持 shell 的加载路径，使一级菜单失效。
    /// 无管理员权限时静默跳过（不影响非提权安装）。
    /// </summary>
    private static void CleanClassicClsid(StringBuilder log)
    {
        var key = @"HKLM:\SOFTWARE\Classes\CLSID\" + Clsid;
        var script =
            "$k = '" + key.Replace("'", "''") + "';" +
            "if(Test-Path $k){" +
            "  try{ Remove-Item -LiteralPath $k -Recurse -Force -ErrorAction Stop; 'CLEANED' }" +
            "  catch{ 'NOPERM' }" +
            "} else { 'ABSENT' }";

        var (_, output) = RunPowerShell(script);
        output = output.Trim();
        switch (output)
        {
            case "CLEANED":
                log.AppendLine("已清理会劫持一级菜单的经典 CLSID 注册项。");
                break;
            case "NOPERM":
                log.AppendLine("提示：检测到残留的经典 CLSID 注册项但无权限清理。" +
                               "若一级菜单不出现，请以管理员身份重新运行本程序。");
                break;
        }
    }

    /// <summary>
    /// 确保签名证书已装进「受信任的人」（TrustedPeople）。
    ///
    /// 为什么是 TrustedPeople 而不是「受信任的根证书颁发机构」：
    ///   本程序用的是**自签名**证书，只为给自家 .msix 做侧载签名。
    ///   Windows 对非商店 .msix 的侧载要求是"签名证书位于
    ///   LocalMachine\TrustedPeople（或 Root）"，实测两者都能让
    ///   Add-AppxPackage 成功。
    ///   但把自签证书装进 Root 等于让它成为**全系统根 CA** —— 权限过大，
    ///   而 TrustedPeople 正是为"信任某个具体发布者"设计的，最小权限。
    ///
    /// 实测（2026-10-04）：把证书从所有存储里移除后，
    ///   Add-AppxPackage 立刻失败 —— 说明这一步**不能省**。
    /// </summary>
    private static void EnsureCertificateTrusted(StringBuilder log)
    {
        var cer = Path.Combine(InstallDir, CertFileName);
        if (!File.Exists(cer))
        {
            log.AppendLine($"提示：未找到证书 {CertFileName}，跳过证书导入。");
            return;
        }

        // 不做"是否已存在"的前置判断，直接导入。
        //
        // 原因：Import-Certificate 本身幂等（同一张证书重复导入不会报错、
        // 也不会产生重复项），而任何"先探测再决定"的写法都容易出岔子 ——
        // 之前就踩过一个经典坑：probe 返回字符串 "NOTFOUND"，
        // 而 C# 侧用 Contains("FOUND") 判断，'NOTFOUND' 里恰好含 'FOUND'，
        // 于是永远误判成"已受信任"、直接跳过导入，导致注册必然失败。
        var script =
            "try{" +
            "  Import-Certificate -FilePath '" + cer.Replace("'", "''") + "'" +
            "      -CertStoreLocation 'Cert:\\LocalMachine\\TrustedPeople' -ErrorAction Stop | Out-Null;" +
            "  'OK'" +
            "}catch{ 'ERR|' + $_.Exception.Message }";

        var (_, output) = RunPowerShell(script);
        output = output.Trim();

        if (output.Contains("ERR|"))
        {
            log.AppendLine("警告：证书导入失败（" + output + "）。");
            log.AppendLine("      若菜单不出现，请以管理员身份运行 Flux.exe --register-shell。");
        }
        else
        {
            log.AppendLine("已导入签名证书到「受信任的人」（TrustedPeople）。");
        }
    }

    /// <summary>
    /// 运行 PowerShell 并返回 (成功, 输出)。不弹窗、不阻塞交互。
    /// </summary>
    private static (bool Ok, string Output) RunPowerShell(string script)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" +
                            script.Replace("\"", "\\\"") + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using var p = Process.Start(psi);
            if (p is null) return (false, "无法启动 PowerShell");

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);

            var text = (stdout + (stderr.Length > 0 ? "\n" + stderr : string.Empty)).Trim();
            return (p.ExitCode == 0, text);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
