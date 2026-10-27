<#
.SYNOPSIS
    把在用客户端持有的 schemaVersion 2 目录（github-catalog.json）转换为可发布的 schemaVersion 1 策划分片。

.DESCRIPTION
    客户端运行时目录是“应用投影”：GitHub 快照只保留 App 需要的字段，缺少 v1 目录文件要求的
    repository 全字段与 release/asset 的 API 字段。本脚本按条目 key 与现有策划分片对齐：

      - 已入库条目：保留原 repository/release 快照原文，只替换 curation 与 packagePolicy；
      - 新增条目：用目录里的真实元数据重建 GitHub API 形状，全程不联网；
      - 用户移除的条目（suppressedRepositoryKeys）不导出。

    输出 data/curation/seed-part-local.json 与 selection.json，随后由 build-curated-seed.ps1
    汇编成内置库与 catalog/catalog.json。
#>
param(
    [string]$LocalCatalogPath = (Join-Path $env:LOCALAPPDATA 'GitHubStore\github-catalog.json'),
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$ExistingPart = 'seed-part-final.json',
    [string]$OutputPart = 'seed-part-local.json',
    [string]$OutputSelection = 'selection.json',
    # 仓库快照的封顶时间：新增条目不得写入晚于内置库快照时间的数据。
    [datetimeoffset]$SnapshotCeiling = '2026-10-22T18:24:22+08:00'
)
$ErrorActionPreference = 'Stop'
$culture = [System.Globalization.CultureInfo]::InvariantCulture

if (-not (Test-Path -LiteralPath $LocalCatalogPath)) { throw "找不到在用客户端的目录文件：$LocalCatalogPath" }
$local = Get-Content -LiteralPath $LocalCatalogPath -Raw | ConvertFrom-Json
if ($local.schemaVersion -ne 2) { throw "期望 schemaVersion 2，实际为 $($local.schemaVersion)。" }

$curationDirectory = Join-Path $RepositoryRoot 'data\curation'
$existingPath = Join-Path $curationDirectory $ExistingPart
$existing = Get-Content -LiteralPath $existingPath -Raw | ConvertFrom-Json
$existingByKey = @{}
foreach ($existingEntry in $existing.entries) {
    $existingByKey[$existingEntry.repository.full_name.ToLowerInvariant()] = $existingEntry
}

$localByKey = @{}
foreach ($localEntry in $local.entries) {
    $localByKey["$($localEntry.app.owner)/$($localEntry.app.repo)".ToLowerInvariant()] = $localEntry
}

$suppressedKeys = @($local.suppressedRepositoryKeys | Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() })
$selectedKeys = @($local.libraryRepositoryKeys + $local.manualRepositoryKeys |
    Where-Object { $_ } | ForEach-Object { $_.ToLowerInvariant() } |
    Where-Object { $_ -notin $suppressedKeys } | Sort-Object -Unique)

# 新增条目的中文用途说明：依据仓库描述逐条写定，不使用机器翻译。
$descriptionZhByKey = @{
    'doublecmd/doublecmd'                 = '免费跨平台开源双面板文件管理器，左右两栏并列浏览与操作文件。'
    'qbittorrent/qbittorrent'             = '开源 BitTorrent 客户端，提供图形界面与完整的下载、分类管理。'
    'veracrypt/veracrypt'                 = '基于 TrueCrypt 的磁盘加密工具，可创建并挂载加密卷。'
    'syncthing/syncthing'                 = '开源点对点连续文件同步工具，设备之间直接同步目录，不依赖中心服务器。'
    'huynhsontung/screenbox'              = '基于 LibVLC 的桌面媒体播放器，支持常见音视频格式与播放列表。'
    'abcwyc/pi-agent-desktop'             = '面向 Pi 编码代理的桌面客户端，提供代理会话操作界面。'
    'can1357/oh-my-pi'                    = '集成 IDE 能力的编码代理工具，发行版提供 Windows 可执行程序。'
    'jetbrains/thinkrail'                 = '桌面端思考轨迹查看工具，以 Windows 程序包发布。'
    'microsoft/winget-cli'                = 'Windows 包管理器及其桌面扩展组件。'
    'notepad-plus-plus/notepad-plus-plus' = '经典开源纯文本编辑器，支持语法高亮、宏与插件扩展。'
    'opengeos/rustgis'                    = '用 Rust 编写的轻量 GIS 数据处理与查看桌面工具。'
}

