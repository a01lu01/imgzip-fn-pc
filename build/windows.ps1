#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$CompilerPath = '',
    [switch]$BootstrapCompiler,
    [switch]$SkipTests
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The native XAML compiler and Inno Setup require Windows.' }
$root = Split-Path -Parent $PSScriptRoot
$deps = Get-Content (Join-Path $PSScriptRoot 'dependencies.json') -Raw | ConvertFrom-Json
[xml]$properties = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
$version = [string]$properties.Project.PropertyGroup.Version
$tools = Join-Path $root '.tools'
$buildRoot = Join-Path $root ('artifacts/build/' + [Guid]::NewGuid().ToString('N'))
$publish = Join-Path $buildRoot 'publish'
$installer = Join-Path $root 'artifacts/installer'
$null = New-Item -ItemType Directory -Path $tools,$publish,$installer -Force

function Download-Verified($Dependency, [string]$Destination) {
    if (-not (Test-Path -LiteralPath $Destination) -or (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Dependency.sha256) {
        Invoke-WebRequest -Uri $Dependency.url -OutFile $Destination
    }
    if ((Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash.ToLowerInvariant() -ne $Dependency.sha256) { throw "Checksum mismatch: $Destination" }
}
function Dotnet([string[]]$Arguments) {
    # 注意: 函数名 Dotnet 与命令 dotnet 大小写不敏感, 必须用 dotnet.exe 强制走外部程序
    & dotnet.exe @Arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed: ' + ($Arguments -join ' ')) }
}
Push-Location $root
try {
    if (-not $SkipTests) { & (Join-Path $PSScriptRoot 'test.ps1') }
    # The app pins win-x64 itself; a global -r would change the core library lock graph.
    Dotnet -Arguments @('restore','src/ImgZip.App/ImgZip.App.csproj','--locked-mode')
    Dotnet -Arguments @('publish','src/ImgZip.App/ImgZip.App.csproj','-c','Release','--no-restore','-o',$publish)
    $archive = Join-Path $tools ('caesium-' + $deps.caesiumWindows.version + '-windows.zip')
    Download-Verified $deps.caesiumWindows $archive
    $extracted = Join-Path $buildRoot 'caesium'
    Expand-Archive -LiteralPath $archive -DestinationPath $extracted
    $binary = @(Get-ChildItem $extracted -Recurse -File -Filter 'caesiumclt.exe')
    if ($binary.Count -ne 1) { throw 'Engine archive did not contain exactly one caesiumclt.exe.' }
    $null = New-Item -ItemType Directory -Path (Join-Path $publish 'bin')
    # Preserve engine-side DLLs and notices if the pinned distribution contains them.
    Get-ChildItem -LiteralPath $binary[0].DirectoryName -Force | Copy-Item -Destination (Join-Path $publish 'bin') -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination (Join-Path $publish 'licenses') -Recurse
    # Keep the notices from the exact restored runtime packages alongside our upstream licenses.
    $assets = Get-Content (Join-Path $root 'src/ImgZip.App/obj/project.assets.json') -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Name -notmatch '^(Microsoft\.NETCore\.App\.Runtime|Microsoft\.WindowsAppSDK)' -or $library.Value.type -ne 'package') { continue }
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $package = Join-Path $folder $library.Value.path
            if (-not (Test-Path -LiteralPath $package)) { continue }
            $noticeDirectory = Join-Path (Join-Path $publish 'licenses') ($library.Name -replace '/', '-')
            $null = New-Item -ItemType Directory -Path $noticeDirectory -Force
            Get-ChildItem -LiteralPath $package -File | Where-Object Name -Match '(?i)license|notice' | Copy-Item -Destination $noticeDirectory
            break
        }
    }
    Copy-Item -LiteralPath (Join-Path $root 'docs/WINDOWS.md') -Destination (Join-Path $publish '使用说明.md')
    Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $publish
    # Smoke-check the real codec's launch dependency chain without opening any UI.
    & (Join-Path $publish 'bin/caesiumclt.exe') --version
    if ($LASTEXITCODE -ne 0) { throw 'Bundled engine cannot launch on the build host.' }
    if ($BootstrapCompiler) {
        $setup = Join-Path $tools ('innosetup-' + $deps.innoSetup.version + '.exe')
        Download-Verified $deps.innoSetup $setup
        $compilerDirectory = Join-Path $tools 'inno'
        $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',('/DIR="' + $compilerDirectory + '"')) -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw 'Inno Setup compiler installation failed.' }
        $CompilerPath = Join-Path $compilerDirectory 'ISCC.exe'
    }
    if (-not $CompilerPath) {
        $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($command) { $CompilerPath = $command.Source }
        else { $CompilerPath = Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe' }
    }
    if (-not (Test-Path -LiteralPath $CompilerPath)) { throw 'Install Inno Setup 7.1.0, pass -CompilerPath, or explicitly use -BootstrapCompiler.' }
    $compilerVersion = (Get-Item -LiteralPath $CompilerPath).VersionInfo.FileVersion
    # ISCC.exe 常不带版本资源(读到 0.0.0.0); 安装包本身已按 SHA256 校验, 此处仅拦截明显不符的版本
    if ($compilerVersion -ne '0.0.0.0' -and $compilerVersion -notlike ($deps.innoSetup.version + '*')) { throw "Expected Inno Setup $($deps.innoSetup.version), found $compilerVersion" }
    & $CompilerPath ('/DAppVersion=' + $version) ('/DPublishDir=' + $publish) ('/DOutputDir=' + $installer) (Join-Path $root 'installer/ImgZip.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
    $result = Join-Path $installer ("ImgZip-$version-win-x64-setup.exe")
    if (-not (Test-Path -LiteralPath $result)) { throw 'Installer output was not created.' }
    (Get-FileHash -LiteralPath $result -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + [IO.Path]::GetFileName($result) | Set-Content -LiteralPath ($result + '.sha256') -Encoding utf8
    Write-Host "Created: $result"
} finally { Pop-Location }
