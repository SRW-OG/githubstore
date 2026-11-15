<#
.SYNOPSIS
  离线准备自包含发布所需的 .NET 运行时包（runtime pack）。

.DESCRIPTION
  自包含发布需要与 SDK 期望版本一致的 Microsoft.NETCore.App.Runtime.win-x64 包。
  当构建机无法联网（nuget.org 不可达）且该版本包不在 NuGet 缓存时，本脚本用本机已安装的
  同版本共享运行时（shared\Microsoft.NETCore.App\<ver>）与 host\fxr\<ver> 组装出一个等价的
  runtime pack，放进一个 fallback 包文件夹，并返回一个 nuget 配置文件路径供 restore/publish 使用。

  仅在以下条件同时满足时合成（否则原样放行，让正常联网场景照旧工作）：
    - 目标运行时包的官方版本（由 SDK 的 TargetingPackVersion 决定）不在 NuGet 缓存；
    - 本机存在该版本共享运行时与匹配的 hostfxr。

  合成产物只用于本机打包，不修改 NuGet 缓存或系统目录，全部落在 artifacts 下。

.PARAMETER Repository
  仓库根目录。

.OUTPUTS
  返回 nuget 配置文件路径（string）；若无需合成或无法合成，返回 $null 表示按默认联网方式。
#>
param(
    [Parameter(Mandatory = $true)][string]$Repository
)

$ErrorActionPreference = 'Stop'

function Get-SdkRuntimeVersion {
    # 直接从 SDK 安装目录读取 BundledVersions，取 TargetingPackVersion（运行时包版本基准）。
    # 不调用 dotnet --info，避免依赖 CLI 首次运行初始化。
    # 优先使用 global.json 指定的 SDK（项目实际使用的），否则取最高版本 8.x（项目目标为 net8.0）。
    $sdkBase = 'C:\Program Files\dotnet\sdk'
    if ($env:DOTNET_ROOT) { $sdkBase = Join-Path $env:DOTNET_ROOT 'sdk' }
    if (-not (Test-Path $sdkBase)) { return $null }

    $preferred = $null
    $globalJson = Join-Path $Repository 'global.json'
    if (Test-Path $globalJson) {
        try {
            $wanted = (Get-Content $globalJson -Raw | ConvertFrom-Json).sdk.version
            if ($wanted) {
                # rollForward 常见为 latestPatch/latestFeature：匹配相同 major.minor 的最高补丁。
                $prefix = ($wanted -split '\.')[0..1] -join '.'
                $preferred = Get-ChildItem $sdkBase -Directory |
                    Where-Object { $_.Name -like "$prefix.*" } | Sort-Object Name -Descending | Select-Object -First 1
            }
        } catch { }
    }
    $ordered = @()
    if ($preferred) { $ordered += $preferred }
    # 项目面向 net8.0：只看 8.x SDK，避免取到 9.x 的运行时版本。
    $ordered += Get-ChildItem $sdkBase -Directory |
        Where-Object { $_.Name -like '8.*' -and ($preferred -eq $null -or $_.Name -ne $preferred.Name) } |
        Sort-Object Name -Descending

    foreach ($dir in $ordered) {
        $props = Join-Path $dir.FullName 'Microsoft.NETCoreSdk.BundledVersions.props'
        if (-not (Test-Path $props)) { continue }
        $m = Select-String -Path $props -Pattern 'TargetingPackVersion="([0-9]+\.[0-9]+\.[0-9]+)"' |
            Select-Object -First 1
        if ($m) { return $m.Matches[0].Groups[1].Value.Trim() }
    }
    return $null
}

$runtimeVersion = Get-SdkRuntimeVersion
if (-not $runtimeVersion) {
    Write-Host 'prepare-offline-runtime: 无法确定 SDK 期望的运行时包版本，跳过离线合成。'
    return $null
}

$rid = 'win-x64'
$packageId = 'microsoft.netcore.app.runtime.win-x64'
$nugetCache = Join-Path $env:USERPROFILE ".nuget\packages"
$cachedPack = Join-Path $nugetCache "$packageId\$runtimeVersion"
if (Test-Path (Join-Path $cachedPack 'runtimes\win-x64\native\coreclr.dll')) {
    # 缓存里已有完整运行时包，无需合成。
    return $null
}

$sharedRuntime = "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\$runtimeVersion"
$hostFxr = "C:\Program Files\dotnet\host\fxr\$runtimeVersion\hostfxr.dll"
if (-not (Test-Path $sharedRuntime)) {
    Write-Host "prepare-offline-runtime: 本机缺少共享运行时 $runtimeVersion，无法离线合成（需要联网）。"
    return $null
}
if (-not (Test-Path $hostFxr)) {
    Write-Host "prepare-offline-runtime: 本机缺少 hostfxr $runtimeVersion，无法离线合成（需要联网）。"
    return $null
}

