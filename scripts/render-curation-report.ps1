param(
    [string]$SeedPath = 'src\GitHubStore.Core\Catalog\CuratedCatalog.seed.json',
    [string]$AuditA = 'data\curation\audit-a.json',
    [string]$AuditB = 'data\curation\audit-b.json',
    [string]$OutFile = 'docs/CURATION.md'
)
$ErrorActionPreference = 'Stop'

$categoryNames = @{
    'dev-tools' = '开发工具'; 'system' = '系统工具'; 'network' = '网络'; 'media' = '媒体影音'
    'graphics' = '图形设计'; 'office' = '办公效率'; 'security' = '安全隐私'; 'games' = '游戏'
    'ai' = 'AI / LLM 工具'; 'terminal' = '终端与 Shell'; 'files' = '文件管理'; 'science' = '教育与科学'
}
$platformNames = @{ 'zhihu' = '知乎'; 'bilibili' = 'B 站'; 'xiaohongshu' = '小红书'; 'github' = 'GitHub 官方项目' }
$evidenceNames = @{
    'indexed-excerpt' = '搜索索引摘要（非全文）'
    'platform-text' = '平台正文或简介（非完整视频/图片内容）'
    'search-snippet' = '搜索摘要（非全文）'
    'title-only' = '仅标题（非全文）'
    'full-text' = '全文正文'
}

$seed = Get-Content -LiteralPath $SeedPath -Raw | ConvertFrom-Json
$audits = @()
foreach ($path in @($AuditA, $AuditB)) {
    if (Test-Path -LiteralPath $path) { $audits += (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).entries }
}

