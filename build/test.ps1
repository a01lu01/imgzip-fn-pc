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
    $parseErrors = $null; $tokens = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'worker/imgzip-worker.ps1'), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
} finally { Pop-Location }
