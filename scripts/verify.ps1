param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64', 'ARM64')][string]$Platform = 'x64',
    [switch]$InProcess
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repositoryRoot
try {
    $null = New-Item -ItemType Directory -Path (Join-Path $repositoryRoot 'artifacts\test-results') -Force
    dotnet restore GitHubStore.sln "-p:Platform=$Platform" --disable-parallel -m:1 -nr:false
    if ($LASTEXITCODE -ne 0) { throw 'NuGet 还原失败。' }

    dotnet build GitHubStore.sln -c $Configuration "-p:Platform=$Platform" --no-restore -m:1 -nr:false -p:UseSharedCompilation=false -v:minimal "-flp:logfile=artifacts\build-$Configuration-$Platform.log;verbosity=minimal" "-bl:artifacts\build-$Configuration-$Platform.binlog"
    if ($LASTEXITCODE -ne 0) { throw '解决方案构建失败。' }

    # 测试为 AnyCPU 的纯托管测试，在当前宿主执行，不启动 WinUI 窗口。
    if ($InProcess) {
        dotnet run --project tests\GitHubStore.Core.Tests\GitHubStore.Core.Tests.csproj -c $Configuration --no-build --no-restore -- --result "artifacts\test-results\inprocess-tests-$Configuration-$Platform.json"
    }
    else {
        dotnet test tests\GitHubStore.Core.Tests\GitHubStore.Core.Tests.csproj -c $Configuration --no-build --no-restore -m:1 -nr:false --logger "trx;LogFileName=core-tests-$Configuration-$Platform.trx" --results-directory artifacts\test-results
    }
    if ($LASTEXITCODE -ne 0) { throw '基础测试失败。' }
}
finally {
    Pop-Location
}
