using ImgZip.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ImgZip.App;
public partial class App : Application
{
    private MainWindow? window;
    private InstanceBroker? broker;
    private TaskCoordinator? coordinator;
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
        // 阶段 1c 仍是单窗口，因此统一使用 UI 线程的派发器。
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        coordinator = new TaskCoordinator(worker, new TaskStore(directory), action => dispatcher.TryEnqueue(() => action()));
        window = new MainWindow(worker, store, coordinator, paths);
        window.Activate();
        _ = broker.ListenAsync(incoming => window.DispatcherQueue.TryEnqueue(async () =>
        {
            window.Activate();
            try { await window.ViewModel.SetSourcesAsync(incoming); } catch (Exception ex) { window.ViewModel.Message = ex.Message; }
        }));
        window.Closed += (_, _) => broker.Dispose();
    }
    private static string[] ParsePaths(string[] args)
    {
        var paths = new List<string>();
        for (var i = 0; i < args.Length; i++)
            if (args[i] == "--path" && i + 1 < args.Length) paths.Add(args[++i]);
        return paths.ToArray();
    }
}
