param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
if (-not $CompilerPath) {
    $candidate = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($candidate) { $CompilerPath = $candidate.Source }
    foreach ($path in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        'C:\Program Files (x86)\Inno Setup 6\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe'
    )) {
        if (-not $CompilerPath -and (Test-Path -LiteralPath $path -PathType Leaf)) { $CompilerPath = $path }
    }
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'Inno Setup 6 compiler is required. Pass -CompilerPath with the full path to ISCC.exe.'
}
# Use a new staging folder for each publish so obsolete build files cannot enter an installer.
$stamp = [guid]::NewGuid().ToString('N')
$publishDirectory = Join-Path $repository "artifacts\publish\win-x64-$stamp"
$outputDirectory = Join-Path $repository 'dist'
$null = New-Item -ItemType Directory -Path $publishDirectory -Force
$null = New-Item -ItemType Directory -Path $outputDirectory -Force
& dotnet publish (Join-Path $repository 'src\GitHubStore.App\GitHubStore.App.csproj') `
    -c Release -r win-x64 -p:Platform=x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" `
    -p:UseSharedCompilation=false -m:1 -nr:false -o $publishDirectory -v:minimal
if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed: $LASTEXITCODE" }
foreach ($required in @('githubstore-ui.exe', 'githubstore-ui.dll', 'coreclr.dll', 'hostfxr.dll',
    'Microsoft.UI.Xaml.dll', 'githubstore-ui.runtimeconfig.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required) -PathType Leaf)) {
        throw "Incomplete self-contained publish: $required"
    }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'githubstore-ui.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks) {
    throw 'The publish still requires an external .NET runtime.'
}
$installer = Join-Path $outputDirectory "GitHubStore-Setup-$Version-win-x64.exe"
# Do not silently replace an already delivered installer.
if (Test-Path -LiteralPath $installer) { throw "Installer already exists: $installer. Choose a new version or archive it first." }
& $CompilerPath "/DAppVersion=$Version" "/DPublishDir=$publishDirectory" "/DInstallerOutputDir=$outputDirectory" `
    (Join-Path $repository 'packaging\GitHubStore.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Expected installer was not produced.' }
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $([System.IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath "$installer.sha256" -Encoding ascii
$report = [ordered]@{
    version = $Version
    architecture = 'win-x64'
    installer = $installer
    sizeBytes = (Get-Item -LiteralPath $installer).Length
    sha256 = $hash
    defaultDirectory = '%LOCALAPPDATA%\Programs\GitHubStore'
    selfContained = $true
    publishDirectory = $publishDirectory
    compiler = $CompilerPath
    timestamp = [DateTimeOffset]::Now.ToString('O')
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDirectory 'installer-build.json') -Encoding utf8
$report | ConvertTo-Json