$starredAudit = $null
$starredAuditPath = 'data/curation/starred-audit.json'
if (Test-Path -LiteralPath $starredAuditPath) {
    $starredAudit = Get-Content -LiteralPath $starredAuditPath -Raw | ConvertFrom-Json
}
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# 软件库与来源核验')
$lines.Add('')
$lines.Add('本页由策展数据和官方仓库快照生成，记录当前软件库、本轮筛选结果与历史核验结论。返回 [项目说明](../README.md)。')
$lines.Add('')
$lines.Add('> 原有推荐来源：知乎、B 站、小红书；本轮新增来源：账号 GitHub 收藏（Star）的官方仓库。')
$lines.Add('> 本轮按用户指定筛选：仓库 2026 年仍在更新、最近一年内发布 GitHub Windows 图形程序包、且提供桌面图形界面；纯命令行工具不入库。')
$lines.Add('> 核验范围限官方仓库身份、许可标识、发行时间、发行资产与项目说明中的界面证据，未执行第三方软件安装或安全审计。')
$lines.Add('')
$lines.Add('原有 9 款软件保留；本轮按上述条件新增条目，历史候选记录保留用于追溯。')
$lines.Add('')
$lines.Add("入库条目：**$($seed.entries.Count)**；历史核验记录：**$($audits.Count)**。生成时间：$($seed.curatedAt)。")
$lines.Add('')
$lines.Add('## 入库软件')
$lines.Add('')
$lines.Add('| 软件 | 仓库 | 分类 | 许可证 | 最近发行 | 推荐来源 |')
$lines.Add('|---|---|---|---|---|---|')
foreach ($group in ($seed.entries | Group-Object categoryId | Sort-Object Name)) {
    foreach ($entry in ($group.Group | Sort-Object { $_.repository.full_name })) {
        $full = $entry.repository.full_name
        $license = $entry.repository.license.spdx_id
        if ([string]::IsNullOrWhiteSpace($license)) { $license = '见仓库内许可文件' }
        $release = if ($entry.release) { $entry.release.tag_name } else { '无 GitHub 发行' }
        $platforms = @($entry.curation.recommendationSources | ForEach-Object {
            $name = $platformNames[$_.platform.ToLowerInvariant()]
            if ($name) { $name } else { $_.platform }
        } | Select-Object -Unique) -join '、'
        $category = $categoryNames[$group.Name]
        if (-not $category) { $category = $group.Name }
        $lines.Add("| $($entry.repository.name) | [$full](https://github.com/$full) | $category | $license | $release | $platforms |")
    }
}
$lines.Add('')
if ($starredAudit) {
    $lines.Add('## 收藏仓库本轮筛选')
    $lines.Add('')
    $lines.Add('筛选依据见[本轮核验记录](../data/curation/starred-audit.json)。漏斗如下：')
    $lines.Add('')
    $lines.Add('| 阶段 | 数量 |')
    $lines.Add('|---|---|')
    $f = $starredAudit.funnel
    $lines.Add("| 收藏仓库总数 | $($f.starredTotal) |")
    $lines.Add("| 未归档且非 fork | $($f.activeNotArchived) |")
    $lines.Add("| 2026 年仍有推送 | $($f.pushedIn2026) |")
    $lines.Add("| 取到发行快照 | $($f.releaseSnapshot) |")
    $lines.Add("| 发行含 Windows 程序包 | $($f.windowsPkgCandidates) |")
    $lines.Add("| 其中 2026 年发布 | $($f.candidatesIn2026) |")
    $lines.Add("| 项目说明判定为桌面图形界面 | $($f.guiClassified) |")
    $lines.Add("| 通过资产复核、可入库 | $($f.kept) |")
    $lines.Add("| 本次新增（已在库的本地发送跳过） | $($f.newlyAdded) |")
    $lines.Add('')
    if (@($starredAudit.excluded).Count -gt 0) {
        $lines.Add('通过图形界面判定、但发行资产复核不通过而未入库的候选：')
        $lines.Add('')
        $lines.Add('| 仓库 | 原因 |')
        $lines.Add('|---|---|')
        foreach ($item in $starredAudit.excluded) {
            $reason = ($item.reason -replace '\|', '/' -replace '\s+', ' ').Trim()
            $lines.Add("| [$($item.repository)](https://github.com/$($item.repository)) | $reason |")
        }
        $lines.Add('')
    }
}
$lines.Add('## 历史候选核验记录')
$lines.Add('')
$lines.Add('以下是首批研究时按开源软件标准形成的原始结论，保留用于追溯；不代替本轮按发行时间、Windows 程序包及图形界面的筛选结果。')
$lines.Add('')
$lines.Add('| 候选 | 结论 | 原因 |')
$lines.Add('|---|---|---|')
foreach ($entry in ($audits | Where-Object { $_.status -ne 'verified' } | Sort-Object name)) {
    $status = if ($entry.status -eq 'excluded') { '排除' } else { '待复核' }
    # 说明里那句固定核验范围已在页首统一声明，逐条重复会淹没真正的原因。
    $notes = @($entry.notes | Where-Object { $_ -notmatch '核验范围仅限' }) -join ' '
    $licenseNotes = @($entry.license.notes) -join ' '
    if (-not [string]::IsNullOrWhiteSpace($licenseNotes)) { $notes = "$notes $licenseNotes".Trim() }
    $notes = $notes -replace '\|', '/'
    $notes = $notes -replace '\.license\.json', '许可证接口'
    $notes = $notes -replace '\s+', ' '
    if ([string]::IsNullOrWhiteSpace($notes)) { $notes = '见核验记录' }
    $lines.Add("| $($entry.name) | $status | $notes |")
}
$lines.Add('')
$lines.Add('## 来源与限制')
$lines.Add('')
$lines.Add('| 项目 | 实际情况 |')
$lines.Add('|---|---|')
$lines.Add('| 知乎 | 经公开搜索读取，仅取得搜索索引摘要，**未读取全文** |')
$lines.Add('| B 站 / 小红书 | 经 catbus 读取笔记正文与视频简介；**未观看视频或识别图片内文字** |')
$lines.Add('| catbus 平台范围 | 支持 10 个平台，**不含知乎**；知乎改用公开搜索 |')
$lines.Add('| 推荐来源的价值 | 只证明“该来源提到了这个软件”，不证明软件安全、好用或适合所有人 |')
$lines.Add('| 许可证结论 | 以仓库内实际许可文件为准；混合、自定义、双许可及闭源发行项目如实注明，不按单一开源许可证宣称 |')
$lines.Add('| 本轮入库条件 | 账号收藏的官方仓库、2026 年仍有更新、最近一年发行 Windows 图形程序包、项目说明有桌面界面证据；纯 CLI、本地 Web 界面与服务端程序均不满足 |')
$lines.Add('')
$lines.Add('完整原始证据：[知乎来源](../data/curation/sources-zhihu.json) · [社媒来源](../data/curation/sources-social.json) · [核验记录 A](../data/curation/audit-a.json) · [核验记录 B](../data/curation/audit-b.json) · [收藏筛选记录](../data/curation/starred-audit.json) · [内置软件库资源](../src/GitHubStore.Core/Catalog/CuratedCatalog.seed.json)')
$lines.Add('')

$null = New-Item -ItemType Directory -Path (Split-Path -Parent $OutFile) -Force
Set-Content -LiteralPath $OutFile -Value ($lines -join "`n") -Encoding utf8
Write-Output "Wrote $OutFile lines=$($lines.Count)"
