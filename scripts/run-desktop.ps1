param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Debug',
    [ValidateSet('x64','ARM64')][string]$Platform = 'x64',
    [switch]$PrepareOnly
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$runtime = if ($Platform -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$sourceDirectory = Join-Path $repository "src\GitHubStore.App\bin\$Platform\$Configuration\net8.0-windows10.0.19041.0\$runtime"
$entryName = 'githubstore-ui.exe'
if (-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $entryName) -PathType Leaf)) { throw 'Build the requested configuration before launching.' }
# A workspace may carry an inheritable Low mandatory label. Publish file contents into
# a normal per-user application directory; do not copy security descriptors or alter labels.
$identityFiles = @('githubstore-ui.exe','githubstore-ui.dll','GitHubStore.Core.dll','GitHubStore.Platform.Win32.dll','githubstore-ui.deps.json','githubstore-ui.runtimeconfig.json')
$identity = ($identityFiles | ForEach-Object {
    $path=Join-Path $sourceDirectory $_
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing build file: $_" }
    $_ + ':' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
}) -join "`n"
$buildId=[Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData([System.Text.Encoding]::UTF8.GetBytes($identity))).Substring(0,16)
$applicationRoot=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'Programs\GitHubStore.Dev'
$desktopDirectory=Join-Path $applicationRoot "$runtime-$buildId"
$desktopEntry=Join-Path $desktopDirectory $entryName
$readyMarker=Join-Path $desktopDirectory '.deployment-complete'
if (-not (Test-Path -LiteralPath $readyMarker -PathType Leaf)) {
    if (Test-Path -LiteralPath $desktopDirectory) { throw "Incomplete deployment exists; leaving it untouched: $desktopDirectory" }
    $null=New-Item -ItemType Directory -Path $desktopDirectory -ErrorAction Stop
    foreach ($item in Get-ChildItem -LiteralPath $sourceDirectory -Recurse -File) {
        if ($item.Extension -in @('.log','.dmp')) { continue }
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Unexpected linked build file: $($item.FullName)" }
        $relative=[System.IO.Path]::GetRelativePath($sourceDirectory,$item.FullName)
        $target=Join-Path $desktopDirectory $relative
        $null=[System.IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        $inputFile=[System.IO.File]::OpenRead($item.FullName)
        try {
            $outputFile=[System.IO.File]::Open($target,[System.IO.FileMode]::CreateNew,[System.IO.FileAccess]::Write,[System.IO.FileShare]::None)
            try { $inputFile.CopyTo($outputFile) } finally { $outputFile.Dispose() }
        } finally { $inputFile.Dispose() }
    }
    foreach ($name in $identityFiles) {
        if ((Get-FileHash -LiteralPath (Join-Path $sourceDirectory $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $desktopDirectory $name)).Hash) { throw "Deployment checksum mismatch: $name" }
    }
    [System.IO.File]::WriteAllText($readyMarker,$identity)
}
$record=[ordered]@{sourceDirectory=$sourceDirectory;applicationDirectory=$desktopDirectory;executable=$desktopEntry;buildId=$buildId;configuration=$Configuration;platform=$Platform;timestamp=[DateTimeOffset]::Now.ToString('O')}
$null = New-Item -ItemType Directory -Path (Join-Path $repository 'artifacts') -Force
$record | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repository 'artifacts\desktop-deployment.json') -Encoding utf8
$record | ConvertTo-Json
if ($PrepareOnly) { exit 0 }
$start=[System.Diagnostics.ProcessStartInfo]::new($desktopEntry)
$start.UseShellExecute=$false
$start.WorkingDirectory=$desktopDirectory
$null=$start.Environment.Remove('GITHUBSTORE_DATA_DIR')
$null=$start.Environment.Remove('GITHUBSTORE_WINGET_TRACE')
$app=[System.Diagnostics.Process]::Start($start)
if ($app.WaitForExit(3000)) { throw "Application exited early: $($app.ExitCode)" }
$app.Refresh()
Write-Output "Started process=$($app.Id) title=$($app.MainWindowTitle) responding=$($app.Responding)"
$app.WaitForExit()
Write-Output "Application exited at=$([DateTimeOffset]::Now.ToString('O')) exitCode=$($app.ExitCode)"
exit $app.ExitCode
