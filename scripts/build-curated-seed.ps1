param(
    [string]$CurationDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'data\curation'),
    [string]$OutputPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\GitHubStore.Core\Catalog\CuratedCatalog.seed.json'),
    [string]$BatchA = (Join-Path (Split-Path -Parent $PSScriptRoot) 'data\curation'),
    [string]$BatchB = (Join-Path (Split-Path -Parent $PSScriptRoot) 'data\curation')
)
$ErrorActionPreference = 'Stop'
$knownCategories = @('dev-tools','system','network','media','graphics','office','security','games','ai','terminal','files','science')
# 证据级别由实际采集方式决定，不是估计值：知乎来自搜索索引摘要，小红书/B站来自接口返回的正文或视频简介。
$evidenceByPlatform = @{ 'zhihu' = 'indexed-excerpt'; 'xiaohongshu' = 'platform-text'; 'bilibili' = 'platform-text' }

function Assert-HttpsUrl([string]$value, [string]$context) {
    $uri = $null
    if (-not [uri]::TryCreate($value, [System.UriKind]::Absolute, [ref]$uri)) { throw "$context is not an absolute URI: $value" }
    if ($uri.Scheme -ne 'https') { throw "$context must use https: $value" }
    if (-not [string]::IsNullOrEmpty($uri.UserInfo)) { throw "$context must not contain credentials: $value" }
}

function Read-Raw([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    if ((Get-Item -LiteralPath $path).Length -lt 5) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) } catch { return $null }
}

# 原始 GitHub 快照提供真实仓库身份字段，用于补回分片里缺失的 id/full_name/html_url。
$identity = @{}
foreach ($pattern in @(@($BatchB, '*.repo.json'), @($BatchA, '*.repository.json'))) {
    $batch = $pattern[0]
    if (-not (Test-Path -LiteralPath $batch)) { continue }
    foreach ($file in (Get-ChildItem -LiteralPath $batch -Filter $pattern[1])) {
        $repo = Read-Raw $file.FullName
        if ($null -eq $repo -or [string]::IsNullOrWhiteSpace($repo.full_name)) { continue }
        $identity[$repo.full_name.ToLowerInvariant()] = $repo
    }
}

$entries = @()
$selection = Get-Content -LiteralPath (Join-Path $CurationDirectory 'selection.json') -Raw | ConvertFrom-Json
if ($selection.schemaVersion -ne 1 -or @($selection.repositories).Count -eq 0) { throw 'Invalid selection manifest.' }
$selectedKeys = @($selection.repositories | ForEach-Object { $_.ToLowerInvariant() })
$seenKeys = @{}
# 分片按名称自动发现，新增策展批次无需再改脚本。
$partNames = @(Get-ChildItem -LiteralPath $CurationDirectory -Filter 'seed-part-*.json' -File |
    Sort-Object Name | ForEach-Object { $_.Name })
