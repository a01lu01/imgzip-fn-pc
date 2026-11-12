using ImgZip.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ImgZip.App;
public partial class App : Application
{
    // 一窗一任务：每个窗口承载一个任务草稿或结果，窗口按需创建、不重复使用。
    private readonly List<MainWindow> windows = [];
    private InstanceBroker? broker;
    private TaskCoordinator? coordinator;
    // 级联位置：新窗口相对上一个窗口偏移，避免完全重叠。
    private int cascadeIndex;
    private const int CascadeStep = 32;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImgZip", "logs");
                Directory.CreateDirectory(directory);
                var details = new System.Text.StringBuilder();
                details.AppendLine($"{DateTimeOffset.Now:O} {e.Exception}");
                for (var inner = e.Exception.InnerException; inner is not null; inner = inner.InnerException)
                    details.AppendLine($"[INNER] {inner.GetType().FullName} HR=0x{inner.HResult:X8}: {inner.Message}");
                foreach (var property in e.Exception.GetType().GetProperties())
                {
                    try { details.AppendLine($"[PROP] {property.Name} = {property.GetValue(e.Exception)}"); }
                    catch { }
                }
                foreach (System.Collections.DictionaryEntry entry in e.Exception.Data)
                    details.AppendLine($"[DATA] {entry.Key} = {entry.Value}");
                File.AppendAllText(Path.Combine(directory, "application.log"), details.ToString());
            }
            catch { }
        };
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var paths = ParsePaths(Environment.GetCommandLineArgs().Skip(1).ToArray());
        broker = new InstanceBroker();
        if (!broker.IsFirst)
        {
            try { await broker.SendAsync(paths); } finally { Exit(); }
            return;
        }
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImgZip");
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var worker = new WorkerClient(shell, Path.Combine(AppContext.BaseDirectory, "worker", "imgzip-worker.ps1"));
        var store = new ConfigStore(directory);
        // 应用级单例：名额池与任务记录只此一份，所有窗口共享。
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        coordinator = new TaskCoordinator(worker, new TaskStore(directory), action => dispatcher.TryEnqueue(() => action()));
        // 无参数启动（双击图标）：复用已有的空白草稿窗口，避免反复双击堆出一排空窗口。
        // 带 --path 启动则一定新开窗口：新来源归新窗口，不劫持已有草稿。
        if (paths.Length == 0 && FindBlankDraft() is { } blank)
        {
            // 这次启动由用户点击触发，本进程持有前台权，可以直接抢。
            BringToFront(blank);
        }
        else
        {
            OpenWindow(worker, store, paths);
        }
        _ = broker.ListenAsync(incoming => dispatcher.TryEnqueue(() =>
        {
            // 不带路径的第二次启动（又双击了一次）：只是把用户叫回来，不建新窗口。
            // 发送方已调用 AllowSetForegroundWindow 把前台权让过来，这里可以真的置前。
            if (incoming.Length == 0 && FindBlankDraft() is { } blank) { BringToFront(blank); return; }
            // 右键新来源：新开一个窗口承载，而不是劫持已有窗口的草稿。
            OpenWindow(worker, store, incoming);
        }));
    }

    /// <summary>找一个还没承办任务、也没有来源的窗口。没有就返回 null。</summary>
    private MainWindow? FindBlankDraft() =>
        windows.FirstOrDefault(w => w.ViewModel.ShowDraft && !w.ViewModel.HasSources);

    private MainWindow OpenWindow(IWorkerClient worker, ConfigStore store, string[] paths)
    {
        var window = new MainWindow(worker, store, coordinator!, paths, NextCascadeOffset());
        windows.Add(window);
        window.Closed += (_, _) =>
        {
            windows.Remove(window);
            // 关掉最后一个窗口时整个应用退出；否则只移除这个窗口，其他任务继续。
            if (windows.Count == 0) Exit();
        };
        BringToFront(window);
        return window;
    }

    private (int X, int Y)? NextCascadeOffset()
    {
        if (windows.Count == 0) { cascadeIndex = 0; return null; }
        cascadeIndex = (cascadeIndex + 1) % 8;
        return (cascadeIndex * CascadeStep, cascadeIndex * CascadeStep);
    }

    /// <summary>
    /// 把窗口带到前面。每个窗口都是这两种来源之一：用户直接启动进程（本进程持有前台权），
    /// 或收到转发消息（发送方先调了 AllowSetForegroundWindow）。两种情况都有抢前台的资格。
    /// 抢不到时（用户正在别处输入、UAC、锁屏）退回 BringWindowToTop，至少提到 Z 序顶部。
    /// </summary>
    private static void BringToFront(MainWindow window)
    {
        try
        {
            window.Activate();
            var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
            if (handle == IntPtr.Zero) return;
            if (!SetForegroundWindow(handle)) BringWindowToTop(handle);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);
    private static string[] ParsePaths(string[] args)
    {
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--path" && i + 1 < args.Length) paths.Add(args[++i]);
        return paths.ToArray();
    }
}
