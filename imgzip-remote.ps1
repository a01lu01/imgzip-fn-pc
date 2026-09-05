#Requires -Version 5.1
<#
.SYNOPSIS
    图片压缩执行器：可在 NAS(SSH 遥控) 或本机 PC 上压缩图片。

.DESCRIPTION
    双引擎：NAS 引擎通过 SSH 在 fnOS 上执行 caesiumclt；PC 引擎调用本工程
    bin\caesiumclt.exe 直接压缩 Windows 可见路径(本地或 SMB)。支持保留目录结构、
    保留 EXIF/文件时间、多线程、无损/有损、格式转换、长边/短边/固定宽高缩放、
    目标体积、仅本文件夹/递归、干跑预演，并自动统计压缩前后的体积变化。

    NAS 引擎前提：Windows 与 NAS 已配置 SSH 免密（公钥认证）。

.PARAMETER Server
    NAS 的 IP 或主机名（默认读取 local-config.json；亦可用本参数覆盖）
.PARAMETER User
    NAS 的 SSH 用户名（默认读取 local-config.json；亦可用本参数覆盖）
.PARAMETER Port
    SSH 端口，默认 22
.PARAMETER KeyPath
    私钥路径，默认 ~/.ssh/id_ed25519_nas
.PARAMETER Source
    NAS 引擎的源目录(NAS 真实路径，如 /vol1/photo/写真)；-Local 时为本机路径
.PARAMETER Output
    输出目录，默认 <源目录>_compressed
.PARAMETER Format
    输出格式：keep(默认) / webp / jpeg / png
.PARAMETER Quality
    有损质量 0-100，默认 82
.PARAMETER Threads
    并行线程数，N5105 为 4 核，默认 4
.PARAMETER LongEdge
    大于 0 时把图片长边缩放到该像素
.PARAMETER Width
    大于 0 时把图片宽度缩放到该像素（保持比例）
.PARAMETER Height
    大于 0 时把图片高度缩放到该像素（保持比例）
.PARAMETER ShortEdge
    大于 0 时把图片短边缩放到该像素
.PARAMETER MaxSize
    大于 0 时按目标文件体积压缩（字节），如 512000 = 500KB
.PARAMETER NoUpscale
    缩放时不放大小图（小于目标尺寸的照片保持原样）
.PARAMETER Recurse
    目录压缩时是否递归进入子文件夹（默认不递归，仅处理本文件夹内图片；界面可用复选框切换）
.PARAMETER WinSource
    Windows UNC 源路径（供静默窗体按 SMB 统计进度；可选）
.PARAMETER WinOutput
    Windows UNC 输出目录（供静默窗体按 SMB 统计进度；可选）
.PARAMETER NasCapable
    路径属于本 NAS 时由 sendto 传入，界面默认给出 NAS 引擎（与 Source 配合）
.PARAMETER Local
    使用本机 PC 引擎（配合 -Source <Windows 路径>，或由 sendto 配合 -WinSource）
.PARAMETER Preset
    一键预设：archive-jpeg(长边4000+q90+JPEG,收藏推荐) / archive-webp(同参WebP) / webp-share(长边2560+q80+WebP)
.PARAMETER Silent
    静默模式(供右键菜单调用): 不依赖控制台输出, 结束时用弹窗显示结果摘要
.PARAMETER Lossless
    使用无损压缩模式
.PARAMETER DryRun
    只预演不写文件，先看能省多少
.PARAMETER Setup
    在 NAS 上安装 caesiumclt（NAS 需要能访问 GitHub 下载）
.PARAMETER Test
    测试与 NAS 的连接、用户和 caesiumclt 是否就绪
.PARAMETER Force
    允许输出目录等于源目录（有覆盖风险，慎用）

.EXAMPLE
    .\imgzip-remote.ps1 -Test

.EXAMPLE
    .\imgzip-remote.ps1 -Setup

.EXAMPLE
    .\imgzip-remote.ps1 -Source /vol1/photo/写真 -Quality 82 -DryRun

.EXAMPLE
    .\imgzip-remote.ps1 -Source /vol1/photo/写真 -Output /vol1/photo/写真_webp -Format webp -Quality 80
#>
[CmdletBinding()]
param(
    [string]$Server  = '',
    [string]$User    = '',
    [int]$Port       = 22,
    [string]$KeyPath = (Join-Path $env:USERPROFILE '.ssh\id_ed25519_nas'),

    [string]$Source,
    [string]$Output,
    [ValidateSet('keep','webp','jpeg','png')]
    [string]$Format = 'keep',
    [int]$Quality   = 82,
    [int]$Threads   = 4,
    [int]$LongEdge  = 0,
    [int]$Width     = 0,
    [int]$Height    = 0,
    [int]$ShortEdge = 0,
    [long]$MaxSize  = 0,
    [switch]$Lossless,
    [switch]$NoUpscale,
    [ValidateSet('none','archive-jpeg','archive-webp','webp-share')]
    [string]$Preset  = 'none',
    [switch]$DryRun,
    [switch]$Setup,
    [switch]$Test,
    [switch]$Force,
    [switch]$Recurse,
    [switch]$NasCapable,
    [switch]$Local,
    [switch]$Silent,
    [string]$WinSource = '',
    [string]$WinOutput = ''
)

