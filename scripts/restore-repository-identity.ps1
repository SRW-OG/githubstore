param(
    [string]$SeedPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\GitHubStore.Core\Catalog\CuratedCatalog.seed.json'),
    [Parameter(Mandatory)][string]$BatchA,
    [Parameter(Mandatory)][string]$BatchB
)
$ErrorActionPreference = 'Stop'

function Read-Raw([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    if ((Get-Item -LiteralPath $path).Length -lt 5) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) } catch { return $null }
}

# Build a lookup of real repository identity keyed by lower-case owner/name.
$identity = @{}
foreach ($file in (Get-ChildItem -LiteralPath $BatchB -Filter '*.repo.json')) {
    $repo = Read-Raw $file.FullName
    if ($null -eq $repo -or [string]::IsNullOrWhiteSpace($repo.full_name)) { continue }
    $identity[$repo.full_name.ToLowerInvariant()] = $repo
}
foreach ($file in (Get-ChildItem -LiteralPath $BatchA -Filter '*.repository.json')) {
    $repo = Read-Raw $file.FullName
    if ($null -eq $repo -or [string]::IsNullOrWhiteSpace($repo.full_name)) { continue }
    $identity[$repo.full_name.ToLowerInvariant()] = $repo
}
Write-Output "identity snapshots available=$($identity.Count)"

$document = Get-Content -LiteralPath $SeedPath -Raw | ConvertFrom-Json
$restored = 0
foreach ($entry in $document.entries) {
    $repo = $entry.repository
    $key = "$($repo.owner.login)/$($repo.name)".ToLowerInvariant()
    if (-not $identity.ContainsKey($key)) { throw "No raw snapshot for $key" }
    $raw = $identity[$key]
    # Only fill fields that are absent; never overwrite values already taken from the snapshot.
    if (-not ($repo.PSObject.Properties.Name -contains 'id')) { $repo | Add-Member -NotePropertyName id -NotePropertyValue $raw.id }
    if (-not ($repo.PSObject.Properties.Name -contains 'full_name')) { $repo | Add-Member -NotePropertyName full_name -NotePropertyValue $raw.full_name }
    if (-not ($repo.PSObject.Properties.Name -contains 'html_url')) { $repo | Add-Member -NotePropertyName html_url -NotePropertyValue $raw.html_url }
    if (-not ($repo.PSObject.Properties.Name -contains 'node_id') -and $raw.PSObject.Properties.Name -contains 'node_id') { $repo | Add-Member -NotePropertyName node_id -NotePropertyValue $raw.node_id }
    if ($repo.id -ne $raw.id) { throw "id mismatch for $key" }
    if ($repo.full_name -ne $raw.full_name) { throw "full_name mismatch for $key" }
    $restored++
}

$json = $document | ConvertTo-Json -Depth 100
$null = $json | ConvertFrom-Json
Set-Content -LiteralPath $SeedPath -Value $json -Encoding utf8
Write-Output "Restored identity for $restored entries; bytes=$((Get-Item -LiteralPath $SeedPath).Length)"
