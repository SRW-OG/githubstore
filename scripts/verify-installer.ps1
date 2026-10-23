$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$build = Get-Content -LiteralPath (Join-Path $repository 'dist\installer-build.json') -Raw | ConvertFrom-Json
$identity = [guid]::NewGuid().ToString('N')
$testRoot = Join-Path $env:LOCALAPPDATA ('GitHubStore.PackageAcceptance-' + $identity)
$target = Join-Path $env:LOCALAPPDATA ('Programs\GitHubStore.PackageAcceptance-' + $identity)
$registryKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{739F23F9-0C57-4938-A740-54A455E166DA}_is1'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'GitHubStore.lnk'
if (Test-Path -LiteralPath $registryKey) { throw 'A production GitHubStore installation is already registered; refusing to replace it during testing.' }
if (Test-Path -LiteralPath $shortcut) { throw 'A GitHubStore Start Menu shortcut already exists; refusing to change it during testing.' }
if (Test-Path -LiteralPath $target) { throw 'Test installation target already exists.' }
$null = New-Item -ItemType Directory -Path $testRoot
# Create a fresh file at normal user integrity, rather than copying the workspace security label.
$setup = Join-Path $testRoot 'GitHubStore-Setup.exe'
[System.IO.File]::WriteAllBytes($setup, [System.IO.File]::ReadAllBytes($build.installer))
if ((Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant() -ne $build.sha256) { throw 'Installer checksum mismatch.' }
$data = Join-Path $testRoot 'userdata'
$null = New-Item -ItemType Directory -Path $data
$marker = Join-Path $data 'preserve-me.txt'
[System.IO.File]::WriteAllText($marker, 'Synthetic user data must survive client uninstallation.')
$app = $null
$installed = $false
$report = [ordered]@{ success = $false; targetDirectory = $target; syntheticUserData = $true }
try {
    $installLog = Join-Path $testRoot 'setup.log'
    $process = Start-Process -FilePath $setup -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-',
        ('/DIR="' + $target + '"'), ('/LOG="' + $installLog + '"'), '/TASKS=""') -PassThru -Wait
    if ($process.ExitCode -ne 0) { throw "Installation failed: $($process.ExitCode)" }
    $installed = $true
    $entry = Join-Path $target 'githubstore-ui.exe'
    foreach ($name in @('githubstore-ui.exe','coreclr.dll','Microsoft.UI.Xaml.dll','unins000.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $target $name))) { throw "Installed file missing: $name" }
    }
    $receipt = Get-ItemProperty -LiteralPath $registryKey
    if ([System.IO.Path]::TrimEndingDirectorySeparator($receipt.InstallLocation) -ne $target) { throw 'Windows uninstall registration has an incorrect installation directory.' }
    $report.installationSucceeded = $true
    $report.windowsUninstallEntry = $receipt.DisplayName
    $start = [System.Diagnostics.ProcessStartInfo]::new($entry)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $target
    $start.Environment['GITHUBSTORE_DATA_DIR'] = $data
    $app = [System.Diagnostics.Process]::Start($start)
    if ($app.WaitForExit(5000)) { throw "Installed application exited early: $($app.ExitCode)" }
    $app.Refresh()
    if (-not $app.Responding -or $app.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Installed application has no responding desktop window.' }
    $report.windowTitle = $app.MainWindowTitle
    $runtimeModule = @($app.Modules | Where-Object { $_.ModuleName -eq 'coreclr.dll' }) | Select-Object -First 1
    if (-not $runtimeModule -or $runtimeModule.FileName -ne (Join-Path $target 'coreclr.dll')) { throw 'Application did not load its bundled .NET runtime.' }
    $report.bundledRuntimeLoaded = $true
    $null = $app.CloseMainWindow()
    if (-not $app.WaitForExit(10000)) { throw 'Application did not exit after closing its window.' }
    $app = $null
    $uninstall = Start-Process -FilePath (Join-Path $target 'unins000.exe') -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -PassThru -Wait
    if ($uninstall.ExitCode -ne 0) { throw "Uninstallation failed: $($uninstall.ExitCode)" }
    for ($attempt = 0; $attempt -lt 40 -and (Test-Path -LiteralPath $entry); $attempt++) { Start-Sleep -Milliseconds 250 }
    $report.clientRemoved = -not (Test-Path -LiteralPath $entry)
    $report.uninstallEntryRemoved = -not (Test-Path -LiteralPath $registryKey)
    $report.shortcutRemoved = -not (Test-Path -LiteralPath $shortcut)
    $report.userDataPreserved = Test-Path -LiteralPath $marker
    $report.success = $report.clientRemoved -and $report.uninstallEntryRemoved -and $report.shortcutRemoved -and $report.userDataPreserved
    if (-not $report.success) { throw 'Uninstall or user-data preservation validation failed.' }
}
catch {
    $report.error = $_.Exception.Message
    throw
}
finally {
    if ($app -and -not $app.HasExited) { $null = $app.CloseMainWindow(); if (-not $app.WaitForExit(5000)) { $app.Kill(); $app.WaitForExit() } }
    $report.testLogs = $testRoot
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $repository 'artifacts\installer-acceptance.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 6
}