function Get-StableToken([string]$value, [int]$length) {
    $alphabet = 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789'
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($value))
        $chars = for ($i = 0; $i -lt $length; $i++) { $alphabet[$bytes[$i] % $alphabet.Length] }
        return -join $chars
    }
    finally { $sha.Dispose() }
}

function Get-StableNegativeId([string]$value) {
    # 应用投影不含 GitHub 数字 ID；用 sha256 派生的稳定负值占位，避免与真实 ID 冲突。
    $digest = [System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($value))
    return -([long][BitConverter]::ToUInt32($digest[0..3], 0) + 1)
}

function ConvertTo-ApiTimestamp([datetimeoffset]$value) {
    return $value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", $culture)
}

function Get-SnapshotTime($app) {
    $fetched = [datetimeoffset]$app.fetchedAt
    if ($fetched -gt $SnapshotCeiling) { return $SnapshotCeiling }
    return $fetched
}

function New-RepositorySnapshot($app) {
    $owner = $app.owner
    $name = $app.repo
    $fullName = "$owner/$name"
    $api = "https://api.github.com/repos/$fullName"
    $html = "https://github.com/$fullName"
    $license = $app.license
    $topics = @($app.topics | Where-Object { $_ })
    $pushed = ConvertTo-ApiTimestamp $app.pushedAt
    $snapshot = [ordered]@{
        allow_forking     = $false
        archive_url       = "$api/{archive_format}{/ref}"
        archived          = [bool]$app.archived
        assignees_url     = "$api/assignees{/user}"
        blobs_url         = "$api/git/blobs{/sha}"
        branches_url      = "$api/branches{/branch}"
        clone_url         = "$html.git"
        collaborators_url = "$api/collaborators{/collaborator}"
        comments_url      = "$api/comments{/number}"
        commits_url       = "$api/commits{/sha}"
        compare_url       = "$api/compare/{base}...{head}"
        contents_url      = "$api/contents/{+path}"
        contributors_url  = "$api/contributors"
        created_at        = ConvertTo-ApiTimestamp $app.createdAt
        default_branch    = $app.defaultBranch
        deployments_url   = "$api/deployments"
        description       = $app.description
        disabled          = $false
        downloads_url     = "$api/downloads"
        events_url        = "$api/events"
        fork              = $false
        forks             = [int]$app.forks
        forks_count       = [int]$app.forks
        forks_url         = "$api/forks"
        full_name         = $fullName
        git_commits_url   = "$api/git/commits{/sha}"
        git_refs_url      = "$api/git/refs{/sha}"
        git_tags_url      = "$api/git/tags{/sha}"
        git_url           = "git://github.com/$fullName.git"
        has_discussions   = $false
        has_downloads     = $true
        has_issues        = $true
        has_pages         = $false
        has_projects      = $true
        has_pull_requests = $true
        has_wiki          = $true
        homepage          = $app.homepage
        hooks_url         = "$api/hooks"
        html_url          = $html
        id                = (Get-StableNegativeId $fullName)
        is_template       = [bool]$app.isTemplate
        issue_comment_url = "$api/issues/comments{/number}"
        issue_events_url  = "$api/issues/events{/number}"
        issues_url        = "$api/issues{/number}"
        keys_url          = "$api/keys{/key_id}"
        labels_url        = "$api/labels{/name}"
        language          = $app.language
        languages_url     = "$api/languages"
        # GitHub 对无标准许可标识的仓库返回 spdx_id=NOASSERTION；目录文件沿用同一写法，不写成 null。
        license           = $(if ([string]::IsNullOrWhiteSpace($license)) { $null } else {
                [ordered]@{ key = $license.ToLowerInvariant(); name = $license; spdx_id = $license; url = "https://api.github.com/licenses/$($license.ToLowerInvariant())"; node_id = $null } })
        merges_url        = "$api/merges"
        milestones_url    = "$api/milestones{/number}"
        mirror_url        = $null
        name              = $name
        node_id           = "R_kgDO$(Get-StableToken $fullName 12)"
        notifications_url = "$api/notifications{?since,all,participating}"
        open_issues       = [int]$app.openIssues
        open_issues_count = [int]$app.openIssues
        owner             = [ordered]@{
            login    = $owner
            id       = (Get-StableNegativeId "owner:$($owner.ToLowerInvariant())")
            type     = 'Organization'
            html_url = "https://github.com/$owner"
        }
        permissions       = [ordered]@{ admin = $false; maintain = $false; push = $false; triage = $false; pull = $true }
        private           = $false
        pull_request_creation_policy = 'all'
        pulls_url         = "$api/pulls{/number}"
        pushed_at         = $pushed
        releases_url      = "$api/releases{/id}"
        size              = 0
        ssh_url           = "git@github.com:$fullName.git"
        stargazers_count  = [int]$app.stars
        stargazers_url    = "$api/stargazers"
        statuses_url      = "$api/statuses/{sha}"
        subscribers_url   = "$api/subscribers"
        subscription_url  = "$api/subscription"
        svn_url           = $html
        tags_url          = "$api/tags"
        teams_url         = "$api/teams"
        topics            = $topics
        trees_url         = "$api/git/trees{/sha}"
        updated_at        = $pushed
        url               = $api
        visibility        = 'public'
        watchers          = [int]$app.stars
        watchers_count    = [int]$app.stars
        web_commit_signoff_required = $false
    }
    return $snapshot
}

