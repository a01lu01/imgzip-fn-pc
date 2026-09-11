using System.Diagnostics;
using System.Runtime.InteropServices;
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
    private readonly string[] initialPaths;
    private bool loaded;
    private bool allowClose;
    private bool showingClose;
    private bool synchronizingTheme;
    private bool synchronizingEngine;

    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    public MainWindow(IWorkerClient worker, ConfigStore store, string[] initialPaths)
    {
        this.initialPaths = initialPaths;
        ViewModel = new MainViewModel(worker, store, Path.Combine(AppContext.BaseDirectory, "bin", "caesiumclt.exe"), action => DispatcherQueue.TryEnqueue(() => action()));
        InitializeComponent();
        Root.DataContext = ViewModel;
        Title = "ImgZip · 图片压缩";
        ExtendsContentIntoTitleBar = false;
        var dpi = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        var scale = (dpi == 0 ? 96 : dpi) / 96d;
        AppWindow.Resize(new SizeInt32((int)(760 * scale), (int)(640 * scale)));
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(area.X + Math.Max(0, (area.Width - AppWindow.Size.Width) / 2), area.Y + Math.Max(0, (area.Height - AppWindow.Size.Height) / 2)));
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
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = inactiveForeground;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = hoverBackground;
        // 悬停/按下时系统默认前景色会与自定义底色冲突，必须显式给出对比色。
        AppWindow.TitleBar.ButtonHoverForegroundColor = foreground;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = pressedBackground;
        AppWindow.TitleBar.ButtonPressedForegroundColor = foreground;
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
        try
        {
            if (!Directory.Exists(ViewModel.OpenOutputPath)) { ViewModel.Message = "输出目录当前不可访问，请检查共享连接。"; return; }
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            start.ArgumentList.Add(ViewModel.OpenOutputPath);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex) { ViewModel.Message = ex.Message; }
    }
    private async void Failures_Click(object sender, RoutedEventArgs e) => await Safe(async () =>
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "未完成的图片", CloseButtonText = "关闭",
            Content = new ScrollViewer { MaxHeight = 350, Content = new TextBlock { Text = string.Join("\n\n", ViewModel.Failures), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }
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
