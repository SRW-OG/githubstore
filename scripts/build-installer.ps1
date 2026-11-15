param(
    # 版本号同时驱动程序集版本、安装包文件名与安装器清单；三处必须来自同一个值。
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.1.0',
    [ValidatePattern('^[A-Za-z0-9._-]{1,64}$')][string]$TagPrefix = 'v',
    [string]$CompilerPath,
    # 是否打包为自包含（随包携带 .NET 运行时）。'true' 要求能联网获取 8.0.x 运行时包；
    # 离线环境可设 'false' 走 framework-dependent（要求目标机已装 .NET 8 桌面运行时）。
    [ValidateSet('true', 'false')][string]$SelfContained = 'true',
    # 传递 -NoRestore：离线且已完成 restore 时复用现有 assets，避免联网还原失败。
    [switch]$NoRestore,
    # 显式指定 MSBuild.exe；默认自动查找 Visual Studio 的 MSBuild。
    [string]$MSBuildPath
)
$ErrorActionPreference = 'Stop'
$isSelfContained = ($SelfContained -eq 'true')
$repository = Split-Path -Parent $PSScriptRoot
# 隔离 dotnet 首次运行哨兵，避免默认用户目录无权写入导致 CLI 启动失败。
$env:DOTNET_CLI_HOME = Join-Path $repository '.dotnet_home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
if (-not (Test-Path $env:DOTNET_CLI_HOME)) { $null = New-Item -ItemType Directory -Path $env:DOTNET_CLI_HOME -Force }
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
# 解析 MSBuild：本机 dotnet CLI 可能因首次运行初始化失败，优先用 Visual Studio 自带的 MSBuild.exe。
# 也可用 -MSBuildPath 显式指定。找不到时回退 dotnet msbuild。
function Resolve-MSBuild {
    param([string]$Explicit)
    if ($Explicit -and (Test-Path -LiteralPath $Explicit -PathType Leaf)) { return $Explicit }
    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null |
            Select-Object -First 1
        if ($found -and (Test-Path -LiteralPath $found -PathType Leaf)) { return $found }
    }
    foreach ($path in @(
        'C:\MicrosoftVisualStudio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
    )) { if (Test-Path -LiteralPath $path -PathType Leaf) { return $path } }
    return $null
}
$msbuildPath = Resolve-MSBuild -Explicit $MSBuildPath
if (-not $msbuildPath) { throw 'MSBuild.exe not found. Install Visual Studio Build Tools or pass -MSBuildPath.' }
Write-Host "使用 MSBuild：$msbuildPath"

$projectFile = Join-Path $repository 'src\GitHubStore.App\GitHubStore.App.csproj'
# 自包含发布在离线环境可能缺少与 SDK 匹配的运行时包。尝试离线合成 fallback：
# 若需要并可用，就用它提供的 nuget 配置先 restore，再发布，避免联网失败。
$offlineConfig = $null
if ($isSelfContained -and -not $NoRestore) {
    $prepareScript = Join-Path $PSScriptRoot 'prepare-offline-runtime.ps1'
    if (Test-Path $prepareScript) {
        # 子进程可能带日志输出；取最后一行可解析为现存文件的路径作为 nuget 配置。
        $prepareOutput = & powershell -NoProfile -ExecutionPolicy Bypass -File $prepareScript -Repository $repository
        $candidate = @($prepareOutput) | Where-Object { $_ -and (Test-Path -LiteralPath $_.ToString() -PathType Leaf) } |
            Select-Object -Last 1
        if ($candidate) { $offlineConfig = $candidate.ToString().Trim() }
    }
}

$commonProps = @{
    Configuration = 'Release'
    Platform = 'x64'
    RuntimeIdentifier = 'win-x64'
    NuGetAudit = 'false'
    UseSharedCompilation = 'false'
}
if ($offlineConfig) {
    Write-Host "使用离线运行时 fallback 配置：$offlineConfig"
    $commonProps.RestoreConfigFile = $offlineConfig
}
# 1) 还原：已有 assets 时可跳过（-NoRestore）；否则执行 Restore。
if (-not $NoRestore) {
    $restoreArgs = @($projectFile, '-t:Restore', '-nologo', '-v:minimal')
    foreach ($kv in $commonProps.GetEnumerator()) { $restoreArgs += "-p:$($kv.Key)=$($kv.Value)" }
    & $msbuildPath @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw "Restore failed: $LASTEXITCODE" }
}
# 2) 发布：用 MSBuild 的 Publish 目标生成自包含/framework-dependent 输出。
$publishArgs = @($projectFile, '-t:Publish', '-nologo', '-v:minimal', '-m:1')
foreach ($kv in $commonProps.GetEnumerator()) { $publishArgs += "-p:$($kv.Key)=$($kv.Value)" }
$publishArgs += @(
    "-p:SelfContained=$($isSelfContained.ToString().ToLowerInvariant())",
    '-p:WindowsAppSDKSelfContained=true', '-p:PublishSingleFile=false', '-p:PublishTrimmed=false',
    '-p:DebugType=None', '-p:DebugSymbols=false',
    "-p:AppVersion=$Version", "-p:Version=$Version",
    "-p:PublishDir=$publishDirectory\"
)
& $msbuildPath @publishArgs
if ($LASTEXITCODE -ne 0) { throw "Publish failed: $LASTEXITCODE" }
# 自包含发布必须携带完整运行时；framework-dependent 只要求程序集与 WindowsAppSDK。
if ($isSelfContained) {
    foreach ($required in @('githubstore-ui.exe', 'githubstore-ui.dll', 'coreclr.dll', 'hostfxr.dll',
        'Microsoft.UI.Xaml.dll', 'githubstore-ui.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required) -PathType Leaf)) {
            throw "Incomplete self-contained publish: $required"
        }
    }
} else {
    foreach ($required in @('githubstore-ui.exe', 'githubstore-ui.dll',
        'Microsoft.UI.Xaml.dll', 'githubstore-ui.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $required) -PathType Leaf)) {
            throw "Incomplete framework-dependent publish: $required"
        }
    }
}
$runtimeConfig = Get-Content -LiteralPath (Join-Path $publishDirectory 'githubstore-ui.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($isSelfContained) {
    if ($runtimeConfig.runtimeOptions.framework -or $runtimeConfig.runtimeOptions.frameworks) {
        throw 'The publish still requires an external .NET runtime.'
    }
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
    selfContained = $isSelfContained
    publishDirectory = $publishDirectory
    compiler = $CompilerPath
    timestamp = [DateTimeOffset]::Now.ToString('O')
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDirectory 'installer-build.json') -Encoding utf8
$report | ConvertTo-Json