switch ($Preset) {
    'archive-jpeg' { if (-not $PSBoundParameters.ContainsKey('Format'))   { $Format   = 'jpeg' }
                     if (-not $PSBoundParameters.ContainsKey('Quality'))  { $Quality  = 90 }
                     if (-not $PSBoundParameters.ContainsKey('LongEdge')) { $LongEdge = 4000 }
                     $NoUpscale = $true }
    'archive-webp' { if (-not $PSBoundParameters.ContainsKey('Format'))   { $Format   = 'webp' }
                     if (-not $PSBoundParameters.ContainsKey('Quality'))  { $Quality  = 90 }
                     if (-not $PSBoundParameters.ContainsKey('LongEdge')) { $LongEdge = 4000 }
                     $NoUpscale = $true }
    'webp-share'   { if (-not $PSBoundParameters.ContainsKey('Format'))   { $Format   = 'webp' }
                     if (-not $PSBoundParameters.ContainsKey('Quality'))  { $Quality  = 80 }
                     if (-not $PSBoundParameters.ContainsKey('LongEdge')) { $LongEdge = 2560 }
                     $NoUpscale = $true }
}

# 读取本机 local-config.json(不随仓库上传) 提供默认连接信息
$cfgFile = Join-Path $PSScriptRoot 'local-config.json'
$cfg = $null
if (Test-Path -LiteralPath $cfgFile) {
    try { $cfg = Get-Content -LiteralPath $cfgFile -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $cfg = $null }
}
if ($cfg) {
    if (-not $PSBoundParameters.ContainsKey('Server')  -and $cfg.server)  { $Server  = $cfg.server }
    if (-not $PSBoundParameters.ContainsKey('User')    -and $cfg.user)    { $User    = $cfg.user }
    if (-not $PSBoundParameters.ContainsKey('KeyPath') -and $cfg.keyPath) { $KeyPath = $cfg.keyPath }
}
$ErrorActionPreference = 'Continue'

# 远程输出是 UTF-8, 强制控制台按 UTF-8 解码, 否则中文变乱码
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

$ReleaseUrl = 'https://github.com/Lymphatus/caesium-clt/releases/download/v1.4.0/caesiumclt-v1.4.0-x86_64-unknown-linux-musl.tar.gz'
$localExe  = Join-Path $PSScriptRoot 'bin\caesiumclt.exe'

function Format-Bytes {
    param([double]$bytes)
    if ($bytes -ge 1GB) { return '{0:N1} GB' -f ($bytes / 1GB) }
    if ($bytes -ge 1MB) { return '{0:N1} MB' -f ($bytes / 1MB) }
    return '{0:N1} KB' -f ($bytes / 1KB)
}

function Invoke-Ssh {
    param([Parameter(Mandatory = $true)][string]$RemoteCmd)
    # 远程脚本经 base64 编码传输, 规避 PowerShell->ssh 多层引号剥离导致 bash 语法损坏
    $sshArgs = @(
        '-o', 'BatchMode=yes',
        '-o', 'ConnectTimeout=10',
        '-o', 'StrictHostKeyChecking=accept-new',
        '-o', 'IdentitiesOnly=yes'
    )
    if ($Port -ne 22) { $sshArgs += @("-p", "$Port") }
    $sshArgs += @("-i", $KeyPath, "$User@$Server")
    $clean = $RemoteCmd -replace "`r", ""
    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($clean))
    # 实时回显远程输出（下载/压缩不再黑盒），同时收集以便解析报告行
    $output = @( & ssh @sshArgs ("echo {0} | base64 -d | bash" -f $b64) 2>&1 | ForEach-Object { $line = "$_"; Write-Host $line; $line } )
    return ,@{ Code = $LASTEXITCODE; Output = $output }
}

function Show-Summary {
    param([string]$Text)
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [System.Windows.Forms.MessageBox]::Show($Text, 'ImgZip', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    } catch { Write-Host $Text }
}

function Show-Lines {
    param([object[]]$Lines)
    foreach ($l in $Lines) { Write-Host $l }
}

function Out-Help {
    Write-Host '用法: .\imgzip-remote.ps1 <参数>'
    Write-Host ''
    Write-Host '  -Test                测试与 NAS 的连接'
    Write-Host '  -Setup               在 NAS 上安装 caesiumclt'
    Write-Host '  -Source <目录>       源目录(NAS路径)，如 /vol1/photo/写真'
    Write-Host '  -Output <目录>       输出目录，默认 <源目录>_compressed'
    Write-Host '  -Quality <0-100>     质量，默认 82'
    Write-Host '  -Format <keep/webp/jpeg/png>  输出格式'
    Write-Host '  -Threads <N>         线程数，默认 4'
    Write-Host '  -LongEdge <px>       长边缩放（0=不缩放）'
    Write-Host '  -Width <px>           固定宽度缩放（0=不缩放）'
    Write-Host '  -Height <px>          固定高度缩放（0=不缩放）'
    Write-Host '  -ShortEdge <px>       短边缩放（0=不缩放）'
    Write-Host '  -MaxSize <bytes>      目标最大文件体积，如 512000=500KB'
    Write-Host '  -Preset <名称>        预设: archive-jpeg / archive-webp / webp-share'
    Write-Host '  -NoUpscale            缩放时不放大小于目标的图片'
    Write-Host '  -Lossless            无损模式'
    Write-Host '  -DryRun              只预演不写文件'
    Write-Host '  -Local               使用本机 PC 引擎(配 -Source <Windows路径>)'
    Write-Host '  -Server/-User/-Port/-KeyPath  连接参数(默认读 local-config.json)'
    Write-Host ''
    Write-Host '示例: .\imgzip-remote.ps1 -Source /vol1/photo/写真 -Quality 82 -DryRun'
    Write-Host '示例: .\imgzip-remote.ps1 -Local -Source D:\photo\写真 -DryRun'
}

