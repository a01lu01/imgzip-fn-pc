using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using ImgZip.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace ImgZip.App;
public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; }
    private readonly ConfigStore store;
    private readonly string[] initialPaths;
    private bool loaded;
    private bool allowClose;
    private bool showingClose;
    private bool synchronizingTheme;
    private bool synchronizingEngine;

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    private const int DwmwaCaptionColor = 35;   // Windows 11 22000+
    private const int DwmwaTextColor = 36;
    public MainWindow(IWorkerClient worker, ConfigStore store, string[] initialPaths)
    {
        this.store = store;
        this.initialPaths = initialPaths;
        ViewModel = new MainViewModel(worker, store, Path.Combine(AppContext.BaseDirectory, "bin", "caesiumclt.exe"), action => DispatcherQueue.TryEnqueue(() => action()));
        InitializeComponent();
        Root.DataContext = ViewModel;
        Title = "ImgZip · 图片压缩";
        ExtendsContentIntoTitleBar = false;
        AppWindow.Closing += Window_Closing;
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModel.Config)) ApplyTheme();
            else if (e.PropertyName is nameof(ViewModel.EngineIndex) or nameof(ViewModel.NasEligible) or nameof(ViewModel.CanEdit)) SyncEngineButtons();
        };
        Root.ActualThemeChanged += (_, _) => ApplyCaptionTheme();
    }
    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        ApplyWindowPlacement();
        await Safe(async () =>
        {
            await ViewModel.InitializeAsync(); ApplyTheme();
            SyncEngineButtons();
            if (initialPaths.Length > 0) await ViewModel.SetSourcesAsync(initialPaths);
        });
    }
    private void ApplyTheme()
    {
        Root.RequestedTheme = ViewModel.Config.Theme switch { "light" => ElementTheme.Light, "dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        synchronizingTheme = true;
        ThemeSelector.SelectedIndex = ViewModel.Config.Theme switch { "light" => 1, "dark" => 2, _ => 0 };
        synchronizingTheme = false;
        ApplyCaptionTheme();
    }
    private void ApplyCaptionTheme()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var foreground = dark ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 31, 31, 31);
        var inactiveForeground = dark ? Windows.UI.Color.FromArgb(255, 148, 148, 148) : Windows.UI.Color.FromArgb(255, 120, 120, 120);
        var hoverBackground = dark ? Windows.UI.Color.FromArgb(255, 51, 51, 51) : Windows.UI.Color.FromArgb(255, 233, 233, 233);
        var pressedBackground = dark ? Windows.UI.Color.FromArgb(255, 68, 68, 68) : Windows.UI.Color.FromArgb(255, 214, 214, 214);
        // 标题栏本身由系统绘制：必须显式给背景/前景，否则系统主题与应用主题相反时会出现白底白图标。
        var barBackground = dark ? Windows.UI.Color.FromArgb(255, 31, 31, 31) : Windows.UI.Color.FromArgb(255, 243, 243, 243);
        AppWindow.TitleBar.BackgroundColor = barBackground;
        AppWindow.TitleBar.InactiveBackgroundColor = barBackground;
        AppWindow.TitleBar.ForegroundColor = foreground;
        AppWindow.TitleBar.InactiveForegroundColor = inactiveForeground;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = inactiveForeground;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = hoverBackground;
        // 悬停/按下时系统默认前景色会与自定义底色冲突，必须显式给出对比色。
        AppWindow.TitleBar.ButtonHoverForegroundColor = foreground;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = pressedBackground;
        AppWindow.TitleBar.ButtonPressedForegroundColor = foreground;
        // 标准标题栏不接受上面的 BackgroundColor；用 DWM 属性直接给系统绘制的标题栏着色。
        // COLORREF 为 0x00BBGGRR，这里三通道同值故与 RGB 等价。
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var caption = dark ? 0x001F1F1F : 0x00F3F3F3;
            var captionText = dark ? 0x00FFFFFF : 0x001F1F1F;
            _ = DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
            _ = DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref captionText, sizeof(int));
        }
        catch { }
    }
    private sealed record WindowState(int X, int Y, int W, int H);
    private string WindowStatePath => Path.Combine(store.DirectoryPath, "window-state.json");
    private WindowState? LoadWindowState()
    {
        try
        {
            if (!File.Exists(WindowStatePath)) return null;
            return JsonSerializer.Deserialize<WindowState>(File.ReadAllText(WindowStatePath));
        }
        catch { return null; }
    }
    private void SaveWindowPlacement()
    {
        try
        {
            Directory.CreateDirectory(store.DirectoryPath);
            var state = new WindowState(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            File.WriteAllText(WindowStatePath, JsonSerializer.Serialize(state));
        }
        catch { }
    }
    private void ApplyWindowPlacement()
    {
        try
        {
            var scale = Root.XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0) scale = 1.0;
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var saved = LoadWindowState();
            if (saved is not null && saved.W >= 320 && saved.H >= 240
                && DisplayArea.GetFromPoint(new PointInt32(saved.X + 40, saved.Y + 40), DisplayAreaFallback.None) is not null)
            {
                // 跟随上次位置；尺寸按当前工作区夹取，避免换了显示器/分辨率后超出屏幕。
                var w = Math.Min(saved.W, work.Width);
                var h = Math.Min(saved.H, work.Height);
                var x = Math.Clamp(saved.X, work.X, work.X + Math.Max(0, work.Width - w));
                var y = Math.Clamp(saved.Y, work.Y, work.Y + Math.Max(0, work.Height - h));
                AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
                return;
            }
            var logicalW = Math.Min(860, Math.Max(480, work.Width / scale - 40));
            var logicalH = Math.Min(740, Math.Max(360, work.Height / scale - 40));
            var width = (int)(logicalW * scale);
            var height = (int)(logicalH * scale);
            AppWindow.MoveAndResize(new RectInt32(
                work.X + Math.Max(0, (work.Width - width) / 2),
                work.Y + Math.Max(0, (work.Height - height) / 2),
                Math.Min(width, work.Width), Math.Min(height, work.Height)));
        }
        catch { }
    }
    private async Task Safe(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) { ViewModel.Message = ex.Message; }
    }
    private void InitializePicker(object picker) => WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
    private async void Files_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var picker = new FileOpenPicker(); InitializePicker(picker);
        foreach (var extension in new[] { ".jpg", ".jpeg", ".png", ".webp", ".gif" }) picker.FileTypeFilter.Add(extension);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count > 0) await ViewModel.SetSourcesAsync(files.Select(file => file.Path));
    });
    private async void Folder_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*"); InitializePicker(picker);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) await ViewModel.SetSourcesAsync([folder.Path]);
    });
    private void Source_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = ViewModel.CanEdit && e.DataView.Contains(StandardDataFormats.StorageItems) ? DataPackageOperation.Copy : DataPackageOperation.None;
        e.DragUIOverride.Caption = "作为压缩来源";
    }
    private async void Source_Drop(object sender, DragEventArgs e) => await Safe(async () =>
    {
        if (ViewModel.CanEdit && e.DataView.Contains(StandardDataFormats.StorageItems))
            await ViewModel.SetSourcesAsync((await e.DataView.GetStorageItemsAsync()).Select(item => item.Path));
    });
    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel.ClearSources();
    private void Preset_Click(object sender, RoutedEventArgs e) => ViewModel.SelectPreset((string)((FrameworkElement)sender).Tag);
    private async void Start_Click(object sender, RoutedEventArgs e) => await Safe(ViewModel.StartAsync);
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await Safe(ViewModel.CancelAsync);
    private async void Inspect_Click(object sender, RoutedEventArgs e) => await Safe(ViewModel.InspectAsync);
    private static CompressionTaskViewModel? TaskOf(object sender) => (sender as FrameworkElement)?.DataContext as CompressionTaskViewModel;
    private async void TaskCancel_Click(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is { } task) await Safe(() => ViewModel.CancelTaskAsync(task));
    }
    private async void TaskInspect_Click(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is { } task) await Safe(() => ViewModel.InspectTaskAsync(task));
    }
    private void TaskOpen_Click(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is { } task) OpenFolder(task.OpenOutputPath);
    }
    private async void TaskFailures_Click(object sender, RoutedEventArgs e)
    {
        if (TaskOf(sender) is { } task) await ShowFailuresAsync(task.Failures);
    }
    private void SyncEngineButtons()
    {
        synchronizingEngine = true;
        try
        {
            EnginePcButton.IsChecked = ViewModel.EngineIndex == 0;
            EngineNasButton.IsChecked = ViewModel.EngineIndex == 1;
            EnginePcButton.IsEnabled = ViewModel.CanEdit;
            EngineNasButton.IsEnabled = ViewModel.CanEdit && ViewModel.NasEligible;
        }
        finally { synchronizingEngine = false; }
    }
    private async void EngineButton_Click(object sender, RoutedEventArgs e)
    {
        if (synchronizingEngine) return;
        var index = (sender as FrameworkElement)?.Tag as string == "nas" ? 1 : 0;
        if (index == 1 && !ViewModel.NasEligible) { SyncEngineButtons(); return; }
        if (ViewModel.EngineIndex != index)
        {
            ViewModel.EngineIndex = index;
            if (loaded) await Safe(ViewModel.ProbeSelectedAsync);
        }
        SyncEngineButtons();
    }
    private async void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!loaded || synchronizingTheme) return;
        await Safe(() => ViewModel.SetThemeAsync(ThemeSelector.SelectedIndex switch { 1 => "light", 2 => "dark", _ => "system" }));
    }
    private void OpenOutput_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(ViewModel.OpenOutputPath);
    }
    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) { ViewModel.Message = "输出目录当前不可访问，请检查共享连接。"; return; }
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(path);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { ViewModel.Message = ex.Message; }
    }
    private async void Failures_Click(object sender, RoutedEventArgs e) => await ShowFailuresAsync(ViewModel.Failures);
    private async Task ShowFailuresAsync(IEnumerable<string> failures) => await Safe(async () =>
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "未完成的图片", CloseButtonText = "关闭",
            Content = new ScrollViewer { MaxHeight = 350, Content = new TextBlock { Text = string.Join("\n\n", failures), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }
        };
        await dialog.ShowAsync();
    });
    private async void Settings_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var dialog = new SettingsDialog(ViewModel, WinRT.Interop.WindowNative.GetWindowHandle(this)) { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme };
        await dialog.ShowAsync();
    });
    private async void Window_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SaveWindowPlacement();
        if (allowClose || !ViewModel.IsLocked) return;
        args.Cancel = true;
        if (showingClose) return;
        showingClose = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
                Title = "任务尚未确认结束",
                Content = "关闭窗口不会保证停止任务。可以先取消并等待确认；仅关闭窗口后，下次启动将重新检查任务状态。",
                PrimaryButtonText = ViewModel.CanCancel ? "取消并等待" : "重新检查",
                SecondaryButtonText = "仅关闭窗口", CloseButtonText = "返回"
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (ViewModel.CanCancel) await ViewModel.CancelAsync(); else await ViewModel.InspectAsync();
                if (!ViewModel.IsLocked) { allowClose = true; Close(); }
            }
            else if (result == ContentDialogResult.Secondary) { allowClose = true; Close(); }
        }
        catch (Exception ex) { ViewModel.Message = ex.Message; }
        finally { showingClose = false; }
    }
}