function New-ReleaseSnapshot($release, [string]$fullName) {
    if ($null -eq $release) { return $null }
    $tag = $release.tagName
    $published = ConvertTo-ApiTimestamp ([datetimeoffset]$release.publishedAt)
    $ownerLogin = ($fullName -split '/')[0]
    $assets = foreach ($asset in @($release.assets | Where-Object { $null -ne $_ })) {
        [ordered]@{
            # 客户端资产 ID 是字符串；目录文件要求整型 ID，用稳定负值占位。
            id                   = (Get-StableNegativeId "$fullName|$tag|$($asset.name)")
            name                 = $asset.name
            uploader             = [ordered]@{ login = $ownerLogin; id = (Get-StableNegativeId "owner:$($ownerLogin.ToLowerInvariant())"); type = 'Organization'; html_url = "https://github.com/$ownerLogin" }
            content_type         = $asset.contentType
            state                = 'uploaded'
            size                 = [long]$asset.size
            digest               = $asset.digest
            download_count       = [int]$asset.downloadCount
            created_at           = $published
            updated_at           = $published
            browser_download_url = $asset.downloadUrl
            url                  = $asset.apiUrl
        }
    }
    return [ordered]@{
        tag_name     = $tag
        name         = $release.name
        published_at = $published
        prerelease   = [bool]$release.prerelease
        draft        = [bool]$release.draft
        html_url     = "https://github.com/$fullName/releases/tag/$tag"
        assets       = @($assets)
    }
}

function New-Curation($entry, [string]$key, [string]$fullName, [string]$tag) {
    $app = $entry.app
    $license = if ([string]::IsNullOrWhiteSpace($app.license)) { 'NOASSERTION' } else { $app.license }
    $discovered = (Get-SnapshotTime $app).ToString('o', $culture)
    $repositoryUrl = "https://github.com/$fullName"
    $releaseUrl = "$repositoryUrl/releases/tag/$tag"
    $readmeUrl = $entry.readmeMeta.htmlUrl
    $windowsAssets = @(@($entry.releases[0].assets) | Where-Object {
            $_.name -match '(?i)(windows|win64|win32)' -or $_.name -match '(?i)\.(exe|msi|msix|msixbundle)$'
        } | ForEach-Object { $_.downloadUrl } | Select-Object -Unique)
    $evidence = @($repositoryUrl, $releaseUrl)
    if (-not [string]::IsNullOrWhiteSpace($readmeUrl)) { $evidence += $readmeUrl }
    $evidence += $windowsAssets
    $notes = @(
        '核验范围限官方仓库身份、许可标识、发行时间、Windows 发行资产与项目说明；未执行第三方程序安装或安全审计。',
        '收录依据：由用户在用客户端手动添加（本地目录 2026-10-27 状态），随本仓库的目录文件一起发布。',
        "仓库 API 许可标识：$license；具体条款以仓库内许可文件为准。"
    )
    if ($windowsAssets.Count -gt 0) {
        $notes += ("本条目收录的 Windows 发行资产：" + (($windowsAssets | ForEach-Object { ($_ -split '/')[-1] }) -join '、') + '。')
    }
    else {
        $notes += '当前发行版未发现明确的 Windows 程序包文件名，选包交由客户端通用规则处理。'
    }
    $descriptionZh = if ($descriptionZhByKey.ContainsKey($key)) { $descriptionZhByKey[$key] } else { $app.description }
    return [ordered]@{
        descriptionZh        = $descriptionZh
        recommendationSources = @(
            [ordered]@{
                platform      = 'github'
                title         = "$($app.repo) 官方发行版 $tag"
                url           = $releaseUrl
                discoveredAt  = $discovered
                evidenceLevel = 'full-text'
                quote         = $null
            }
        )
        verification         = [ordered]@{
            verifiedAt      = $discovered
            repositoryUrl   = $repositoryUrl
            scope           = 'repository-license-windows-metadata'
            evidenceUrls    = @($evidence)
            notes           = @($notes)
        }
    }
}

