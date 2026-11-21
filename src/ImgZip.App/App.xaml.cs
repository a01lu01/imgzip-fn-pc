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
        // 无参数启动（双击图标）：把用户叫回来，而不是再堆一个空窗口。
        // 带 --path 启动则一定新开窗口：新来源归新窗口，不劫持已有草稿。
        if (paths.Length == 0) Recall(worker, store);
        else OpenWindow(worker, store, paths);
        _ = broker.ListenAsync(incoming => dispatcher.TryEnqueue(() =>
        {
            // 不带路径的第二次启动（又双击了一次）：只是把用户叫回来，不建新窗口。
            // 发送方已调用 AllowSetForegroundWindow 把前台权让过来，这里可以真的置前。
            if (incoming.Length == 0) { Recall(worker, store); return; }
            // 右键新来源：新开一个窗口承载，而不是劫持已有窗口的草稿。
            OpenWindow(worker, store, incoming);
        }));
    }

    /// <summary>
    /// 无来源启动该做什么：有空白草稿就只叫它，没有就把已有窗口整体叫回来。
    /// 取舍本身在 <see cref="RecallPolicy"/> 里，这里只负责执行。
    /// </summary>
    private void Recall(IWorkerClient worker, ConfigStore store)
    {
        var blank = FindBlankDraft();
        switch (RecallPolicy.Decide(windows.Count, blank is not null))
        {
            case RecallAction.FocusDraft:
                // 这次启动由用户点击触发，本进程持有前台权，可以直接抢。
                BringToFront(blank!);
                break;
            case RecallAction.RevealAll:
                RevealAll();
                break;
            default:
                OpenWindow(worker, store, []);
                break;
        }
    }

    /// <summary>
    /// 把已有窗口整体叫到前面来。用在两处：双击图标但没有空白草稿；以及点击任务栏图标
    /// 把某个最小化窗口叫回来时，让同组窗口一起回来。
    ///
    /// 被最小化的先还原；最后打开（通常是最后用的那个）的窗口拿前台。选择「最后打开的」
    /// 而不是「最后交互的」：后者需要跟踪 WM_ACTIVATEAPP，而这里要的只是「别让用户
    /// 面对一个空屏幕」，最后打开的窗口是最可能的意图目标，且实现上没有额外状态。
    /// </summary>
    private void RevealAll()
    {
        // 还原窗口可能触发同步回调（窗口置前会跑消息），遍历副本避免集合被改动。
        foreach (var window in windows.ToArray())
        {
            try
            {
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                if (handle == IntPtr.Zero) continue;
                if (IsIconic(handle)) ShowWindow(handle, SwRestore);
            }
            catch { }
        }
        if (windows.Count > 0) BringToFront(windows[^1]);
    }

    /// <summary>
    /// 点任务栏图标把某个最小化窗口叫回来时，把其余仍在最小化的窗口一并还原。
    /// 不抢前台——前台交给用户点中的那个窗口，我们只负责让别的窗口别再躺着。
    /// </summary>
    private void RevealSiblings()
    {
        foreach (var window in windows.ToArray())
        {
            try
            {
                var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                if (handle == IntPtr.Zero) continue;
                if (IsIconic(handle)) ShowWindow(handle, SwRestore);
            }
            catch { }
        }
    }

    /// <summary>找一个还没承办任务、也没有来源的窗口。没有就返回 null。</summary>
    /// <remarks>
    /// 判据不能只看 <c>ShowDraft</c>：草稿区现在整个生命周期都显示（见问题 2），
    /// 光靠它会退化成一个恒真的条件，让「已办过任务、来源已清空」的窗口被当成可复用草稿。
    /// 真正要说的是「还没承办过任务」。
    /// </remarks>
    private MainWindow? FindBlankDraft() =>
        windows.FirstOrDefault(w => !w.ViewModel.HasCurrentTask && !w.ViewModel.HasSources);

    private MainWindow OpenWindow(IWorkerClient worker, ConfigStore store, string[] paths)
    {
        var window = new MainWindow(worker, store, coordinator!, paths, NextCascadeOffset());
        windows.Add(window);
        // 合并后的任务栏按钮点一下只能还原一个窗口；挂上子类过程，让其余最小化的窗口跟上。
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        WindowReveal.Attach(handle, _ => RevealSiblings());
        window.Closed += (_, _) =>
        {
            WindowReveal.Detach(handle);
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

    private const int SwRestore = 9;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    private static string[] ParsePaths(string[] args)
    {
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--path" && i + 1 < args.Length) paths.Add(args[++i]);
        return paths.ToArray();
    }
}
