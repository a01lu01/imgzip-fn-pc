#Requires -Version 5.1
<#
.SYNOPSIS
    右键"压缩图片"入口：识别路径归属(本地/映射盘/UNC/NAS)并组装双引擎调用。

.DESCRIPTION
    由注册表右键菜单经 imgzip-launcher.exe 以 Unicode 启动(韩文等字符无损)。
    自动判断路径：本地盘 / 映射盘符(还原为 UNC) / UNC 共享，并识别是否属于本 NAS
    (host/ip 见 local-config.json 的 nasHosts/nasIp)。NAS 路径携带 NAS 真实路径(SSH 翻译)与 Windows UNC，
    交给 imgzip-remote.ps1 的单一窗体选择 NAS/PC 引擎；本地/异机共享只给 PC 引擎。

.PARAMETER Path
    文件夹/文件路径，由右键菜单的 %1 传入。
.PARAMETER Preset
    压缩预设，默认 archive-jpeg（长边4000 + q90 + JPEG + 不放大小图）。
.EXAMPLE
    .\imgzip-sendto.ps1 -Path '\\SERVER\SHARE\写真'
#>
[CmdletBinding()]
param(
    [string]$Path,
    [ValidateSet('none','archive-jpeg','archive-webp','webp-share')]
    [string]$Preset = 'archive-jpeg',
    [switch]$Yes,
    [switch]$DryRun,
    [switch]$Silent,
    [string]$User   = '',
    [string]$KeyPath = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms | Out-Null

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$logFile    = Join-Path $scriptRoot 'sendto-log.txt'
$mainScript = Join-Path $scriptRoot 'imgzip-remote.ps1'
$mapFile    = Join-Path $scriptRoot 'smb-map.json'
if (-not $KeyPath) { $KeyPath = Join-Path $env:USERPROFILE '.ssh\id_ed25519_nas' }

# 读取本机 local-config.json(不随仓库上传)：用户/主机/NAS 识别信息
$cfgFile = Join-Path $scriptRoot 'local-config.json'
$cfg = $null
if (Test-Path -LiteralPath $cfgFile) {
    try { $cfg = Get-Content -LiteralPath $cfgFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $cfg = $null }
}
$NasHosts = @()
$NasIp    = ''
if ($cfg) {
    if (-not $PSBoundParameters.ContainsKey('User') -and $cfg.user) { $User = $cfg.user }
    if (-not $PSBoundParameters.ContainsKey('KeyPath') -and $cfg.keyPath) { $KeyPath = $cfg.keyPath }
    if ($cfg.nasHosts) { $NasHosts = @($cfg.nasHosts) }
    if ($cfg.nasIp)    { $NasIp = $cfg.nasIp }
}

function Write-Log {
    param([string]$Message)
    try { Add-Content -LiteralPath $logFile -Value ('[{0}] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message) -Encoding UTF8 } catch { }
}

trap {
    $msg = $_.Exception.Message
    Write-Log ('错误: ' + $msg)
    try { Show-Msg ("发生错误:`n" + $msg) -Error } catch { }
    exit 1
}

function Show-Msg {
    param([string]$Text, [string]$Title = 'ImgZip', [switch]$Error)
    [System.Windows.Forms.MessageBox]::Show(
        $Text, $Title,
        [System.Windows.Forms.MessageBoxButtons]::OK,
        $(if ($Error) { [System.Windows.Forms.MessageBoxIcon]::Error } else { [System.Windows.Forms.MessageBoxIcon]::Information })
    ) | Out-Null
}

function Select-Folder {
    param([string]$InitialPath = '')
    $d = New-Object System.Windows.Forms.FolderBrowserDialog
    $d.Description = '请选择要压缩的文件夹（可通过"网络"进入共享）'
    $d.ShowNewFolderButton = $false
    if ($InitialPath) { try { $d.SelectedPath = $InitialPath } catch { } }
    if ($d.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $d.SelectedPath }
    return $null
}

function Ask-Repick {
    param([string]$Message)
    $ans = [System.Windows.Forms.MessageBox]::Show(
        ($Message + "`n`n[是] 重新选择文件夹   [否] 取消"),
        'ImgZip', [System.Windows.Forms.MessageBoxButtons]::YesNo,
        [System.Windows.Forms.MessageBoxIcon]::Warning
    )
    if ($ans -ne [System.Windows.Forms.DialogResult]::Yes) { return $null }
    return Select-Folder
}

function Invoke-NasBash {
    param([string]$Server, [string]$RemoteCmd)
    $cmd = $RemoteCmd -replace "`r", ""
    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($cmd))
    $out = @(& ssh -o BatchMode=yes -o ConnectTimeout=8 -o StrictHostKeyChecking=accept-new `
          -o IdentitiesOnly=yes -i $KeyPath "$User@$Server" "echo $b64 | base64 -d | bash" 2>$null)
    return @{ Code = $LASTEXITCODE; Output = $out }
}

function Get-ShareRoot {
    param([string]$Server, [string]$Share)
    $bash = @'
SHARE="__SHARE__"
P=$(awk -v sec="[$SHARE]" 'tolower($0)==tolower(sec){f=1;next} f&&/^\[/{exit} f&&tolower($1)=="path"{sub(/^[^=]*=[ ]*/,"");print;exit}' /etc/samba/users/*.share.conf /etc/samba/smb.conf /etc/samba/smb.custom.conf 2>/dev/null)
if [ -z "$P" ]; then
  P=$(testparm -s --parameter-name=path --section-name="$SHARE" 2>/dev/null | head -n 1)
fi
if [ -z "$P" ]; then
  P=$(testparm -s 2>/dev/null | awk -v sec="[$SHARE]" 'tolower($0)==tolower(sec){f=1;next} f&&tolower($1)=="path"{sub(/^[^=]*=[ ]*/,"");print;exit}')
fi
if [ -z "$P" ]; then echo "SHARE_NOT_FOUND"; exit 0; fi
echo "$P"
'@
    $bash = $bash.Replace('__SHARE__', $Share)
    $r = Invoke-NasBash -Server $Server -RemoteCmd $bash
    if ($r.Code -ne 0) { return $null }
    $line = @($r.Output) | Where-Object { $_ -and $_.Trim() } | Select-Object -First 1
    if (-not $line -or $line.Trim() -eq 'SHARE_NOT_FOUND') { return $null }
    return $line.Trim()
}

function Get-NasPathType {
    param([string]$Server, [string]$NasPath)
    $bash = @'
P="__PATH__"
if [ -d "$P" ]; then echo "DIR"
elif [ -f "$P" ]; then echo "FILE"
else echo "MISSING"
fi
'@
    $bash = $bash.Replace('__PATH__', $NasPath)
    $r = Invoke-NasBash -Server $Server -RemoteCmd $bash
    if ($r.Code -ne 0) { return 'UNKNOWN' }
    return (@($r.Output) | Where-Object { $_ -match '^(DIR|FILE|MISSING)$' } | Select-Object -First 1)
}

function Load-Map {
    if (Test-Path -LiteralPath $mapFile) {
        try { return Get-Content -LiteralPath $mapFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
    }
    return $null
}

function Save-MapEntry {
    param([string]$Key, [string]$Root)
    $map = @{}
    $old = Load-Map
    if ($old) { foreach ($p in $old.PSObject.Properties) { $map[$p.Name] = $p.Value } }
    $map[$Key] = $Root
    $map | ConvertTo-Json | Set-Content -LiteralPath $mapFile -Encoding UTF8
}

function Get-WinDir {
    param([string]$WinPath, [bool]$IsFile)
    if ($IsFile) { return Split-Path -Parent $WinPath }
    return $WinPath
}

function Test-NasUnc {
    param([string]$Unc)
    $srv = $Unc.TrimStart('\').Split('\')[0]
    if ($NasHosts -contains $srv) { return $true }
    if ($srv -eq $NasIp) { return $true }
    try {
        $ips = [System.Net.Dns]::GetHostAddresses($srv) | Where-Object { $_.AddressFamily -eq 'InterNetwork' }
        if ($ips | Where-Object { $_.IPAddressToString -eq $NasIp }) { return $true }
    } catch { }
    return $false
}

function Resolve-Drive {
    param([string]$P)
    if ($P -notmatch '^[a-zA-Z]:\\') { return $P }
    $drive = $P.Substring(0, 2)
    try {
        $ld = Get-CimInstance -ClassName Win32_LogicalDisk -Filter ("DeviceID='{0}'" -f $drive)
        if ($ld -and $ld.DriveType -eq 4 -and $ld.ProviderName) {
            $rel = $P.Substring(2).TrimStart('\')
            $unc = ($ld.ProviderName.TrimEnd('\') + '\' + $rel)
            Write-Log ("映射盘 $P -> $unc")
            return $unc
        }
    } catch { }
    return $P
}

function Get-WinIsFile {
    param([string]$P)
    try { if (Test-Path -LiteralPath $P -PathType Leaf) { return $true } } catch { }
    return $false
}

# ---------- 主流程 ----------
Write-Log ("==== 启动 Path=[" + $Path + "] Preset=" + $Preset + " Silent=" + $Silent)

# 路径健康检查: 为空或含'?'(损坏特征) -> 文件夹选择框兜底
if (-not $Path -or $Path.Contains('?')) {
    if ($Path) { Show-Msg ("传入的路径可能含无法识别的字符：`n" + $Path + "`n`n请手动选择目标文件夹。") }
    $Path = Select-Folder
    if (-not $Path) { Write-Log '用户取消选择'; exit 0 }
}

# 映射盘符还原为 UNC
$Path = Resolve-Drive -P $Path

$isNas   = $false
$isFile  = $false
if ($Path -like '\\*') {
    $isNas  = Test-NasUnc -Unc $Path
    $isFile = Get-WinIsFile -P $Path
} else {
    # 本地路径
    $isFile = Get-WinIsFile -P $Path
}
Write-Log ("检测: isNas=" + $isNas + " isFile=" + $isFile + " path=" + $Path)

if ($isNas) {
    # ---- NAS 路径: 翻译真实路径(供 NAS 引擎) ----
    $resolved = $null
    $attempt  = 0
    $nasIsFile = $isFile
    while (-not $resolved) {
        $attempt++
        if ($attempt -gt 3) { Show-Msg '尝试次数过多，已取消。' -Error; exit 1 }

        $parts  = $Path.TrimEnd('\').TrimStart('\').Split('\')
        $server = $parts[0]
        $share  = $parts[1]
        $sub    = ''
        if ($parts.Count -gt 2) { $sub = ($parts[2..($parts.Count - 1)] -join '/') }

        $ip = $server
        if ($server -notmatch '^\d{1,3}(\.\d{1,3}){3}$') {
            try {
                $addr = [System.Net.Dns]::GetHostAddresses($server) | Where-Object { $_.AddressFamily -eq 'InterNetwork' } | Select-Object -First 1
                if ($addr) { $ip = $addr.IPAddressToString }
            } catch { }
        }

        $key  = '\\' + $ip + '\' + $share
        $root = $null
        $map  = Load-Map
        if ($map -and ($map.PSObject.Properties.Name -contains $key)) { $root = $map.$key }
        if (-not $root) {
            $root = Get-ShareRoot -Server $ip -Share $share
            if ($root) { Save-MapEntry -Key $key -Root $root }
        }
        if (-not $root) {
            Write-Log ('共享解析失败 share=' + $share)
            $newP = Ask-Repick ("无法解析共享 '$share' 的真实路径。`n`n请确认 NAS 可 SSH 免密连接且共享存在。")
            if (-not $newP) { exit 0 }
            $Path = Resolve-Drive -P $newP
            continue
        }

        $nasPath = $root.TrimEnd('/') + $(if ($sub) { '/' + $sub } else { '' })
        if ($isFile -and -not $nasIsFile) { }  # 已从 Windows 侧判定文件
        $type = Get-NasPathType -Server $ip -NasPath $nasPath
        Write-Log ('NAS 预检 ' + $nasPath + ' -> ' + $type)
        if ($type -eq 'DIR' -or $type -eq 'FILE') {
            $nasIsFile = ($type -eq 'FILE')
            $nasSource = $nasPath.TrimEnd('/')
            $nasDir = if ($nasIsFile) {
                $idx = $nasSource.LastIndexOf('/')
                if ($idx -gt 0) { $nasSource.Substring(0, $idx) } else { $nasSource }
            } else { $nasSource }
            $resolved = @{
                Ip = $ip; NasSource = $nasSource; NasOutput = $nasDir.TrimEnd('/') + '_compressed'; IsFile = $nasIsFile
            }
        } else {
            $newP = Ask-Repick ("NAS 上找不到目标：`n" + $nasPath + "`n`n路径可能被翻译错或目录不存在。")
            if (-not $newP) { exit 0 }
            $Path = Resolve-Drive -P $newP
        }
    }

    $winDir  = Get-WinDir -WinPath $Path -IsFile $resolved.IsFile
    $winOut  = $winDir.TrimEnd('\') + '_compressed'
    $remoteArgs = @{
        Server    = $resolved.Ip
        User      = $User
        KeyPath   = $KeyPath
        Source    = $resolved.NasSource
        Output    = $resolved.NasOutput
        Preset    = $Preset
        Silent    = $Silent
        WinSource = $Path
        WinOutput = $winOut
    }
    if (-not $Silent) {
        if ($DryRun) { $remoteArgs.DryRun = $true }
        elseif (-not $Yes) {
            $presetDesc = switch ($Preset) {
                'archive-jpeg' { 'JPEG q90 · 长边4000 · 不放大小图（收藏档）' }
                'archive-webp' { 'WebP q90 · 长边4000 · 不放大小图' }
                'webp-share'   { 'WebP q80 · 长边2560 · 不放大小图（分享档）' }
                default        { '自定义参数' }
            }
            $msg = "确认压缩以下 NAS 目录？`n`n源目录: $($resolved.NasSource)`n输出到: $($resolved.NasOutput)`n参数: $presetDesc"
            $choice = [System.Windows.Forms.MessageBox]::Show($msg, 'ImgZip 压缩图片', [System.Windows.Forms.MessageBoxButtons]::YesNoCancel, [System.Windows.Forms.MessageBoxIcon]::Question)
            if ($choice -eq [System.Windows.Forms.DialogResult]::Cancel) { exit 0 }
            if ($choice -eq [System.Windows.Forms.DialogResult]::No) { $remoteArgs.DryRun = $true }
        }
    }
    Write-Log ('开始执行 remote(NAS): ' + $resolved.NasSource)
    & $mainScript @remoteArgs
    exit $LASTEXITCODE
}

# ---- 本地 / 异机共享: 仅 PC 引擎 ----
if (-not (Test-Path -LiteralPath $Path)) {
    Show-Msg "路径不存在或当前无权访问：`n$Path" -Error
    exit 1
}
$winDir  = Get-WinDir -WinPath $Path -IsFile $isFile
$winOut  = $winDir.TrimEnd('\') + '_compressed'
$remoteArgs = @{
    User      = $User
    KeyPath   = $KeyPath
    Preset    = $Preset
    Silent    = $Silent
    WinSource = $Path
    WinOutput = $winOut
}
if ($DryRun) { $remoteArgs.DryRun = $true }
if (-not $Silent) { $remoteArgs.Local = $true }
Write-Log ('开始执行 remote(PC): ' + $Path)
& $mainScript @remoteArgs
exit $LASTEXITCODE
