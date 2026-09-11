#Requires -Version 5.1
<# Version 1 JSON-line protocol. This entry point never loads WinForms or writes UI text to stdout. #>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::InputEncoding = $utf8
[Console]::OutputEncoding = $utf8
$request = $null
$journal = $null
$recordEvents = $false
$lastTerminal = $false
$remoteMayBeRunning = $false
$terminationUnconfirmed = $false
$localCounts = $null

function Emit {
    param([hashtable]$Event)
    $Event.version = 1; $Event.jobId = $request.jobId
    if ($Event.type -eq 'finished') { $script:lastTerminal = $true }
    $line = ConvertTo-Json -InputObject $Event -Depth 15 -Compress
    if ($recordEvents -and $journal) { [IO.File]::AppendAllText($journal, $line + "`n", $utf8) }
    [Console]::Out.WriteLine($line); [Console]::Out.Flush()
}
function Finish {
    param([string]$State, [string]$Message = '')
    Emit @{ type='finished'; state=$State; message=$Message }
}
function B64([string]$Text) { [Convert]::ToBase64String($utf8.GetBytes($Text)) }
function UnB64([string]$Text) { $utf8.GetString([Convert]::FromBase64String($Text)) }
function Quote-ProcessArgument([string]$Value) {
    # Windows CommandLineToArgvW quoting, also accepted by ProcessStartInfo.Arguments on .NET Unix.
    return '"' + ([regex]::Replace($Value, '(\\*)"', '$1$1\"') -replace '(\\+)$', '$1$1') + '"'
}
function Start-Child {
    param([string]$FileName, [string[]]$Arguments, [string]$InputText = '', [switch]$HasInput)
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $FileName
    $info.Arguments = (($Arguments | ForEach-Object { Quote-ProcessArgument $_ }) -join ' ')
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = $utf8; $info.StandardErrorEncoding = $utf8
    $info.RedirectStandardInput = [bool]$HasInput
    $process = New-Object Diagnostics.Process
    $process.StartInfo = $info
    $null = $process.Start()
    if ($HasInput) { $process.StandardInput.Write($InputText); $process.StandardInput.Close() }
    return $process
}
function Get-SshArgs {
    $c = $request.config
    if ($c.server -notmatch '^[a-zA-Z0-9][a-zA-Z0-9.:-]*$' -or $c.user -notmatch '^[a-zA-Z0-9_][a-zA-Z0-9_.-]*$') { throw 'Invalid SSH host or username.' }
    if ($c.port -lt 1 -or $c.port -gt 65535) { throw 'Invalid SSH port.' }
    if (-not (Test-Path -LiteralPath $c.keyPath -PathType Leaf)) { throw 'SSH private key path does not exist.' }
    return @('-T','-o','BatchMode=yes','-o','ConnectTimeout=10','-o','ServerAliveInterval=5','-o','ServerAliveCountMax=2',
        '-o','StrictHostKeyChecking=accept-new','-o','IdentitiesOnly=yes','-p',"$($c.port)",'-i',[string]$c.keyPath,"$($c.user)@$($c.server)",'bash','-s')
}
function Remote-Script {
    param([string]$Mode, [hashtable]$Variables = @{})
    $body = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'nas-worker.sh')) -replace "`r", ''
    $values = @{ MODE=$Mode; JOB_ID=[string]$request.jobId }
    foreach ($key in $Variables.Keys) { $values[$key] = [string]$Variables[$key] }
    if ($Mode -eq 'launch') { $values.SCRIPT_B64 = B64 $body }
    $prelude = ''
    foreach ($key in $values.Keys) {
        # The only interpolated input consists of fixed variable names and base64 alphabet.
        $prelude += $key + '=$(printf ''%s'' ''' + (B64 $values[$key]) + "' | base64 -d)`n"
    }
    return $prelude + $body
}
function Decode-RemoteEvent {
    param([string]$Line)
    $item = ConvertFrom-Json -InputObject $Line
    if ($item.version -ne 1 -or $item.jobId -ne $request.jobId) { throw 'Remote protocol task identity mismatch.' }
    $event = @{}
    foreach ($property in $item.PSObject.Properties) {
        if ($property.Name -in @('path64','output64','message64')) { $event[$property.Name.Substring(0,$property.Name.Length-2)] = UnB64 $property.Value }
        else { $event[$property.Name] = $property.Value }
    }
    if ($event.output) {
        $cachePath = Join-Path $request.stateDirectory 'smb-map.json'
        if (Test-Path -LiteralPath $cachePath) {
            try {
                $cache = ConvertFrom-Json ([IO.File]::ReadAllText($cachePath))
                foreach ($mapping in ($cache.PSObject.Properties | Sort-Object { ([string]$_.Value).Length } -Descending)) {
                    $prefix = ([string]$mapping.Value).TrimEnd('/')
                    if ($event.output.StartsWith($prefix + '/', [StringComparison]::Ordinal)) {
                        $event.openOutput = '\\' + ($mapping.Name -replace '/', '\') + ($event.output.Substring($prefix.Length) -replace '/', '\')
                        break
                    }
                }
            } catch { }
        }
    }
    return $event
}
function Invoke-Remote {
    param([string]$Mode, [hashtable]$Variables = @{}, [switch]$Stream)
    $sshPath = if ($request.sshPath) { [string]$request.sshPath } else { 'ssh' }
    $process = Start-Child $sshPath (Get-SshArgs) (Remote-Script $Mode $Variables) -HasInput
    $errorTask = $process.StandardError.ReadToEndAsync()
    $collected = New-Object 'System.Collections.Generic.List[object]'
    $sawTerminal = $false
    try {
        while (($line = $process.StandardOutput.ReadLine()) -ne $null) {
            if (-not $line.Trim()) { continue }
            $event = Decode-RemoteEvent $line
            if ($sawTerminal) { throw 'Remote emitted data after terminal event.' }
            if ($event.type -eq 'finished') { $sawTerminal = $true }
            if ($Stream) { Emit $event } else { $collected.Add($event) }
        }
        $process.WaitForExit()
        $diagnostic = $errorTask.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "SSH failed (exit=$($process.ExitCode)): $diagnostic" }
        if ($Stream -and -not $sawTerminal) { throw 'SSH ended without a confirmed terminal event.' }
        if (-not $Stream) { return ,$collected.ToArray() }
    } finally { $process.Dispose() }
}
function Resolve-WindowsPath([string]$Path) {
    if ($Path.StartsWith('\\')) { return $Path }
    if ($env:OS -eq 'Windows_NT' -and $Path -match '^[a-zA-Z]:\\') {
        $disk = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='{0}'" -f $Path.Substring(0,2)) -ErrorAction SilentlyContinue
        if ($disk -and $disk.DriveType -eq 4 -and $disk.ProviderName) { return $disk.ProviderName.TrimEnd('\') + '\' + $Path.Substring(3) }
    }
    return [IO.Path]::GetFullPath($Path)
}
function Is-NasHost([string]$HostName) {
    $c = $request.config
    if (@($c.nasHosts) -contains $HostName -or $HostName -eq $c.nasIp -or $HostName -eq $c.server) { return $true }
    try {
        $expected = @([Net.Dns]::GetHostAddresses($c.server) | ForEach-Object { $_.ToString() })
        foreach ($address in [Net.Dns]::GetHostAddresses($HostName)) { if ($expected -contains $address.ToString()) { return $true } }
    } catch { }
    return $false
}
function Resolve-NasSources {
    $paths = @()
    foreach ($source in $request.sources) {
        $unc = Resolve-WindowsPath $source
        if ($unc -notmatch '^\\\\([^\\]+)\\([^\\]+)(.*)$') { throw 'NAS engine requires a UNC share or mapped NAS drive.' }
        $hostName = $Matches[1]; $share = $Matches[2]; $tail = $Matches[3]
        if (-not (Is-NasHost $hostName)) { throw 'Source share does not belong to the configured NAS.' }
        # Resolve afresh to validate Samba configuration. Cache is informative, never blindly trusted.
        $events = Invoke-Remote 'resolve' @{ SHARE_B64=(B64 $share) }
        $resolved = @($events | Where-Object { $_.type -eq 'resolved' })
        if ($resolved.Count -ne 1) { throw 'Cannot resolve Samba share. Check SSH permissions and share configuration.' }
        $shareRoot = [string]$resolved[0].path
        $paths += $shareRoot.TrimEnd('/') + ($tail -replace '\\','/')
        $cachePath = Join-Path $request.stateDirectory 'smb-map.json'
        $cache = @{}
        if (Test-Path -LiteralPath $cachePath) {
            try { $old = ConvertFrom-Json ([IO.File]::ReadAllText($cachePath)); foreach ($p in $old.PSObject.Properties) { $cache[$p.Name] = $p.Value } } catch { }
        }
        $cache["$hostName/$share"] = $shareRoot
        $null = [IO.Directory]::CreateDirectory($request.stateDirectory)
        [IO.File]::WriteAllText($cachePath, (ConvertTo-Json $cache -Compress), $utf8)
    }
    return ,$paths
}
function Get-NativeArgs {
    param([string]$Path)
    $o = $request.options
    $a = @('-e','--keep-dates','--threads',"$($o.threads)")
    $extension = [IO.Path]::GetExtension($Path).TrimStart('.').ToLowerInvariant()
    if ($extension -eq 'jpg') { $extension = 'jpeg' }
    if ($o.format -ne 'keep' -and $o.format -ne $extension) { $a += @('--format',[string]$o.format) }
    if ($o.lossless) { $a += '--lossless' } else {
        $a += @('-q',"$($o.quality)")
        if ($o.maxSize -gt 0) { $a += @('--max-size',"$($o.maxSize)") }
    }
    if ($o.resize -ne 'none') {
        $flag = @{long='--long-edge';short='--short-edge';width='--width';height='--height'}[$o.resize]
        $a += @($flag,"$($o.pixels)")
        if ($o.noUpscale) { $a += '--no-upscale' }
    }
    return ,$a
}
function Check-Request {
    if ($request.version -ne 1 -or $request.jobId -notmatch '^[a-f0-9]{32}$') { throw 'Unsupported request version or invalid task ID.' }
    if ($request.operation -notin @('run','resolve','probe','cancel','inspect')) { throw 'Unknown worker operation.' }
    if ($request.engine -notin @('pc','nas')) { throw 'Unknown engine.' }
    if (-not [IO.Path]::IsPathRooted($request.stateDirectory)) { throw 'State directory must be absolute.' }
    if ($request.operation -ne 'run') { return }
    $o = $request.options
    if ($o.format -notin @('keep','jpeg','webp','png') -or $o.resize -notin @('none','long','short','width','height')) { throw 'Invalid format or resize mode.' }
    if ((-not $o.lossless -and ($o.quality -lt 0 -or $o.quality -gt 100 -or $o.maxSize -lt 0)) -or ($o.resize -ne 'none' -and $o.pixels -lt 1) -or $o.threads -lt 1 -or $o.threads -gt 64) { throw 'Compression options are outside supported bounds.' }
    if (@($request.sources).Count -eq 0) { throw 'No input paths.' }
}
function Select-LocalSources {
    $sources = @($request.sources | ForEach-Object { Resolve-WindowsPath $_ } | Select-Object -Unique)
    if ($sources.Count -eq 0) { throw 'No input paths.' }
    if ($sources.Count -gt 1) {
        $parent = [IO.Path]::GetDirectoryName($sources[0])
        foreach ($source in $sources) {
            if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or [IO.Path]::GetDirectoryName($source) -ne $parent) { throw 'Select one folder or images from the same directory.' }
        }
    }
    return ,$sources
}
function Read-Manifest {
    param([string[]]$Sources)
    $files = New-Object 'System.Collections.Generic.List[object]'
    $script:skippedCount = 0
    $pending = New-Object 'System.Collections.Generic.Stack[string]'
    foreach ($source in $Sources) { $pending.Push($source) }
    while ($pending.Count -gt 0) {
        if (Test-Path -LiteralPath $cancelFile) { break }
        $path = $pending.Pop()
        $item = Get-Item -LiteralPath $path -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { $script:skippedCount++; continue }
        if ($item.PSIsContainer) {
            foreach ($child in Get-ChildItem -LiteralPath $path -Force) {
                if (-not $child.PSIsContainer -or $request.options.recurse) { $pending.Push($child.FullName) }
            }
        } elseif ($item.Extension.ToLowerInvariant() -in @('.jpg','.jpeg','.png','.webp','.gif')) {
            $files.Add(@{path=$item.FullName;size=[long]$item.Length})
        } else { $script:skippedCount++ }
    }
    return ,@($files.ToArray() | Sort-Object { $_.path })
}
function Unique-Output([string]$Base, [switch]$Create) {
    $candidate = $Base; $suffix = 2
    while ($true) {
        if (-not (Test-Path -LiteralPath $candidate)) {
            if (-not $Create) { return $candidate }
            try { $null = New-Item -ItemType Directory -Path $candidate -ErrorAction Stop; return $candidate }
            catch { if (-not (Test-Path -LiteralPath $candidate)) { throw } }
        }
        $candidate = "${Base}_$suffix"; $suffix++
    }
}
function Stop-Engine($Process) {
    if ($Process.HasExited) { return }
    $script:terminationUnconfirmed = $true
    if ($env:OS -eq 'Windows_NT') {
        $killer = Start-Child (Join-Path $env:SystemRoot 'System32/taskkill.exe') @('/PID',"$($Process.Id)",'/T','/F')
        $null = $killer.StandardOutput.ReadToEndAsync(); $null = $killer.StandardError.ReadToEndAsync()
        $killer.WaitForExit(); $killer.Dispose()
    } else { $Process.Kill($true) }
    if (-not $Process.WaitForExit(10000)) { throw 'Engine termination could not be confirmed.' }
    $script:terminationUnconfirmed = $false
}
function Move-BatchOutputs {
    # 组批运行时把 stage 中已写完的产物搬到最终位置并逐张上报；文件被独占打开说明仍在写入，跳过本轮。
    param($Stage, $BatchIndexes, $Files, $Output, $Emitted, $Counts)
    foreach ($item in @(Get-ChildItem -LiteralPath $Stage -File -Recurse -ErrorAction SilentlyContinue)) {
        $stream = $null
        try { $stream = [IO.File]::Open($item.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None) }
        catch { continue }
        finally { if ($stream) { $stream.Dispose() } }
        $stem = [IO.Path]::GetFileNameWithoutExtension($item.Name)
        foreach ($bi in $BatchIndexes) {
            if ($Emitted.Contains([int]$bi)) { continue }
            # 引擎按源文件名产出产物, 因此用源 stem 匹配(计划名可能因大小写去重而带 _2 后缀)
            $sourceStem = [IO.Path]::GetFileNameWithoutExtension([string]$Files[$bi].path)
            if (-not [string]::Equals($stem, $sourceStem, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $relative = [string]$Files[$bi].relativeOutput
            $relativeDirectory = [IO.Path]::GetDirectoryName($relative)
            $destination = if ($relativeDirectory) { Join-Path $Output $relativeDirectory } else { $Output }
            $null = [IO.Directory]::CreateDirectory($destination)
            $leaf = [IO.Path]::GetFileName($relative); $target = Join-Path $destination $leaf; $serial = 2
            while (Test-Path -LiteralPath $target) {
                $target = Join-Path $destination ([IO.Path]::GetFileNameWithoutExtension($leaf) + "_$serial" + [IO.Path]::GetExtension($leaf)); $serial++
            }
            # File.Move without overwrite is the final race-safe check.
            [IO.File]::Move($item.FullName, $target)
            $null = $Emitted.Add([int]$bi)
            $length = (Get-Item -LiteralPath $target).Length
            $Counts.succeeded++; $Counts.beforeBytes += [long]$Files[$bi].size; $Counts.afterBytes += $length; $Counts.completed++
            $event = $Counts.Clone(); $event.type = 'file'; $event.state = 'succeeded'; $event.index = [int]$bi
            $event.path = [string]$Files[$bi].path; $event.beforeBytes = [long]$Files[$bi].size; $event.afterBytes = $length
            Emit $event
            break
        }
    }
}
function Run-Local {
    $sources = Select-LocalSources
    $sourceRoot = if (Test-Path -LiteralPath $sources[0] -PathType Container) { $sources[0].TrimEnd([IO.Path]::DirectorySeparatorChar) } else { [IO.Path]::GetDirectoryName($sources[0]) }
    if (-not $sourceRoot -or $sourceRoot -eq [IO.Path]::GetPathRoot($sources[0]).TrimEnd([IO.Path]::DirectorySeparatorChar)) { throw 'Select a subfolder instead of the drive root.' }
    Emit @{type='phase';state='preparing';message='正在建立文件清单'}
    $files = Read-Manifest $sources
    $total = $files.Count
    $planned = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $files) {
        $relative = $file.path.Substring($sourceRoot.Length).TrimStart([IO.Path]::DirectorySeparatorChar)
        $parent = [IO.Path]::GetDirectoryName($relative)
        $extension = [IO.Path]::GetExtension($relative).ToLowerInvariant()
        if ($request.options.format -ne 'keep') { $extension = if ($request.options.format -eq 'jpeg') { '.jpg' } else { '.' + $request.options.format } }
        $stem = [IO.Path]::GetFileNameWithoutExtension($relative)
        $leaf = $stem + $extension; $serial = 2
        $candidate = if ($parent) { Join-Path $parent $leaf } else { $leaf }
        while (-not $planned.Add($candidate)) {
            $leaf = $stem + "_$serial" + $extension; $serial++
            $candidate = if ($parent) { Join-Path $parent $leaf } else { $leaf }
        }
        $file.relativeOutput = $candidate
    }
    $output = Unique-Output ($sourceRoot + '_compressed')
    $counts = @{total=$total;completed=0;succeeded=0;failed=0;skipped=$skippedCount;beforeBytes=[long]0;afterBytes=[long]0;output=$output;dryRun=[bool]$request.options.dryRun}
    $script:localCounts = $counts
    $manifest = $counts.Clone(); $manifest.type='manifest'; $manifest.state='ready'; Emit $manifest
    if (Test-Path -LiteralPath $cancelFile) { $counts.type='finished';$counts.state='cancelled';Emit $counts;return }
    if ($request.options.dryRun) { $counts.type='finished';$counts.state='dryRun';Emit $counts;return }
    if ($total -eq 0) { $counts.type='finished';$counts.state='empty';Emit $counts;return }
    if (-not (Test-Path -LiteralPath $request.enginePath -PathType Leaf)) { throw '本机引擎缺失：请重新安装应用。' }
    $output = Unique-Output ($sourceRoot + '_compressed') -Create
    $counts.output = $output
    $manifest = $counts.Clone();$manifest.type='manifest';$manifest.state='ready';Emit $manifest
    # 组批：同批共享参数；按“是否需要 --format 转换”分组，避免引擎拒绝同格式转换。
    $cores = [Math]::Max(1, [Environment]::ProcessorCount)
    $batchLimit = [Math]::Min(16, [Math]::Max(4, 2 * $cores))
    $plain = New-Object 'System.Collections.Generic.List[int]'
    $convert = New-Object 'System.Collections.Generic.List[int]'
    for ($i = 0; $i -lt $total; $i++) {
        $ext = [IO.Path]::GetExtension([string]$files[$i].path).TrimStart('.').ToLowerInvariant()
        if ($ext -eq 'jpg') { $ext = 'jpeg' }
        if ($request.options.format -ne 'keep' -and $request.options.format -ne $ext) { $convert.Add($i) } else { $plain.Add($i) }
    }
    $batches = New-Object 'System.Collections.Generic.List[object]'
    foreach ($group in @($plain, $convert)) {
        $current = New-Object 'System.Collections.Generic.List[int]'
        $stems = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($idx in $group) {
            # 用源 stem 判重: 同一批内引擎会按源文件名写出产物, 重复会互相覆盖
            $stem = [IO.Path]::GetFileNameWithoutExtension([string]$files[$idx].path)
            if ($current.Count -ge $batchLimit -or -not $stems.Add($stem)) {
                if ($current.Count -gt 0) { $batches.Add($current) }
                $current = New-Object 'System.Collections.Generic.List[int]'
                $stems = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
                $null = $stems.Add($stem)
            }
            $current.Add($idx)
        }
        if ($current.Count -gt 0) { $batches.Add($current) }
    }
    $batchNumber = 0
    foreach ($batchIndexes in $batches) {
        if (Test-Path -LiteralPath $cancelFile) { break }
        $batchNumber++
        $stage = Join-Path $output ('.imgzip-' + $request.jobId + '-b' + $batchNumber)
        $null = New-Item -ItemType Directory -Path $stage
        $process = $null; $text = ''; $wasCancelled = $false
        $emitted = New-Object 'System.Collections.Generic.HashSet[int]'
        try {
            $a = @()
            $a += Get-NativeArgs ([string]$files[$batchIndexes[0]].path)
            $ti = [Array]::IndexOf($a, '--threads')
            $batchThreads = [Math]::Min([Math]::Max(1, [int]$request.options.threads), $batchIndexes.Count)
            if ($ti -ge 0) { $a[$ti + 1] = "$batchThreads" }
            $a += @('--verbose', '3', '-o', $stage)
            foreach ($bi in $batchIndexes) { $a += [string]$files[$bi].path }
            $process = Start-Child $request.enginePath $a
            $outTask = $process.StandardOutput.ReadToEndAsync(); $errTask = $process.StandardError.ReadToEndAsync()
            while (-not $process.WaitForExit(150)) {
                if (Test-Path -LiteralPath $cancelFile) { Stop-Engine $process; $wasCancelled = $true; break }
                # 引擎每写完一张就上报一次，保持逐张进度
                Move-BatchOutputs $stage $batchIndexes $files $output $emitted $counts
            }
            if ($wasCancelled -or (Test-Path -LiteralPath $cancelFile)) { $wasCancelled = $true }
            else {
                $text = $outTask.GetAwaiter().GetResult() + $errTask.GetAwaiter().GetResult()
                Move-BatchOutputs $stage $batchIndexes $files $output $emitted $counts
                foreach ($bi in $batchIndexes) {
                    if ($emitted.Contains([int]$bi)) { continue }
                    $src = [string]$files[$bi].path
                    $failure = '引擎未生成输出'
                    if ($text -match ('(?m)^\[(?:Error|Skipped)\]\s+' + [regex]::Escape($src) + '\s+->')) { $failure = $Matches[0].Trim() }
                    elseif ($process.ExitCode -ne 0) { $failure = "caesium exit=$($process.ExitCode): $text" }
                    if ($failure.Length -gt 2000) { $failure = $failure.Substring(0, 2000) }
                    $counts.failed++; $counts.completed++
                    $event = $counts.Clone(); $event.type = 'file'; $event.state = 'failed'; $event.path = $src; $event.index = [int]$bi
                    $event.message = $failure; $event.beforeBytes = 0; $event.afterBytes = 0; Emit $event
                }
            }
        } catch {
            foreach ($bi in $batchIndexes) {
                if ($emitted.Contains([int]$bi)) { continue }
                $counts.failed++; $counts.completed++
                $event = $counts.Clone(); $event.type = 'file'; $event.state = 'failed'; $event.path = [string]$files[$bi].path; $event.index = [int]$bi
                $event.message = $_.Exception.Message; $event.beforeBytes = 0; $event.afterBytes = 0; Emit $event
            }
        } finally {
            if ($process) {
                if (-not $process.HasExited) { Stop-Engine $process }
                $process.Dispose()
            }
            if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
        }
        if ($wasCancelled) { break }
    }
    $counts.type='finished'
    $counts.state = if (Test-Path -LiteralPath $cancelFile) { 'cancelled' } elseif ($counts.failed -eq 0) { 'succeeded' } elseif ($counts.succeeded -gt 0) { 'partial' } else { 'failed' }
    Emit $counts
}

try {
    $line = [Console]::In.ReadLine()
    if (-not $line) { throw 'Expected one JSON request on stdin.' }
    $request = ConvertFrom-Json -InputObject $line
    Check-Request
    $jobDirectory = Join-Path (Join-Path $request.stateDirectory 'jobs') $request.jobId
    $journal = Join-Path $jobDirectory 'events.jsonl'
    $cancelFile = Join-Path $jobDirectory 'cancel'
    if ($request.operation -eq 'probe') {
        if ($request.engine -eq 'pc') {
            $available = $false
            if (Test-Path -LiteralPath $request.enginePath -PathType Leaf) {
                $probe = Start-Child $request.enginePath @('--version')
                try {
                    $probeOutput=$probe.StandardOutput.ReadToEndAsync(); $probeError=$probe.StandardError.ReadToEndAsync()
                    if (-not $probe.WaitForExit(10000)) { Stop-Engine $probe; throw 'Engine probe timed out.' }
                    $null=$probeOutput.GetAwaiter().GetResult(); $null=$probeError.GetAwaiter().GetResult()
                    $available=$probe.ExitCode -eq 0
                } finally { $probe.Dispose() }
            }
            Emit @{type='probe';pcAvailable=$available}
        }
        else { foreach ($event in (Invoke-Remote 'probe')) { Emit $event } }
    } elseif ($request.operation -eq 'resolve') {
        if ($request.engine -eq 'nas') { $paths=Resolve-NasSources; foreach ($path in $paths) { Emit @{type='resolved';path=$path} } }
        else { foreach ($path in (Select-LocalSources)) { Emit @{type='resolved';path=$path} } }
    } elseif ($request.operation -eq 'inspect') {
        if ($request.engine -eq 'nas') { Invoke-Remote 'inspect' -Stream }
        elseif (Test-Path -LiteralPath $journal) {
            foreach ($saved in [IO.File]::ReadAllLines($journal)) {
                try { $event=ConvertFrom-Json $saved } catch { continue }
                [Console]::Out.WriteLine($saved)
                if ($event.type -eq 'finished') { $lastTerminal=$true }
            }
            if (-not $lastTerminal) { Finish 'unknown' '任务尚未确认结束，请稍后重新检查。' }
        } else { Finish 'unknown' '找不到任务记录，无法确认状态。' }
    } elseif ($request.operation -eq 'cancel') {
        for ($attempt=0; $attempt -lt 50 -and -not (Test-Path -LiteralPath $jobDirectory); $attempt++) { Start-Sleep -Milliseconds 100 }
        if (Test-Path -LiteralPath $jobDirectory) { [IO.File]::WriteAllText($cancelFile,'cancel',$utf8) }
        if ($request.engine -eq 'nas') {
            if ((Test-Path -LiteralPath $jobDirectory) -and -not (Test-Path -LiteralPath (Join-Path $jobDirectory 'nas-launched'))) {
                Emit @{type='cancelRequested';state='cancelling';message='已请求取消 NAS 准备阶段，等待确认。'}
            } else { Invoke-Remote 'cancel' -Stream }
        }
        elseif (Test-Path -LiteralPath $jobDirectory) {
            [IO.File]::WriteAllText($cancelFile,'cancel',$utf8)
            Emit @{type='cancelRequested';state='cancelling';message='已请求取消，等待执行进程确认。'}
        } else { Finish 'unknown' '找不到任务记录，无法确认取消。' }
    } else {
        $null=New-Item -ItemType Directory -Path $jobDirectory
        $recordEvents=$true
        if ($request.engine -eq 'pc') { Run-Local }
        else {
            Emit @{type='phase';state='preparing';message='正在解析 NAS 共享路径'}
            $paths=Resolve-NasSources
            if (Test-Path -LiteralPath $cancelFile) { Finish 'cancelled' '已取消，未启动 NAS 压缩任务。'; exit 0 }
            $o=$request.options
            $variables=@{SOURCES_B64=(($paths | ForEach-Object { B64 $_ }) -join "`n");FORMAT=$o.format;QUALITY=$o.quality;RESIZE=$o.resize;PIXELS=$o.pixels;MAX_SIZE=$o.maxSize;THREADS=$o.threads}
            foreach ($pair in @(@('LOSSLESS','lossless'),@('NO_UPSCALE','noUpscale'),@('RECURSE','recurse'),@('DRY_RUN','dryRun'))) { $variables[$pair[0]]=([bool]$o.($pair[1])).ToString().ToLowerInvariant() }
            $remoteMayBeRunning=$true
            [IO.File]::WriteAllText((Join-Path $jobDirectory 'nas-launched'),'launching',$utf8)
            if (Test-Path -LiteralPath $cancelFile) { Finish 'cancelled' '已取消，未启动 NAS 压缩任务。'; exit 0 }
            Invoke-Remote 'launch' $variables -Stream
        }
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    if ($request) {
        if (-not $lastTerminal) {
            # An SSH failure may leave a detached task running. Never claim it stopped.
            $state = if ($terminationUnconfirmed -or ($request.engine -eq 'nas' -and ($remoteMayBeRunning -or $request.operation -in @('inspect','cancel')))) { 'unknown' } else { 'failed' }
            if ($request.engine -eq 'pc' -and $localCounts) {
                $localCounts.type='finished';$localCounts.state=$state;$localCounts.message=$_.Exception.Message;Emit $localCounts
            } else { Finish $state $_.Exception.Message }
        }
    } else { exit 1 }
}
