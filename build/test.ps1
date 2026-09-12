#Requires -Version 7.0
param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    & $Dotnet restore tests/ImgZip.FakeEngine/ImgZip.FakeEngine.csproj --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Fake engine restore failed.' }
    & $Dotnet build tests/ImgZip.FakeEngine/ImgZip.FakeEngine.csproj -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Fake engine build failed.' }
    & $Dotnet restore tests/ImgZip.Tests/ImgZip.Tests.csproj --locked-mode --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Test restore failed.' }
    & $Dotnet run --project tests/ImgZip.Tests/ImgZip.Tests.csproj -c Release --no-restore -- $root
    if ($LASTEXITCODE -ne 0) { throw 'Core/worker checks failed.' }
    $bash = Get-Command bash.exe -ErrorAction SilentlyContinue
    if (-not $bash) {
        $bashCandidate = @(
            (Join-Path $env:ProgramFiles 'Git\bin\bash.exe'),
            (Join-Path ${env:ProgramFiles(x86)} 'Git\bin\bash.exe'),
            (Join-Path $env:LOCALAPPDATA 'Programs\Git\bin\bash.exe')
        ) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1
        if ($bashCandidate) { $bash = Get-Item -LiteralPath $bashCandidate }
    }
    if ($bash) {
        $bashPath = if ($bash -is [System.Management.Automation.CommandInfo]) { $bash.Source } else { $bash.FullName }
        $bashRoot = $root -replace '\\', '/'
        if ($bashRoot -match '^([A-Za-z]):') { $bashRoot = '/' + $Matches[1].ToLowerInvariant() + $bashRoot.Substring(2) }
        & $bashPath -lc 'exec "$@"' imgzip ($bashRoot.TrimEnd('/') + '/tests/nas-worker-concurrency.sh') $bashRoot
        if ($LASTEXITCODE -ne 0) { throw 'NAS worker concurrency checks failed.' }
    } else {
        Write-Host 'SKIP nas-worker concurrency checks: bash.exe not found'
    }
    $parseErrors = $null; $tokens = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'worker/imgzip-worker.ps1'), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
} finally { Pop-Location }
