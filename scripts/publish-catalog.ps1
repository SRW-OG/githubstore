param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$Push
)
$ErrorActionPreference = 'Stop'
$seedPath = Join-Path $RepositoryRoot 'src\GitHubStore.Core\Catalog\CuratedCatalog.seed.json'
$catalogPath = Join-Path $RepositoryRoot 'catalog\catalog.json'
$catalogTool = Join-Path $RepositoryRoot 'tests\GitHubStore.Core.Tests\GitHubStore.Core.Tests.csproj'

# 1. 从策展数据重建内置精选库。原始快照与来源证据由 build-curated-seed.ps1 校验。
& (Join-Path $PSScriptRoot 'build-curated-seed.ps1') -OutputPath $seedPath
if ($LASTEXITCODE -ne 0) { throw '重建内置精选库失败。' }

# 2. 客户端同步的库文件必须与内置精选库逐字节一致，否则离线与在线内容会不同。
$seed = Get-Content -LiteralPath $seedPath -Raw
$null = $seed | ConvertFrom-Json
Set-Content -LiteralPath $catalogPath -Value $seed -Encoding utf8 -NoNewline
$catalog = Get-Content -LiteralPath $catalogPath -Raw
if ($catalog -ne $seed) { throw '库文件与内置精选库不一致。' }

# 3. 用客户端自身的解析器验收库文件；最大 8 MiB、schemaVersion 与字段格式都在其中检查。
$env:NuGetAudit = 'false'
& dotnet run --project $catalogTool -c Release -- --result (Join-Path $RepositoryRoot 'artifacts\test-results\catalog-publish.json')
if ($LASTEXITCODE -ne 0) { throw '库文件未通过客户端解析与回归测试。' }

$document = $catalog | ConvertFrom-Json
$summary = [ordered]@{
    catalog = $catalogPath
    bytes = (Get-Item -LiteralPath $catalogPath).Length
    entries = $document.entries.Count
    curatedAt = $document.curatedAt
    categories = (($document.entries | Group-Object categoryId | Sort-Object Name |
        ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' ')
}
$summary | ConvertTo-Json

# 4. 提交并推送后，已安装的客户端下次同步即读取新库，无需重新打包。
if ($Push) {
    git -C $RepositoryRoot add -- 'catalog/catalog.json' 'src/GitHubStore.Core/Catalog/CuratedCatalog.seed.json'
    if ($LASTEXITCODE -ne 0) { throw '暂存库文件失败。' }
    $staged = git -C $RepositoryRoot diff --cached --name-only
    if (-not $staged) { Write-Output '库文件没有变化，无需提交。'; exit 0 }
    git -C $RepositoryRoot commit -m 'chore: update hosted software catalog'
    if ($LASTEXITCODE -ne 0) { throw '提交库文件失败。' }
    git -C $RepositoryRoot -c credential.helper= -c 'credential.helper=!gh auth git-credential' push origin HEAD
    if ($LASTEXITCODE -ne 0) { throw '推送库文件失败。' }
    Write-Output '库文件已推送；客户端下次同步即可获取新版本。'
}