function New-CaesiumArgs {
    # 按预设/参数生成与 NAS bash 等价的 caesiumclt 参数(不含 -o/源/输出)
    param(
        [string]$SourcePath,
        [bool]$Recurse,
        [string]$Format,
        [int]$Quality,
        [bool]$Lossless,
        [int]$LongEdge,
        [int]$Width,
        [int]$Height,
        [int]$ShortEdge,
        [long]$MaxSize,
        [bool]$NoUpscale,
        [int]$Threads
    )
    $a = @('-e', '--keep-dates', '--threads', "$Threads")
    if ($Format -eq 'jpeg') {
        # caesiumclt 不允许 jpeg->jpeg 同格式转换, 只有存在非 jpeg 源时才加 --format jpeg
        $need = $false
        if (Test-Path -LiteralPath $SourcePath -PathType Leaf) {
            $need = ($SourcePath -match '(?i)\.(png|webp|gif|bmp|tif|tiff|heic)$')
        } elseif (Test-Path -LiteralPath $SourcePath -PathType Container) {
            $opt = $(if ($Recurse) { [System.IO.SearchOption]::AllDirectories } else { [System.IO.SearchOption]::TopDirectoryOnly })
            foreach ($f in [System.IO.Directory]::EnumerateFiles($SourcePath, '*', $opt)) {
                if ($f -match '(?i)\.(png|webp|gif|bmp|tif|tiff|heic)$') { $need = $true; break }
            }
        }
        if ($need) { $a += '--format', 'jpeg' }
    } elseif ($Format -ne 'keep') {
        $a += '--format', $Format
    }
    if ($Lossless) { $a += '--lossless' } else { $a += '-q', "$Quality" }
    if ($LongEdge -gt 0)  { $a += '--long-edge', "$LongEdge" }
    if ($Width -gt 0)     { $a += '--width', "$Width" }
    if ($Height -gt 0)    { $a += '--height', "$Height" }
    if ($ShortEdge -gt 0) { $a += '--short-edge', "$ShortEdge" }
    if ($MaxSize -gt 0)   { $a += '--max-size', "$MaxSize" }
    if ($NoUpscale)       { $a += '--no-upscale' }
    return ,$a
}

function Show-ConsoleReport {
    param([object[]]$Lines, [switch]$DryRun)
    $files = 0; $before = 0; $after = 0
    foreach ($l in $Lines) {
        if ($l -is [string]) {
            if ($l -match '^REPORT_FILES=(\d+)')   { $files  = [int]$Matches[1] }
            elseif ($l -match '^REPORT_BEFORE=(\d+)') { $before = [long]$Matches[1] }
            elseif ($l -match '^REPORT_AFTER=(\d+)')  { $after  = [long]$Matches[1] }
        }
    }
    if (-not $DryRun -and $before -gt 0) {
        Write-Host ''
        Write-Host '========== 压缩报告 =========='
        Write-Host ('文件数      : {0}' -f $files)
        Write-Host ('压缩前体积  : {0}' -f (Format-Bytes $before))
        Write-Host ('压缩后体积  : {0}' -f (Format-Bytes $after))
        $pct = [math]::Round((1 - $after / $before) * 100, 1)
        Write-Host ('节省空间    : {0} %' -f $pct)
        Write-Host '=============================='
    }
}

function Run-LocalCaesium {
    param(
        [string]$SourcePath,
        [string]$OutputPath,
        [string]$ArgText,
        [bool]$Recurse,
        [switch]$DryRun
    )
    $lines = @()
    if (-not (Test-Path -LiteralPath $SourcePath)) {
        Write-Output "SRC_MISSING: path not found: $SourcePath"
        return @{ Code = 3; Output = $lines }
    }
    if (-not (Test-Path -LiteralPath $OutputPath -PathType Container)) {
        try { New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null } catch { }
    }
    $a = @()
    foreach ($t in $ArgText.Split(' ')) { if ($t) { $a += $t } }
    $isDir = Test-Path -LiteralPath $SourcePath -PathType Container
    # 预检数量/体积(同时做空目录保护)
    $files = 0; $before = 0
    if ($isDir) {
        $opt0 = $(if ($Recurse) { [System.IO.SearchOption]::AllDirectories } else { [System.IO.SearchOption]::TopDirectoryOnly })
        foreach ($f in [System.IO.Directory]::EnumerateFiles($SourcePath, '*', $opt0)) {
            if ($f -match '(?i)\.(jpg|jpeg|png|webp|gif|bmp|heic)$') { $files++; $before += (Get-Item -LiteralPath $f).Length }
        }
    } else {
        $files = 1; $before = (Get-Item -LiteralPath $SourcePath).Length
    }
    if ($files -eq 0) {
        Write-Output 'NO_IMAGES: no compressible images in this scope'
        return @{ Code = 0; Output = @('NO_IMAGES: no compressible images in this scope') }
    }
    if ($isDir -and $Recurse) { $a += '-R'; $a += '-S' }
    if ($DryRun) { $a += '--dry-run' }
    $a += '-o'; $a += $OutputPath; $a += $SourcePath
    $head = ">>> Compressing: $SourcePath -> $OutputPath"
    Write-Host $head
    $lines = @(& $localExe @a 2>&1 | ForEach-Object { $line = "$_"; Write-Host $line; $line })
    $rc = $LASTEXITCODE
    if ($rc -ne 0) { return @{ Code = $rc; Output = $lines } }
    $joined = $lines -join "`n"
    if ($joined -match '\(0 success, [0-9]+ skipped, [1-9][0-9]* errors\)') {
        Write-Output 'COMPRESS_FAILED: caesiumclt failed for all files'
        return @{ Code = 4; Output = $lines }
    }
    if (-not $DryRun) {
        $exts = '\.(jpg|jpeg|png|webp|gif|bmp|heic)$'
        $after = 0
        if (Test-Path -LiteralPath $OutputPath -PathType Container) {
            foreach ($f in [System.IO.Directory]::EnumerateFiles($OutputPath, '*', [System.IO.SearchOption]::AllDirectories)) {
                if ($f -match "(?i)$exts") { $after += (Get-Item -LiteralPath $f).Length }
            }
        }
        $lines += "REPORT_FILES=$files"
        $lines += "REPORT_BEFORE=$before"
        $lines += "REPORT_AFTER=$after"
        Write-Output "REPORT_FILES=$files"
        Write-Output "REPORT_BEFORE=$before"
        Write-Output "REPORT_AFTER=$after"
    }
    Write-Output 'DONE'
    return @{ Code = 0; Output = $lines }
}

