[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [string]$EvidenceDir = (Join-Path $PSScriptRoot '..\artifacts\lw1-uninstaller')
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$evidence = [IO.Path]::GetFullPath($EvidenceDir)
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
$log = Join-Path $evidence 'matrix.log'
Start-Transcript -Path $log -Force | Out-Null

function Write-Step([string]$Message) {
    Write-Host "`n=== $Message ==="
}

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Assertion failed: $Message" }
    Write-Host "PASS: $Message"
}

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -ne $Expected) { throw "Assertion failed: $Message (expected '$Expected', got '$Actual')" }
    Write-Host "PASS: $Message"
}

function Invoke-Exe([string]$FilePath, [string[]]$Arguments, [string]$WorkingDirectory = $null) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $FilePath
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    foreach ($argument in $Arguments) { [void]$psi.ArgumentList.Add($argument) }
    if ($WorkingDirectory) { $psi.WorkingDirectory = $WorkingDirectory }
    $process = [Diagnostics.Process]::Start($psi)
    $process.WaitForExit()
    return $process
}

function Save-Listing([string]$Path, [string]$Name) {
    $out = Join-Path $evidence $Name
    if (-not (Test-Path -LiteralPath $Path)) {
        Set-Content -LiteralPath $out -Value "MISSING`t$Path" -Encoding utf8
        return
    }

    $lines = Get-ChildItem -LiteralPath $Path -Force -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Path.Length).TrimStart('\')
        if ($_.PSIsContainer) {
            "D`t$relative"
        } else {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            "F`t$($_.Length)`t$hash`t$relative"
        }
    }
    Set-Content -LiteralPath $out -Value $lines -Encoding utf8
}

function Wait-Until([scriptblock]$Condition, [int]$Seconds, [string]$Description) {
    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Milliseconds 500
    }
    throw "Timed out waiting for $Description"
}

$testRoot = Join-Path $env:RUNNER_TEMP ("lw1-" + [Guid]::NewGuid().ToString('N'))
$fresh = Join-Path $testRoot 'fresh'
$unowned = Join-Path $testRoot 'unowned'
$unmarkedUninstall = Join-Path $testRoot 'unmarked-uninstall'
$appData = Join-Path $env:LOCALAPPDATA 'LiveWall'
New-Item -ItemType Directory -Force -Path $testRoot | Out-Null
New-Item -ItemType Directory -Force -Path $appData | Out-Null

