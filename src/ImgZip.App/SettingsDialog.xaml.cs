using ImgZip.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace ImgZip.App;
public sealed partial class SettingsDialog : ContentDialog
{
    private readonly MainViewModel model;
    private readonly IntPtr windowHandle;
    public SettingsDialog(MainViewModel model, IntPtr windowHandle)
    {
        this.model = model; this.windowHandle = windowHandle;
        InitializeComponent(); Fill(model.Config);
    }
    private void Fill(AppConfig config)
    {
        ServerField.Text = config.Server; UserField.Text = config.User; KeyField.Text = config.KeyPath;
        PortField.Value = config.Port; HostsField.Text = string.Join(", ", config.NasHosts); IpField.Text = config.NasIp;
        ConcurrencyField.Value = config.PcConcurrency;
    }
    private AppConfig Read()
    {
        if (!double.IsFinite(PortField.Value) || PortField.Value != Math.Truncate(PortField.Value) || PortField.Value is < 1 or > 65535) throw new ArgumentException("SSH 端口需为 1–65535 的整数。");
        if (!double.IsFinite(ConcurrencyField.Value) || ConcurrencyField.Value != Math.Truncate(ConcurrencyField.Value) || ConcurrencyField.Value is < 0 or > 64) throw new ArgumentException("PC 并行任务数需为 0–64 的整数（0 = 自动）。");
        return new AppConfig
        {
            Server = ServerField.Text, User = UserField.Text, Port = (int)PortField.Value, KeyPath = KeyField.Text,
            NasIp = IpField.Text, NasHosts = HostsField.Text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Theme = model.Config.Theme,
            PcConcurrency = (int)ConcurrencyField.Value
        }.Normalize();
    }
    private void Info(string message, InfoBarSeverity severity)
    { ConnectionInfo.Message = message; ConnectionInfo.Severity = severity; ConnectionInfo.IsOpen = true; }
    private async void Save_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try { await model.SaveConnectionAsync(Read()); }
        catch (Exception ex) { args.Cancel = true; Info(ex.Message, InfoBarSeverity.Error); }
        finally { deferral.Complete(); }
    }
    private async void Test_Click(object sender, RoutedEventArgs args)
    {
        TestButton.IsEnabled = false;
        try
        {
            Info("正在连接…", InfoBarSeverity.Informational);
            var result = await model.TestConnectionAsync(Read());
            Info(result.NasAvailable ? "SSH 连接正常，NAS 引擎就绪。当前输入尚未保存。" : result.Connected ? "SSH 已连接，但 NAS 引擎或必要工具缺失。" : "连接失败：" + result.Message, result.NasAvailable ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        catch (Exception ex) { Info(ex.Message, InfoBarSeverity.Error); }
        finally { TestButton.IsEnabled = true; }
    }
    private async void Import_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".json"); WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            Fill(await ConfigStore.ImportAsync(file.Path));
            Info("已载入旧配置，点击保存后生效。原文件保持不变。", InfoBarSeverity.Informational);
        }
        catch (Exception ex) { Info(ex.Message, InfoBarSeverity.Error); }
    }
}