if ($Test) {
    Write-Host "==> 测试连接 $User@$Server ..."
    $bash = @'
set -e
echo "CONNECT_OK"
echo "USER=$(whoami) HOST=$(hostname)"
if [ -x "$HOME/bin/caesiumclt" ]; then
  "$HOME/bin/caesiumclt" --version
else
  echo "BIN_MISSING"
fi
'@
    $r = Invoke-Ssh $bash
    if ($r.Code -ne 0) { Write-Host "连接失败(exit=$($r.Code))，请检查 -Server/-User/-KeyPath。"; exit 1 }
    Write-Host ''
    Write-Host '==> 连接正常。'
    exit 0
}

if ($Setup) {
    Write-Host "==> 在 NAS 上安装 caesiumclt (v1.4.0) ..."
    $bash = @'
set -e
mkdir -p "$HOME/bin" "$HOME/tmp/caesium"
cd "$HOME/tmp/caesium"
curl -fSL --connect-timeout 15 --max-time 180 -o caesium.tgz "__URL__"
tar xzf caesium.tgz
BIN=$(find . -maxdepth 3 -type f -name 'caesiumclt*' | head -n 1)
if [ -z "$BIN" ]; then echo "SETUP_FAIL: 压缩包内未找到 caesiumclt"; exit 1; fi
chmod +x "$BIN"
cp "$BIN" "$HOME/bin/caesiumclt"
chmod +x "$HOME/bin/caesiumclt"
"$HOME/bin/caesiumclt" --version
echo "SETUP_OK"
'@
    $bash = $bash.Replace("__URL__", $ReleaseUrl)
    $r = Invoke-Ssh $bash
    if ($r.Code -ne 0) { Write-Host "安装失败(exit=$($r.Code))，请检查 NAS 是否能访问 GitHub。"; exit 1 }
    Write-Host ''
    Write-Host '==> caesiumclt 安装完成。'
    exit 0
}

if ($Source) {
    if (-not $Output) { $Output = $Source.TrimEnd('/') + '_compressed' }
    $srcNorm = $Source.TrimEnd('/')
    $outNorm = $Output.TrimEnd('/')
    if ($srcNorm -eq $outNorm -and -not $Force) {
        Write-Host '错误: 输出目录与源目录相同，有覆盖风险。请指定不同的 -Output，或加 -Force 强制（慎用）。'
        exit 1
    }

    $lossLine = ""
    $qLine    = ""
    if ($Lossless) { $lossLine = 'ARGS="$ARGS --lossless"' } else { $qLine = 'ARGS="$ARGS -q ' + $Quality + '"' }

    $bash = @'
set -e
if [ ! -x "$HOME/bin/caesiumclt" ]; then
  echo "BIN_MISSING: caesiumclt not installed. Run: imgzip-remote.ps1 -Setup"
  exit 2
fi
SRC="__SRC__"
if [ ! -e "$SRC" ]; then
  echo "SRC_MISSING: path not found: $SRC"
  exit 3
fi
DEPTH=""
if [ -d "$SRC" ] && [ "__RECURSE__" = "0" ]; then DEPTH="-maxdepth 1"; fi
FOUND=$(find "$SRC" $DEPTH -type f 2>/dev/null | grep -icE '\.(jpg|jpeg|png|webp|gif|bmp|heic)$' || true)
if [ -d "$SRC" ] && [ "$FOUND" -eq 0 ]; then
  echo "NO_IMAGES: no compressible images in this scope"
  echo "DONE"
  exit 0
fi
ARGS="-e --keep-dates --threads __THREADS__"
if [ "__FMT__" = "jpeg" ]; then
  # caesiumclt 不允许 jpeg->jpeg 同格式转换, 只有存在非 jpeg 源时才加 --format jpeg
  NEED_CONVERT=no
  if [ -d "$SRC" ]; then
    if find "$SRC" $DEPTH -type f 2>/dev/null | grep -iE '\.(png|webp|gif|bmp|tif|tiff|heic)$' | grep -qi .; then NEED_CONVERT=yes; fi
  else
    case "$SRC" in *.png|*.PNG|*.webp|*.WEBP|*.gif|*.GIF|*.bmp|*.BMP|*.tif|*.tiff|*.heic|*.HEIC) NEED_CONVERT=yes;; esac
  fi
  if [ "$NEED_CONVERT" = "yes" ]; then ARGS="$ARGS --format jpeg"; fi