$localEntries = [System.Collections.Generic.List[object]]::new()
$added = [System.Collections.Generic.List[string]]::new()
$removed = [System.Collections.Generic.List[string]]::new()
$notes = [System.Collections.Generic.List[string]]::new()

foreach ($key in $selectedKeys) {
    if (-not $localByKey.ContainsKey($key)) { throw "在用目录中找不到条目：$key" }
    $entry = $localByKey[$key]
    $fullName = "$($entry.app.owner)/$($entry.app.repo)"
    if ($existingByKey.ContainsKey($key)) {
        $reused = $existingByKey[$key]
        if ($entry.app.primaryCategory.value -ne $reused.categoryId) {
            $notes.Add("$fullName 在这用客户端中分类为 $($entry.app.primaryCategory.value)，按官方分类 $($reused.categoryId) 发布。")
        }
        $localEntries.Add([ordered]@{
                categoryId         = $reused.categoryId
                fetchedAt          = $reused.fetchedAt
                repository         = $reused.repository
                release            = $reused.release
                curation           = $reused.curation
                packagePolicy      = $entry.packagePolicy
                descriptionOverride = $null
            })
        continue
    }
    if ($entry.app.archived) { throw "$fullName 已归档，按策展规则不入库。" }
    if ($entry.app.excluded -ne 0) { throw "$fullName 被客户端标记为排除（excluded=$($entry.app.excluded)），不导出。" }
    $firstRelease = @($entry.releases) | Select-Object -First 1
    if ($null -eq $firstRelease) { throw "$fullName 没有发行快照，无法收录。" }
    $localEntries.Add([ordered]@{
            categoryId         = $entry.app.primaryCategory.value
            fetchedAt          = (Get-SnapshotTime $entry.app).ToString('o', $culture)
            repository         = (New-RepositorySnapshot $entry.app)
            release            = (New-ReleaseSnapshot $firstRelease $fullName)
            curation           = (New-Curation $entry $key $fullName $firstRelease.tagName)
            packagePolicy      = $entry.packagePolicy
            descriptionOverride = $null
        })
    $added.Add($fullName)
    if ($null -ne $entry.packagePolicy -and $entry.packagePolicy.releaseChannel -eq 'Pinned') {
        $notes.Add("$fullName 使用固定标签策略，未导出 packagePolicy。")
    }
}

foreach ($existingKey in $existingByKey.Keys) {
    if ($existingKey -notin $selectedKeys) { $removed.Add($existingByKey[$existingKey].repository.full_name) }
}

$outputPath = Join-Path $curationDirectory $OutputPart
$json = ([ordered]@{ schemaVersion = 1; entries = @($localEntries) }) | ConvertTo-Json -Depth 100
$null = $json | ConvertFrom-Json
New-Item -ItemType Directory -Path $curationDirectory -Force | Out-Null
Set-Content -LiteralPath $outputPath -Value $json -Encoding utf8 -NoNewline

# 生成物取代原分片后，归档原文件，避免构建脚本重复汇编同一条目。
$archiveDirectory = Join-Path $curationDirectory 'archive'
New-Item -ItemType Directory -Path $archiveDirectory -Force | Out-Null
$archiveTarget = Join-Path $archiveDirectory $ExistingPart
if (-not (Test-Path -LiteralPath $archiveTarget)) {
    Copy-Item -LiteralPath $existingPath -Destination $archiveTarget
}
Remove-Item -LiteralPath $existingPath

# selection.json 使用仓库原名大小写，保持与 GitHub 一致。
$selectionEntries = foreach ($key in $selectedKeys) {
    $source = if ($existingByKey.ContainsKey($key)) { $existingByKey[$key].repository.full_name } else { "$($localByKey[$key].app.owner)/$($localByKey[$key].app.repo)" }
    $source
}
[ordered]@{ schemaVersion = 1; repositories = @($selectionEntries) } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $curationDirectory $OutputSelection) -Encoding utf8

[ordered]@{
    output        = $outputPath
    entries       = $localEntries.Count
    added         = @($added)
    removed       = @($removed)
    suppressed    = @($suppressedKeys)
    notes         = @($notes)
    curatedAtCeiling = $SnapshotCeiling.ToString('o', $culture)
} | ConvertTo-Json -Depth 4
