using System.IO;
using System.Windows;
using SmartUnzip.Core;

namespace SmartUnzip;

public partial class App : Application
{
    /// <summary>全局日志。</summary>
    public static Logger Log { get; private set; } = null!;

    public static SettingsManager Settings { get; private set; } = null!;

    public static SevenZipEngine Engine { get; private set; } = null!;

    public static PasswordVault Vault { get; private set; } = null!;

    public static PasswordMatcher Matcher { get; private set; } = null!;

    public static ExtractionTaskManager TaskManager { get; private set; } = null!;

    /// <summary>命令行传入的待解压文件（右键菜单"使用 Flux 解压"）。</summary>
    public static IReadOnlyList<string> StartupFiles { get; private set; } = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 无界面模式：注册/卸载右键菜单、查询状态 ----------------------
        // 这些分支必须在创建主窗口前返回，避免闪一下窗体。
        if (TryRunHeadlessMode(e.Args)) return;

        // 解析命令行：支持 --extract <files...> 与直接传文件路径
        var files = new List<string>();
        for (int i = 0; i < e.Args.Length; i++)
        {
            var a = e.Args[i];
            if (string.Equals(a, "--extract", StringComparison.OrdinalIgnoreCase)) continue;
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            if (File.Exists(a)) files.Add(a);
        }
        StartupFiles = files;

        base.OnStartup(e);

        // 全局异常兜底：任何未处理异常都写日志，避免"静默崩溃"。
        DispatcherUnhandledException += (_, args) =>
        {
            try { Log?.Error($"UI 未处理异常：{args.Exception}"); } catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { Log?.Error($"域未处理异常：{args.ExceptionObject}"); } catch { }
        };

        Settings = new SettingsManager(new Logger(false));
        Log = new Logger(Settings.Current.EnableLogging);
        Engine = new SevenZipEngine(Log);
        Vault = new PasswordVault(Log);
        Matcher = new PasswordMatcher(Vault, Engine, Log);
        TaskManager = new ExtractionTaskManager(Engine, Vault, Matcher, Settings, Log);

        Log.Info("========================================");
        Log.Info("Flux 启动");
        Log.Info($"7-Zip 引擎：{(Engine.IsAvailable ? $"{Engine.EnginePath}（{Engine.Version}）" : "未检测到")}");
        Log.Info($"密码库条目数：{Vault.Count}");

        if (!Engine.IsAvailable)
        {
            MessageBox.Show(
                "未检测到 7-Zip。\n\n" +
                "Flux 使用 7-Zip 作为核心解压引擎，请先安装 7-Zip：\n" +
                "https://www.7-zip.org/\n\n" +
                "安装后重启 Flux 即可。",
                "Flux — 缺少解压引擎",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 处理无界面命令行模式。返回 true 表示已处理完毕，应当直接退出。
    ///
    ///   --register-shell    注册 Windows 11 一级右键菜单（稀疏包）
    ///   --unregister-shell  卸载
    ///   --shell-status      查询状态
    ///
    /// 全部支持 <c>--silent</c>：把结果写进日志与退出码，不弹窗（安装脚本用）。
    /// </summary>
    private static bool TryRunHeadlessMode(string[] args)
    {
        bool Has(string name) => args.Any(a =>
            string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        var isRegister = Has("--register-shell");
        var isUnregister = Has("--unregister-shell");
        var isStatus = Has("--shell-status");
        if (!isRegister && !isUnregister && !isStatus) return false;

        var silent = Has("--silent");

        // 找一个可写的日志位置：注册阶段主程序可能还没初始化 Logger
        var logPath = Path.Combine(Path.GetTempPath(), "Flux.ShellRegister.log");
        void W(string m)
        {
            try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss ") + m + Environment.NewLine); }
            catch { }
        }

        try
        {
            W("========================================");
            W("模式：" + (isRegister ? "register" : isUnregister ? "unregister" : "status"));

            var code = 0;
            string title, body;

            if (isStatus)
            {
                var st = ShellRegistrar.GetStatus();
                W("已注册=" + st.IsRegistered + " 包=" + st.PackageFullName + " 说明=" + st.Message);
                title = "Flux — 右键菜单状态";
                body = st.IsRegistered
                    ? $"已注册\n\n包名：{st.PackageFullName}"
                    : $"未注册\n\n{st.Message}";
                code = st.IsRegistered ? 0 : 1;
            }
            else if (isRegister)
            {
                var (ok, log) = ShellRegistrar.Register();
                W(log);
                W("结果：" + (ok ? "成功" : "失败"));
                title = "Flux — 右键菜单注册";
                body = ok
                    ? "已成功注册 Windows 11 一级右键菜单项。\n\n" +
                      "若菜单项未立即出现，请在任务管理器里重启「Windows 资源管理器」。"
                    : "注册失败：\n\n" + log;
                code = ok ? 0 : 2;
            }
            else
            {
                var (ok, log) = ShellRegistrar.Unregister();
                W(log);
                W("结果：" + (ok ? "成功" : "失败"));
                title = "Flux — 右键菜单卸载";
                body = ok ? "已移除右键菜单项。" : "卸载失败：\n\n" + log;
                code = ok ? 0 : 3;
            }

            if (!silent)
            {
                MessageBox.Show(body, title, MessageBoxButton.OK,
                    code == 0 ? MessageBoxImage.Information : MessageBoxImage.Error);
            }

            Environment.Exit(code);
            return true;
        }
        catch (Exception ex)
        {
            W("异常：" + ex);
            if (!silent)
            {
                MessageBox.Show("操作出错：\n\n" + ex.Message,
                    "Flux", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Environment.Exit(4);
            return true;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            if (Settings.Current.RememberPasswordForSession)
                Matcher.ClearSession();
            Settings.Save();
            Log.Info("Flux 退出");
        }
        catch { }
        base.OnExit(e);
    }
}