elif [ "__FMT__" != "keep" ]; then
  ARGS="$ARGS --format __FMT__"
fi
__LOSSLESS__
__QARGS__
if [ "__LONGEDGE__" -gt 0 ] 2>/dev/null; then ARGS="$ARGS --long-edge __LONGEDGE__"; fi
if [ "__WIDTH__" -gt 0 ] 2>/dev/null; then ARGS="$ARGS --width __WIDTH__"; fi
if [ "__HEIGHT__" -gt 0 ] 2>/dev/null; then ARGS="$ARGS --height __HEIGHT__"; fi
if [ "__SHORTEDGE__" -gt 0 ] 2>/dev/null; then ARGS="$ARGS --short-edge __SHORTEDGE__"; fi
if [ "__MAXSIZE__" -gt 0 ] 2>/dev/null; then ARGS="$ARGS --max-size __MAXSIZE__"; fi
if [ "__NOUPSCALE__" = "1" ]; then ARGS="$ARGS --no-upscale"; fi
if [ "__DRYRUN__" = "1" ]; then ARGS="$ARGS --dry-run"; fi
mkdir -p "__OUT__"
echo ">>> Compressing: $SRC -> __OUT__"
if [ -d "$SRC" ]; then
  if [ "__RECURSE__" = "1" ]; then
    RUN_OUT=$("$HOME/bin/caesiumclt" $ARGS -R -S -o "__OUT__" "$SRC" 2>&1) && RUN_RC=0 || RUN_RC=$?
  else
    RUN_OUT=$("$HOME/bin/caesiumclt" $ARGS -o "__OUT__" "$SRC" 2>&1) && RUN_RC=0 || RUN_RC=$?
  fi
else
  RUN_OUT=$("$HOME/bin/caesiumclt" $ARGS -o "__OUT__" "$SRC" 2>&1) && RUN_RC=0 || RUN_RC=$?
fi
echo "$RUN_OUT"
if [ $RUN_RC -ne 0 ] || echo "$RUN_OUT" | grep -qE '\(0 success, [0-9]+ skipped, [1-9][0-9]* errors\)'; then
  echo "COMPRESS_FAILED: caesiumclt failed for all files (exit=$RUN_RC)"
  exit 4
fi
if [ "__DRYRUN__" = "0" ]; then
  BEFORE=$(find "$SRC" $DEPTH -type f -exec stat -c%s {} + 2>/dev/null | awk '{s+=$1} END{print s+0}')
  AFTER=$(du -sb "__OUT__" | cut -f1)
  FILES=$(find "$SRC" $DEPTH -type f | wc -l)
  echo "REPORT_FILES=$FILES"
  echo "REPORT_BEFORE=$BEFORE"
  echo "REPORT_AFTER=$AFTER"
fi
echo "DONE"
'@
    $bash = $bash.Replace("__SRC__", $Source).Replace("__OUT__", $Output).Replace("__FMT__", $Format).Replace("__THREADS__", "$Threads").Replace("__LONGEDGE__", "$LongEdge").Replace("__WIDTH__", "$Width").Replace("__HEIGHT__", "$Height").Replace("__SHORTEDGE__", "$ShortEdge").Replace("__MAXSIZE__", "$MaxSize")
    $bash = $bash.Replace("__NOUPSCALE__", $(if ($NoUpscale) { "1" } else { "0" })).Replace("__DRYRUN__", $(if ($DryRun) { "1" } else { "0" })).Replace("__LOSSLESS__", $lossLine).Replace("__QARGS__", $qLine)

}  # end NAS-prep (if $Source)