# 找一个可复制的模板 runtime pack：优先已缓存的其它 8.0.x 版本，用它的目录结构做骨架。
$template = Get-ChildItem (Join-Path $nugetCache $packageId) -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '^8\.0\.\d+$' -and $_.Name -ne $runtimeVersion } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $template) {
    Write-Host "prepare-offline-runtime: 缺少可复用的 8.0.x 运行时包模板，无法离线合成。"
    return $null
}

Write-Host "prepare-offline-runtime: 用共享运行时 $runtimeVersion 合成 $rid 运行时包（模板 $($template.Name)）。"
$staging = Join-Path $Repository "artifacts\offline-runtime\$packageId\$runtimeVersion"
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
Copy-Item -Recurse $template.FullName $staging

# 清理模板自带的包元数据（避免误认为原版本）。
Get-ChildItem $staging -Filter '*.nupkg' -ErrorAction SilentlyContinue | Remove-Item -Force
Get-ChildItem $staging -Filter '*.sha512' -ErrorAction SilentlyContinue | Remove-Item -Force
Remove-Item (Join-Path $staging '.nupkg.metadata') -ErrorAction SilentlyContinue
Remove-Item (Join-Path $staging '.signature.p7s') -ErrorAction SilentlyContinue

$native = Join-Path $staging "runtimes\$rid\native"
$lib = Join-Path $staging "runtimes\$rid\lib\net8.0"

# 用共享运行时里的真实文件覆盖 native（共享运行时把 native+managed 平铺在一起）。
foreach ($file in Get-ChildItem $native -File) {
    $src = Join-Path $sharedRuntime $file.Name
    if (Test-Path $src) { Copy-Item $src $file.FullName -Force }
}
# 补 hostfxr（共享运行时不含它，来自 host\fxr）。
Copy-Item $hostFxr (Join-Path $native 'hostfxr.dll') -Force
# mscordaccore 带版本号，文件名会随版本变化：把新的以目标名落位，同时保留模板原名以兼容 deps 引用。
$accore = Get-ChildItem $sharedRuntime -Filter "mscordaccore_amd64_amd64_*.dll" | Select-Object -First 1
if ($accore) {
    $accoreTarget = Join-Path $native $accore.Name
    Copy-Item $accore.FullName $accoreTarget -Force
    foreach ($stale in Get-ChildItem $native -Filter 'mscordaccore_amd64_amd64_*.dll' |
             Where-Object { $_.Name -ne $accore.Name }) {
        # deps 清单可能仍引用旧名；提供同内容副本，避免复制阶段找不到文件。
        Copy-Item $accore.FullName $stale.FullName -Force
    }
}
# 托管程序集用共享运行时覆盖。
foreach ($file in Get-ChildItem $lib -File) {
    $src = Join-Path $sharedRuntime $file.Name
    if (Test-Path $src) { Copy-Item $src $file.FullName -Force }
}
# 修正 nuspec/versions 的版本号。
foreach ($nuspec in Get-ChildItem $staging -Filter '*.nuspec') {
    (Get-Content $nuspec.FullName -Raw) -replace '<version>[^<]+</version>', "<version>$runtimeVersion</version>" |
        Set-Content $nuspec.FullName -Encoding UTF8
}
$versions = Join-Path $staging 'Microsoft.NETCore.App.versions.txt'
if (Test-Path $versions) { (Get-Content $versions -Raw) -replace '8\.0\.\d+', $runtimeVersion | Set-Content $versions -Encoding UTF8 }
'{"version":2,"contentHash":"offline","source":"offline"}' | Set-Content (Join-Path $staging '.nupkg.metadata') -Encoding UTF8

$fallbackRoot = Join-Path $Repository 'artifacts\offline-runtime-fallback'
if (Test-Path $fallbackRoot) { Remove-Item -Recurse -Force $fallbackRoot }
$fallbackPack = Join-Path $fallbackRoot "$packageId\$runtimeVersion"
$null = New-Item -ItemType Directory -Path $fallbackPack -Force
Copy-Item (Join-Path $staging '*') $fallbackPack -Recurse -Force

$configPath = Join-Path $Repository 'artifacts\offline-nuget.config'
@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <fallbackPackageFolders>
    <clear />
    <add key="offline" value="$fallbackRoot" />
  </fallbackPackageFolders>
  <packageSources>
    <clear />
    <add key="cache" value="$nugetCache" />
  </packageSources>
</configuration>
"@ | Set-Content $configPath -Encoding UTF8

Write-Host "prepare-offline-runtime: 已生成离线 fallback 与 nuget 配置：$configPath"
return $configPath