if ($partNames.Count -eq 0) { throw 'No seed parts found.' }
foreach ($partName in $partNames) {
    $partPath = Join-Path $CurationDirectory $partName
    if (-not (Test-Path -LiteralPath $partPath -PathType Leaf)) { throw "Missing input: $partPath" }
    $part = Get-Content -LiteralPath $partPath -Raw | ConvertFrom-Json
    if ($part.schemaVersion -ne 1) { throw "$partName has an unexpected schemaVersion." }
    foreach ($entry in $part.entries) {
        $repository = $entry.repository
        if ($null -eq $repository) { throw "$partName contains an entry without a repository snapshot." }
        $owner = $repository.owner.login
        $name = $repository.name
        if ([string]::IsNullOrWhiteSpace($owner) -or [string]::IsNullOrWhiteSpace($name)) { throw "$partName contains a repository without owner/name." }
        $key = "$owner/$name".ToLowerInvariant()
        if ($key -notin $selectedKeys) { continue }
        if ($seenKeys.ContainsKey($key)) { throw "Duplicate repository across parts: $key" }
        if ($knownCategories -notcontains $entry.categoryId) { throw "Unknown category for ${key}: $($entry.categoryId)" }

        # 补回真实仓库身份，绝不合成缺失字段。
        if ($identity.ContainsKey($key)) {
            $raw = $identity[$key]
            foreach ($field in @('id','node_id','full_name','html_url')) {
                if (($repository.PSObject.Properties.Name -notcontains $field) -and ($raw.PSObject.Properties.Name -contains $field)) {
                    $repository | Add-Member -NotePropertyName $field -NotePropertyValue $raw.$field
                }
            }
        }
        foreach ($field in @('id','full_name','html_url')) {
            if ($repository.PSObject.Properties.Name -notcontains $field) { throw "Repository $key is missing real identity field: $field" }
        }
        if ($repository.full_name.ToLowerInvariant() -ne $key) { throw "Repository identity mismatch for $key" }

        $curation = $entry.curation
        if ($null -eq $curation) { throw "Missing curation for $key" }
        if (@($curation.recommendationSources).Count -eq 0) { throw "No recommendation sources for $key" }
        $verification = $curation.verification
        if ($null -eq $verification) { throw "Missing verification for $key" }
        if ($verification.scope -ne 'repository-license-windows-metadata') { throw "Unexpected verification scope for ${key}: $($verification.scope)" }
        Assert-HttpsUrl $verification.repositoryUrl "verification.repositoryUrl ($key)"
        if ($verification.repositoryUrl.TrimEnd('/') -ne "https://github.com/$($repository.full_name)") {
            throw "verification.repositoryUrl does not match the repository for $key"
        }
        if (@($verification.evidenceUrls).Count -eq 0) { throw "No evidence URLs for $key" }
        foreach ($url in $verification.evidenceUrls) { Assert-HttpsUrl $url "verification evidence ($key)" }
        foreach ($source in $curation.recommendationSources) {
            Assert-HttpsUrl $source.url "recommendation source ($key)"
            if ([string]::IsNullOrWhiteSpace($source.platform) -or [string]::IsNullOrWhiteSpace($source.title)) {
                throw "Recommendation source for $key is missing platform or title."
            }
            # 用实际采集平台校正证据级别，避免把平台正文与索引摘要混为一谈。
            $expected = $evidenceByPlatform[$source.platform.ToLowerInvariant()]
            if ($expected) {
                if ($source.PSObject.Properties.Name -contains 'evidenceLevel') { $source.evidenceLevel = $expected }
                else { $source | Add-Member -NotePropertyName evidenceLevel -NotePropertyValue $expected }
            }
            if ($source.evidenceLevel -notin @('full-text','platform-text','indexed-excerpt','search-snippet','title-only')) {
                throw "Unexpected evidence level for ${key}: $($source.evidenceLevel)"
            }
        }
        $seenKeys[$key] = $true
        $entries += $entry
    }
}

$missing = @($selectedKeys | Where-Object { -not $seenKeys.ContainsKey($_) })
if ($missing.Count -gt 0) { throw "Selected repositories missing: $($missing -join ', ')" }

$document = [ordered]@{
    schemaVersion = 1
    # Keep the timestamp locale-independent so the resource reads the same everywhere.
    curatedAt = [DateTimeOffset]::Now.ToString('yyyy-MM-ddTHH:mm:sszzz', [System.Globalization.CultureInfo]::InvariantCulture)
    entries = $entries
}
$json = $document | ConvertTo-Json -Depth 100
# 发布前先回环解析，确保嵌入资源始终可解析。
$null = $json | ConvertFrom-Json
$null = New-Item -ItemType Directory -Path (Split-Path -Parent $OutputPath) -Force
Set-Content -LiteralPath $OutputPath -Value $json -Encoding utf8
[pscustomobject]@{
    output = $OutputPath
    entries = $entries.Count
    bytes = (Get-Item -LiteralPath $OutputPath).Length
    identitiesFromSnapshots = $identity.Count
    categories = (($entries | Group-Object categoryId | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' ')
} | ConvertTo-Json