# ===== 引擎可用性与分发 =====
$nasEngineOk = [bool]$Source
if ($Local -and -not $WinSource) { $WinSource = $Source }
if ($Local -and -not $WinOutput) {
    if ($Output) { $WinOutput = $Output } else { $WinOutput = $Source.TrimEnd('\') + '_compressed' }
}
$pcEngineOk = $false
if ($WinSource) { $pcEngineOk = (Test-Path -LiteralPath $localExe -PathType Leaf) }

if ($Silent) {
        # 静默模式: 单一窗体完成 确认 -> 进度 -> 结果 三阶段
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        if (-not $nasEngineOk -and -not $pcEngineOk) {
            [System.Windows.Forms.MessageBox]::Show('没有可用的压缩引擎：未提供 NAS 路径且本机未找到 bin\caesiumclt.exe。', 'ImgZip', [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
            exit 1
        }
        $bashRun = ($bash -replace "`r", '').Replace('__DRYRUN__', '0')

        $form = New-Object System.Windows.Forms.Form
        $form.Text = 'ImgZip 压缩图片'
        $form.Size = New-Object System.Drawing.Size(520, 340)
        $form.StartPosition = 'CenterScreen'
        $form.TopMost = $true
        $form.MaximizeBox = $false
        $form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedSingle

        $presetDesc = switch ($Preset) {
            'archive-jpeg' { 'JPEG q90 · 长边4000 · 不放大小图' }
            'archive-webp' { 'WebP q90 · 长边4000 · 不放大小图' }
            'webp-share'   { 'WebP q80 · 长边2560 · 不放大小图' }
            default        { '自定义参数' }
        }

        $dispSrc = $(if ($nasEngineOk) { $Source } else { $WinSource })
        $dispOut = $(if ($nasEngineOk) { $Output } else { $WinOutput })
        $info = New-Object System.Windows.Forms.Label
        $info.Location = New-Object System.Drawing.Point(16, 8)
        $info.Size = New-Object System.Drawing.Size(472, 62)
        $info.Text = "源: $dispSrc`n输出: $dispOut`n参数: $presetDesc"

        $grpEngine = New-Object System.Windows.Forms.GroupBox
        $grpEngine.Text = '压缩引擎'
        $grpEngine.Location = New-Object System.Drawing.Point(16, 76)
        $grpEngine.Size = New-Object System.Drawing.Size(472, 48)
        $rbNas = New-Object System.Windows.Forms.RadioButton
        $rbNas.Text = '用 NAS 压缩（SSH 遥控）'
        $rbNas.Location = New-Object System.Drawing.Point(16, 20)
        $rbNas.Size = New-Object System.Drawing.Size(220, 20)
        $rbNas.Visible = $nasEngineOk
        $rbPc = New-Object System.Windows.Forms.RadioButton
        $rbPc.Text = '用 PC 压缩（本机）'
        $rbPc.Location = New-Object System.Drawing.Point(250, 20)
        $rbPc.Size = New-Object System.Drawing.Size(200, 20)
        $rbPc.Visible = $pcEngineOk
        if ($nasEngineOk) { $rbNas.Checked = $true } else { $rbPc.Checked = $true }
        $grpEngine.Controls.Add($rbNas)
        $grpEngine.Controls.Add($rbPc)

        $chkRecurse = New-Object System.Windows.Forms.CheckBox
        $chkRecurse.Text = '仅处理本文件夹内的图片（不进入子文件夹）'
        $chkRecurse.Checked = $true
        $chkRecurse.Location = New-Object System.Drawing.Point(16, 132)
        $chkRecurse.Size = New-Object System.Drawing.Size(472, 24)

        $bar = New-Object System.Windows.Forms.ProgressBar
        $bar.Location = New-Object System.Drawing.Point(16, 166)
        $bar.Size = New-Object System.Drawing.Size(472, 18)
        $bar.Minimum = 0
        $bar.Maximum = 100
        $bar.Style = [System.Windows.Forms.ProgressBarStyle]::Blocks
        $bar.Visible = $false

        $lblProgress = New-Object System.Windows.Forms.Label
        $lblProgress.ForeColor = [System.Drawing.Color]::Gray
        $lblProgress.Location = New-Object System.Drawing.Point(16, 190)
        $lblProgress.Size = New-Object System.Drawing.Size(472, 20)
        $lblProgress.Text = ''
        $lblProgress.Visible = $false

        $btnRun = New-Object System.Windows.Forms.Button
        $btnRun.Text = '开始压缩'
        $btnRun.Location = New-Object System.Drawing.Point(16, 232)
        $btnRun.Size = New-Object System.Drawing.Size(148, 34)
        $btnCancel = New-Object System.Windows.Forms.Button
        $btnCancel.Text = '取消'
        $btnCancel.Location = New-Object System.Drawing.Point(340, 232)
        $btnCancel.Size = New-Object System.Drawing.Size(148, 34)
        $btnClose = New-Object System.Windows.Forms.Button
        $btnClose.Text = '关闭'
        $btnClose.Location = New-Object System.Drawing.Point(340, 232)
        $btnClose.Size = New-Object System.Drawing.Size(148, 34)
        $btnClose.Visible = $false

        $form.Controls.AddRange(@($info, $grpEngine, $chkRecurse, $bar, $lblProgress, $btnRun, $btnCancel, $btnClose))
        $form.Tag = @{ Job = $null; AllowClose = $false; Total = 0; StartCount = 0; Counting = $false; Engine = ''; OutPath = '' }

        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 400

        function Get-ImageCount {
            param([string]$Dir, [bool]$Recursive)
            if (-not $Dir) { return $null }
            try {
                if (Test-Path -LiteralPath $Dir -PathType Leaf) { return 1 }
                if (-not (Test-Path -LiteralPath $Dir -PathType Container)) {
                    if ($Dir -eq $WinOutput) { return 0 }
                    return $null
                }
                $opt = $(if ($Recursive) { [System.IO.SearchOption]::AllDirectories } else { [System.IO.SearchOption]::TopDirectoryOnly })
                $exts = @('.jpg','.jpeg','.png','.webp','.gif','.bmp','.heic')
                $n = 0
                foreach ($f in [System.IO.Directory]::EnumerateFiles($Dir, '*', $opt)) {
                    if ($exts -contains [System.IO.Path]::GetExtension($f).ToLowerInvariant()) { $n++ }
                }
                return $n
            } catch { return $null }
        }

        function Start-Task {
            $tag = $form.Tag
            $engine = $(if ($rbPc.Checked) { 'pc' } else { 'nas' })
            $tag.Engine = $engine
            $chkRecurse.Visible = $false
            $grpEngine.Visible = $false
            $btnRun.Visible = $false
            $btnCancel.Visible = $false
            $btnClose.Visible = $false
            $bar.Visible = $true
            $bar.Style = [System.Windows.Forms.ProgressBarStyle]::Blocks
            $bar.Value = 0
            $lblProgress.Visible = $true
            $info.ForeColor = [System.Drawing.Color]::Black
            if ($engine -eq 'nas') {
                $tag.OutPath = $Output
                $info.Text = '正在压缩（NAS 引擎），请保持网络连接…' + "`n源: $Source"
            } else {
                $tag.OutPath = $WinOutput
                $info.Text = '正在压缩（PC 引擎）…' + "`n源: $WinSource"
            }
            $recurseBool = -not $chkRecurse.Checked
            $total = Get-ImageCount -Dir $WinSource -Recursive $recurseBool
            $start = Get-ImageCount -Dir $WinOutput -Recursive $true
            if ($null -eq $total -or $null -eq $start -or $total -le 0) {
                $tag.Counting = $false
                $bar.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
                $lblProgress.Text = '正在压缩…（暂无法统计共享目录，仅显示动效）'
            } else {
                $tag.Counting = $true
                $tag.Total = $total
                $tag.StartCount = $start
                $lblProgress.Text = ('已完成 0 / {0}' -f $total)
            }
            if ($engine -eq 'nas') {
                $recurseBash = $(if ($recurseBool) { '1' } else { '0' })
                $payload = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($bashRun.Replace('__RECURSE__', $recurseBash)))
                $tag.Job = Start-Job -ScriptBlock {
                    param($srv, $usr, $key, $prt, $payload)
                    try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
                    $a = @('-o','BatchMode=yes','-o','ConnectTimeout=10','-o','StrictHostKeyChecking=accept-new','-o','IdentitiesOnly=yes')
                    if ($prt -ne 22) { $a += @('-p', "$prt") }
                    $a += @('-i', $key, "${usr}@${srv}")
                    & ssh @a ("echo {0} | base64 -d | bash" -f $payload) 2>&1
                    Write-Output ("__RC__=" + $LASTEXITCODE)
                } -ArgumentList $Server, $User, $KeyPath, $Port, $payload
            } else {
                $argText = (New-CaesiumArgs -SourcePath $WinSource -Recurse $recurseBool -Format $Format -Quality $Quality -Lossless $Lossless -LongEdge $LongEdge -Width $Width -Height $Height -ShortEdge $ShortEdge -MaxSize $MaxSize -NoUpscale $NoUpscale -Threads $Threads) -join ' '
                $tag.Job = Start-Job -ScriptBlock {
                    param($exe, $src, $out, $argText, $recBool)
                    try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
                    if (-not (Test-Path -LiteralPath $src)) {
                        Write-Output "SRC_MISSING: path not found: $src"
                        Write-Output '__RC__=3'; return
                    }
                    if (-not (Test-Path -LiteralPath $out -PathType Container)) {
                        try { New-Item -ItemType Directory -Path $out -Force | Out-Null } catch { }
                    }
                    $a = @()
                    foreach ($t in $argText.Split(' ')) { if ($t) { $a += $t } }
                    $isDir = Test-Path -LiteralPath $src -PathType Container
                    $cnt = 0
                    if ($isDir) {
                        $opt0 = $(if ($recBool) { [System.IO.SearchOption]::AllDirectories } else { [System.IO.SearchOption]::TopDirectoryOnly })
                        foreach ($f in [System.IO.Directory]::EnumerateFiles($src, '*', $opt0)) {
                            if ($f -match '(?i)\.(jpg|jpeg|png|webp|gif|bmp|heic)$') { $cnt++ }
                        }
                    } else { $cnt = 1 }
                    if ($cnt -eq 0) {
                        Write-Output 'NO_IMAGES: no compressible images in this scope'
                        Write-Output '__RC__=0'; return
                    }
                    if ($isDir -and $recBool) { $a += '-R'; $a += '-S' }
                    $a += '-o'; $a += $out; $a += $src
                    Write-Output (">>> Compressing: $src -> $out")
                    $lines = @(& $exe @a 2>&1 | ForEach-Object { "$_" })
                    $rc = $LASTEXITCODE
                    $lines | ForEach-Object { Write-Output $_ }
                    if ($rc -ne 0) { Write-Output ('__RC__=' + $rc); return }
                    $joined = $lines -join "`n"
                    if ($joined -match '\(0 success, [0-9]+ skipped, [1-9][0-9]* errors\)') {
                        Write-Output 'COMPRESS_FAILED: caesiumclt failed for all files'
                        Write-Output '__RC__=4'; return
                    }
                    $exts = '\.(jpg|jpeg|png|webp|gif|bmp|heic)$'
                    if ($isDir) {
                        $opt = $(if ($recBool) { [System.IO.SearchOption]::AllDirectories } else { [System.IO.SearchOption]::TopDirectoryOnly })
                        $files = 0; $before = 0
                        foreach ($f in [System.IO.Directory]::EnumerateFiles($src, '*', $opt)) {
                            if ($f -match "(?i)$exts") { $files++; $before += (Get-Item -LiteralPath $f).Length }
                        }
                    } else {
                        $files = 1; $before = (Get-Item -LiteralPath $src).Length
                    }
                    $after = 0
                    if (Test-Path -LiteralPath $out -PathType Container) {
                        foreach ($f in [System.IO.Directory]::EnumerateFiles($out, '*', [System.IO.SearchOption]::AllDirectories)) {
                            if ($f -match "(?i)$exts") { $after += (Get-Item -LiteralPath $f).Length }
                        }
                    }
                    Write-Output "REPORT_FILES=$files"
                    Write-Output "REPORT_BEFORE=$before"
                    Write-Output "REPORT_AFTER=$after"
                    Write-Output '__RC__=0'
                } -ArgumentList $localExe, $WinSource, $WinOutput, $argText, [bool]$recurseBool
            }
            $timer.Start()
        }

        $btnRun.Add_Click({ Start-Task })
        $btnCancel.Add_Click({ $form.Tag.AllowClose = $true; $form.Close() })
        $btnClose.Add_Click({ $form.Tag.AllowClose = $true; $form.Close() })

        $timer.Add_Tick({
            $tag = $form.Tag
            $job = $tag.Job
            if (-not $job) { return }
            if ($job.State -ne 'Running') {
                $timer.Stop()
                $allOut = @(Receive-Job -Job $job -ErrorAction SilentlyContinue | ForEach-Object { "$_" })
                Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
                $tag.Job = $null
                $rc = 1
                foreach ($line in $allOut) { if ($line -like '__RC__=*') { $rc = [int]$line.Substring(7) } }
                $clean = @($allOut | Where-Object { $_ -notlike '__RC__=*' })
                $bar.Visible = $false
                $lblProgress.Visible = $false
                $btnClose.Visible = $true
                if ($rc -ne 0) {
                    $errTail = (@($clean | Select-Object -Last 3) -join "`n")
                    $info.ForeColor = [System.Drawing.Color]::Firebrick
                    $info.Text = "压缩失败：`n" + $(if ($errTail) { $errTail } else { "退出码 $rc" })
                } else {
                    $files = 0; $before = 0; $after = 0
                    foreach ($l in $clean) {
                        if ($l -match '^REPORT_FILES=(\d+)') { $files = [int]$Matches[1] }
                        elseif ($l -match '^REPORT_BEFORE=(\d+)') { $before = [long]$Matches[1] }
                        elseif ($l -match '^REPORT_AFTER=(\d+)') { $after = [long]$Matches[1] }
                    }
                    if ($before -gt 0) {
                        $pct = [math]::Round((1 - $after / $before) * 100, 1)
                        $info.Text = "压缩完成！`n`n文件数: $files`n原体积: $(Format-Bytes $before)`n新体积: $(Format-Bytes $after)`n节省: $pct %`n输出: $($tag.OutPath)"
                    } else {
                        $info.Text = "处理完成。`n`n输出: $($tag.OutPath)"
                    }
                }
            } else {
                if ($tag.Counting) {
                    $cur = Get-ImageCount -Dir $WinOutput -Recursive $true
                    if ($null -eq $cur) {
                        $tag.Counting = $false
                        $bar.Style = [System.Windows.Forms.ProgressBarStyle]::Marquee
                        $lblProgress.Text = '正在压缩…（暂无法统计共享目录，仅显示动效）'
                    } else {
                        $done = $cur - $tag.StartCount
                        if ($done -lt 0) { $done = 0 }
                        if ($done -gt $tag.Total) { $done = $tag.Total }
                        $pct = [int](100 * $done / $tag.Total)
                        $bar.Value = $pct
                        $lblProgress.Text = ('已完成 {0} / {1}  ({2}%)' -f $done, $tag.Total, $pct)
                    }
                }
            }
        })

        $form.Add_FormClosing({
            $tag = $form.Tag
            if ($tag.Job -and $tag.Job.State -eq 'Running' -and -not $tag.AllowClose) {
                $ans = [System.Windows.Forms.MessageBox]::Show($form, "任务仍在进行中，确定中止并退出？`n正在执行的压缩可能会被中断。", '确认退出', [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Warning)
                if ($ans -ne [System.Windows.Forms.DialogResult]::Yes) { $_.Cancel = $true }
                else { Remove-Job -Job $tag.Job -Force -ErrorAction SilentlyContinue }
            }
        })

        $null = $form.ShowDialog()
        $timer.Stop()
        if ($form.Tag.Job) { Remove-Job -Job $form.Tag.Job -Force -ErrorAction SilentlyContinue }
        exit 0
    } elseif ($Local) {
        # 控制台 PC 引擎
        if (-not $pcEngineOk) { Write-Host "错误: 未找到本地引擎 $localExe"; exit 1 }
        Write-Host '==> 本机 PC 压缩 ...'
        $argText = (New-CaesiumArgs -SourcePath $WinSource -Recurse $Recurse -Format $Format -Quality $Quality -Lossless $Lossless -LongEdge $LongEdge -Width $Width -Height $Height -ShortEdge $ShortEdge -MaxSize $MaxSize -NoUpscale $NoUpscale -Threads $Threads) -join ' '
        $r = Run-LocalCaesium -SourcePath $WinSource -OutputPath $WinOutput -ArgText $argText -Recurse:$Recurse -DryRun:$DryRun
        if ($r.Code -ne 0) { Write-Host "压缩失败(exit=$($r.Code))，请查看上方输出。"; exit 1 }
        Show-ConsoleReport -Lines $r.Output -DryRun:$DryRun
        exit 0
    } elseif ($Source) {
        # 控制台 NAS 引擎
        Write-Host '==> 遥控 NAS 压缩 ...'
        $bashFinal = $bash.Replace('__RECURSE__', $(if ($Recurse) { '1' } else { '0' }))
        $r = Invoke-Ssh $bashFinal
        if ($r.Code -ne 0) { Write-Host "压缩失败(exit=$($r.Code))，请查看上方输出。"; exit 1 }
        Show-ConsoleReport -Lines $r.Output -DryRun:$DryRun
        exit 0
    } else {
        Out-Help
        exit 1
    }

Out-Help
