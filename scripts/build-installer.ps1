param(
    # 版本号同时驱动程序集版本、安装包文件名与安装器清单；三处必须来自同一个值。
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')][string]$TagPrefix = 'v',
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
# AppVersion 是自更新读取的权威版本：程序集版本、Inno Setup 清单与安装包名都取自它。
# 只传 -p:Version= 也兼容，但显式传 AppVersion 可避免 SDK 拼出带 build 元数据的字符串。
& dotnet publish (Join-Path $repository 'src\GitHubStore.App\GitHubStore.App.csproj') `
    -c Release -r win-x64 -p:Platform=x64 --self-contained true `
    -p:WindowsAppSDKSelfContained=true -p:PublishSingleFile=false -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false "-p:AppVersion=$Version" "-p:Version=$Version" `
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
    "/DIconPath=$(Join-Path $repository 'src\GitHubStore.App\Assets\githubstore.ico')" `
    (Join-Path $repository 'packaging\GitHubStore.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) { throw 'Expected installer was not produced.' }
# 自更新按“官方发行版随附同名 .sha256 资产”校验安装包，因此这个文件是更新链的一部分，
# 不是可选附带物：缺少它时客户端会拒绝下载更新。
$hash = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumName = [System.IO.Path]::GetFileName($installer) + '.sha256'
# 格式与 sha256sum 一致（<摘要><两空格><文件名>），客户端按此解析并把文件名与安装包核对。
"$hash  $([System.IO.Path]::GetFileName($installer))" | Set-Content -LiteralPath (Join-Path $outputDirectory $checksumName) -Encoding ascii

# 端到端确认版本号真的进了产物。这里只读 PE 元数据，不启动程序：
# 自更新界面把这同一个值当作“当前版本”展示，写不进去就必须在这里失败，
# 而不是交付一个版本可疑、界面只能显示“未知”的安装包。
$stampedVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo(
    (Join-Path $publishDirectory 'githubstore-ui.dll')).FileVersion
if (-not $stampedVersion) {
    throw 'The published assembly carries no file version; self-update would report an unknown version.'
}
# FileVersion 形如 X.Y.Z.0，去掉构建号后必须与请求的版本一致。
$stampedCore = ($stampedVersion -split '\.')[0..2] -join '.'
if ($stampedCore -ne $Version) {
    throw "Stamped version '$stampedVersion' does not match the requested version '$Version'."
}

$report = [ordered]@{
    version = $Version
    releaseTag = "$TagPrefix$Version"
    architecture = 'win-x64'
    installer = $installer
    installerName = [System.IO.Path]::GetFileName($installer)
    sizeBytes = (Get-Item -LiteralPath $installer).Length
    sha256 = $hash
    checksumAsset = $checksumName
    stampedFileVersion = $stampedVersion
    defaultDirectory = '%LOCALAPPDATA%\Programs\GitHubStore'
    selfContained = $true
    publishDirectory = $publishDirectory
    compiler = $CompilerPath
    timestamp = [DateTimeOffset]::Now.ToString('O')
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDirectory 'installer-build.json') -Encoding utf8
$report | ConvertTo-Json
