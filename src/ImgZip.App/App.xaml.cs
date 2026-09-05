using ImgZip.Core;
using Microsoft.UI.Xaml;

namespace ImgZip.App;
public partial class App : Application
{
    private MainWindow? window;
    private InstanceBroker? broker;
    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImgZip", "logs");
                Directory.CreateDirectory(directory);
                File.AppendAllText(Path.Combine(directory, "application.log"), $"{DateTimeOffset.Now:O} {e.Exception}\n");
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
        window = new MainWindow(worker, new ConfigStore(directory), paths);
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
