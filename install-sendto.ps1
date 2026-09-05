#Requires -Version 5.1
<#
.SYNOPSIS
    把"压缩图片(NAS/PC)"装进资源管理器右键菜单（HKCU 注册表，无需管理员权限），或卸载。

.DESCRIPTION
    安装时用 .NET 编译器生成一个无窗口(GUI 子系统)启动器 imgzip-launcher.exe，
    右键菜单命令指向该 exe：它静默拉起 PowerShell（CreateNoWindow），
    因此全程无黑色控制台、无闪烁、不占任务栏。
    路径经 explorer -> launcher.exe -> powershell 全程 Unicode 传递，韩文等字符无损。
    覆盖三个入口：右键文件夹图标 / 右键文件 / 文件夹空白处(=压缩当前目录)。
    安装时会清理旧版"发送到"残留(.lnk/.bat/.vbs)。

.EXAMPLE
    .\install-sendto.ps1            # 安装
    .\install-sendto.ps1 -Uninstall # 卸载
#>
[CmdletBinding()]
param([switch]$Uninstall)

$ErrorActionPreference = 'Stop'
$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$target   = Join-Path $here 'imgzip-sendto.ps1'
$exePath  = Join-Path $here 'imgzip-launcher.exe'
if (-not (Test-Path -LiteralPath $target)) { Write-Host '错误: 找不到 imgzip-sendto.ps1'; exit 1 }

$menuText = '压缩图片（NAS/PC）'
$keyName  = 'ImgZipCompress'
$entries = @(
    @{ Path = 'Software\Classes\Directory\shell\ImgZipCompress';           Arg = '%1' }, # 右键文件夹图标
    @{ Path = 'Software\Classes\*\shell\ImgZipCompress';                   Arg = '%1' }, # 右键任意文件
    @{ Path = 'Software\Classes\Directory\Background\shell\ImgZipCompress'; Arg = '%V' } # 文件夹空白处右键=压缩当前目录
)
$icon = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe,0"

function Clear-SendToLeftovers {
    $sendToDir = [Environment]::GetFolderPath('SendTo')
    foreach ($name in @('压缩到NAS（JPEG收藏档）.lnk','压缩到NAS（JPEG收藏档）.bat','压缩到NAS（JPEG收藏档）.vbs')) {
        $p = Join-Path $sendToDir $name
        if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Force; Write-Host "已清理旧发送到残留: $name" }
    }
}

function New-Launcher {
    if (Test-Path -LiteralPath $exePath) { Remove-Item -LiteralPath $exePath -Force }
    $csharp = @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;

static class ImgZipLauncher
{
    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string script = Path.Combine(exeDir, "imgzip-sendto.ps1");
            string powershell = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");

            var sb = new StringBuilder();
            sb.Append("-NoProfile -ExecutionPolicy Bypass -File ");
            sb.Append(Quote(script));
            foreach (string a in args)
            {
                sb.Append(' ');
                sb.Append(Quote(a));
            }

            var psi = new ProcessStartInfo(powershell, sb.ToString())
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            using (Process p = Process.Start(psi)) { }
        }
        catch { }
    }

    static string Quote(string s)
    {
        return "\"" + s.Replace("\"", "\\\"") + "\"";
    }
}
'@
    Add-Type -TypeDefinition $csharp -OutputAssembly $exePath -OutputType WindowsApplication
    Write-Host "已生成无窗口启动器: $exePath"
}

if ($Uninstall) {
    foreach ($e in $entries) {
        try {
            [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($e.Path, $false)
            Write-Host "已移除注册表项: HKCU\$($e.Path)"
        } catch {
            Write-Host "跳过(不存在): HKCU\$($e.Path)"
        }
    }
    if (Test-Path -LiteralPath $exePath) {
        Remove-Item -LiteralPath $exePath -Force
        Write-Host "已删除启动器: $exePath"
    }
    Clear-SendToLeftovers
    Write-Host '卸载完成。'
    exit 0
}

Clear-SendToLeftovers
New-Launcher
foreach ($e in $entries) {
    $command = '"' + $exePath + '" -Path "' + $e.Arg + '" -Silent'
    $k = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($e.Path)
    $k.SetValue('', $menuText)
    $k.SetValue('Icon', $icon)
    $c = $k.CreateSubKey('command')
    $c.SetValue('', $command)
    $c.Close()
    $k.Close()
    Write-Host "已写入: HKCU\$($e.Path)"
}

Write-Host ''
Write-Host "已安装到右键菜单: $menuText"
Write-Host '用法: 在资源管理器中右键 本地文件夹 或 NAS 共享下的文件夹/文件 -> 压缩图片（NAS/PC）'
Write-Host '提示: Win11 需先点"显示更多选项"。'