try {
    Write-Step 'Environment'
    Write-Host "Runner: $env:RUNNER_OS $env:RUNNER_IMAGE_OS $env:RUNNER_IMAGE_VERSION"
    Write-Host ([Environment]::OSVersion.VersionString)
    & dotnet --info
    if ($LASTEXITCODE) { throw 'dotnet --info failed' }

    if (-not $SkipBuild) {
        Write-Step 'Unit tests'
        & dotnet test (Join-Path $root 'LiveWall.sln') -c Release --logger 'console;verbosity=normal' 2>&1 |
            Tee-Object -FilePath (Join-Path $evidence 'unit-tests.log')
        if ($LASTEXITCODE) { throw 'dotnet test failed' }

        Write-Step 'Build installer'
        & (Join-Path $root 'build-installer.ps1')
        if ($LASTEXITCODE) { throw 'build-installer.ps1 failed' }
    }

    $setup = Join-Path $root 'dist\LiveWallSetup.exe'
    Assert-True (Test-Path -LiteralPath $setup) "setup exe exists: $setup"

    Write-Step 'Fresh install'
    Save-Listing $fresh 'fresh-before.txt'
    $p = Invoke-Exe $setup @('--silent', $fresh, '--desktop')
    Assert-Equal $p.ExitCode 0 'fresh install exits 0'
    Assert-True (Test-Path -LiteralPath (Join-Path $fresh 'LiveWall.exe')) 'LiveWall.exe installed'
    Assert-True (Test-Path -LiteralPath (Join-Path $fresh 'Uninstall.exe')) 'Uninstall.exe installed'
    Assert-True (Test-Path -LiteralPath (Join-Path $fresh '.livewall-install.json')) 'ownership marker installed'
    $marker = Get-Content -LiteralPath (Join-Path $fresh '.livewall-install.json') -Raw | ConvertFrom-Json
    Assert-Equal $marker.productId 'livewall-desktop' 'marker product id'
    Assert-Equal $marker.installPath ((Resolve-Path -LiteralPath $fresh).Path.TrimEnd('\')) 'marker install path'
    Assert-Equal $marker.version '1.0.0' 'marker version'
    Assert-True ($marker.files -contains 'LiveWall.exe') 'marker lists LiveWall.exe'
    Assert-True ($marker.files -contains 'Uninstall.exe') 'marker lists Uninstall.exe'
    Assert-True (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) 'LiveWall.lnk')) 'Start menu shortcut exists'
    Assert-True (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'LiveWall.lnk')) 'Desktop shortcut exists'
    Copy-Item -LiteralPath (Join-Path $fresh '.livewall-install.json') -Destination (Join-Path $evidence 'marker-fresh.json')
    Save-Listing $fresh 'fresh-after.txt'

    Write-Step 'Upgrade over an existing LiveWall install'
    $legacyFile = Join-Path $fresh 'legacy-owned.bin'
    Set-Content -LiteralPath $legacyFile -Value 'old version file' -Encoding utf8
    $marker.version = '0.9.0'
    $marker.files = @($marker.files + 'legacy-owned.bin') | Select-Object -Unique
    $marker | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fresh '.livewall-install.json') -Encoding utf8
    Set-Content -LiteralPath (Join-Path $fresh 'keep-me.txt') -Value 'must survive uninstall' -Encoding utf8
    New-Item -ItemType Directory -Force -Path (Join-Path $fresh 'personal') | Out-Null
    Set-Content -LiteralPath (Join-Path $fresh 'personal\notes.txt') -Value 'personal nested file' -Encoding utf8
    $appDataSentinel = Join-Path $appData 'lw1-user-data-sentinel.json'
    Set-Content -LiteralPath $appDataSentinel -Value 'settings and library stay on uninstall' -Encoding utf8
    Save-Listing $fresh 'upgrade-before.txt'

    $p = Invoke-Exe $setup @('--silent', $fresh)
    Assert-Equal $p.ExitCode 0 'upgrade exits 0'
    $upgraded = Get-Content -LiteralPath (Join-Path $fresh '.livewall-install.json') -Raw | ConvertFrom-Json
    Assert-Equal $upgraded.version '1.0.0' 'upgrade rewrites marker version'
    Assert-True ($upgraded.files -contains 'legacy-owned.bin') 'upgrade preserves prior owned-file manifest'
    Assert-Equal (Get-Content -LiteralPath (Join-Path $fresh 'keep-me.txt') -Raw).Trim() 'must survive uninstall' 'upgrade leaves unowned sentinel intact'
    Copy-Item -LiteralPath (Join-Path $fresh '.livewall-install.json') -Destination (Join-Path $evidence 'marker-upgraded.json')
    Save-Listing $fresh 'upgrade-after.txt'

    Write-Step 'Refuse --silent install into a non-empty non-LiveWall folder'
    New-Item -ItemType Directory -Force -Path $unowned | Out-Null
    Set-Content -LiteralPath (Join-Path $unowned 'unrelated-data.txt') -Value 'do not touch' -Encoding utf8
    $unownedHash = (Get-FileHash -LiteralPath (Join-Path $unowned 'unrelated-data.txt') -Algorithm SHA256).Hash
    Save-Listing $unowned 'refused-before.txt'
    $p = Invoke-Exe $setup @('--silent', $unowned)
    Assert-True ($p.ExitCode -ne 0) 'non-empty unowned --silent install fails'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $unowned '.livewall-install.json'))) 'refused folder got no marker'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $unowned 'LiveWall.exe'))) 'refused folder got no app files'
    Assert-Equal (Get-FileHash -LiteralPath (Join-Path $unowned 'unrelated-data.txt') -Algorithm SHA256).Hash $unownedHash 'refused folder sentinel unchanged'
    Copy-Item -LiteralPath (Join-Path $env:TEMP 'livewall-setup.log') -Destination (Join-Path $evidence 'setup-refused.log') -Force
    Save-Listing $unowned 'refused-after.txt'

    Write-Step 'Uninstaller refuses an unmarked folder'
    New-Item -ItemType Directory -Force -Path $unmarkedUninstall | Out-Null
    Set-Content -LiteralPath (Join-Path $unmarkedUninstall 'personal.txt') -Value 'unowned sentinel' -Encoding utf8
    Copy-Item -LiteralPath (Join-Path $fresh 'Uninstall.exe') -Destination (Join-Path $unmarkedUninstall 'Uninstall.exe')
    Save-Listing $unmarkedUninstall 'unmarked-uninstall-before.txt'
    $p = Invoke-Exe (Join-Path $unmarkedUninstall 'Uninstall.exe') @('--uninstall', '--silent')
    Assert-Equal $p.ExitCode 2 'unmarked uninstall refuses with exit 2'
    Assert-True (Test-Path -LiteralPath (Join-Path $unmarkedUninstall 'personal.txt')) 'unmarked uninstall leaves sentinel intact'
    Assert-True (Test-Path -LiteralPath (Join-Path $unmarkedUninstall 'Uninstall.exe')) 'unmarked uninstall leaves uninstaller intact'
    Save-Listing $unmarkedUninstall 'unmarked-uninstall-after.txt'

    Write-Step 'Uninstall with sentinel files and user data'
    Save-Listing $fresh 'uninstall-before.txt'
    Save-Listing $appData 'appdata-before.txt'
    $keepHash = (Get-FileHash -LiteralPath (Join-Path $fresh 'keep-me.txt') -Algorithm SHA256).Hash
    $personalHash = (Get-FileHash -LiteralPath (Join-Path $fresh 'personal\notes.txt') -Algorithm SHA256).Hash
    $appDataHash = (Get-FileHash -LiteralPath $appDataSentinel -Algorithm SHA256).Hash
    $p = Invoke-Exe (Join-Path $fresh 'Uninstall.exe') @('--silent')
    Assert-Equal $p.ExitCode 0 'uninstall exits 0'
    Wait-Until { -not (Test-Path -LiteralPath (Join-Path $fresh '.livewall-install.json')) } 60 'ownership marker removal'
    Wait-Until {
        $uninstallLog = Join-Path $env:TEMP 'livewall-uninstall.log'
        (Test-Path -LiteralPath $uninstallLog) -and (Select-String -LiteralPath $uninstallLog -SimpleMatch 'cleanup complete' -Quiet)
    } 10 'cleanup completion log'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $fresh 'LiveWall.exe'))) 'known app exe removed'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $fresh 'Uninstall.exe'))) 'known uninstaller removed'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path $fresh 'legacy-owned.bin'))) 'previous version file removed from manifest'
    Assert-True (Test-Path -LiteralPath (Join-Path $fresh 'keep-me.txt')) 'root sentinel remains'
    Assert-True (Test-Path -LiteralPath (Join-Path $fresh 'personal\notes.txt')) 'nested sentinel remains'
    Assert-Equal (Get-FileHash -LiteralPath (Join-Path $fresh 'keep-me.txt') -Algorithm SHA256).Hash $keepHash 'root sentinel hash unchanged'
    Assert-Equal (Get-FileHash -LiteralPath (Join-Path $fresh 'personal\notes.txt') -Algorithm SHA256).Hash $personalHash 'nested sentinel hash unchanged'
    Assert-True (Test-Path -LiteralPath $appDataSentinel) '%LOCALAPPDATA%\\LiveWall data remains'
    Assert-Equal (Get-FileHash -LiteralPath $appDataSentinel -Algorithm SHA256).Hash $appDataHash 'user data hash unchanged'
    Assert-True (-not (Test-Path -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\LiveWall')) 'uninstall registry entry removed'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('Programs')) 'LiveWall.lnk'))) 'Start menu shortcut removed'
    Assert-True (-not (Test-Path -LiteralPath (Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'LiveWall.lnk'))) 'Desktop shortcut removed'
    Copy-Item -LiteralPath (Join-Path $env:TEMP 'livewall-uninstall.log') -Destination (Join-Path $evidence 'uninstall.log') -Force
    Save-Listing $fresh 'uninstall-after.txt'
    Save-Listing $appData 'appdata-after.txt'

    Write-Host "`nAll LW-1 Windows matrix checks passed."
}
catch {
    Write-Host "`nLW-1 matrix failed: $($_.Exception.Message)"
    if (Test-Path -LiteralPath (Join-Path $env:TEMP 'livewall-setup.log')) {
        Copy-Item -LiteralPath (Join-Path $env:TEMP 'livewall-setup.log') -Destination (Join-Path $evidence 'setup-failure.log') -Force
    }
    throw
}
finally {
    Stop-Transcript | Out-Null
}
