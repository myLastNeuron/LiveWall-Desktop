[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('dotnet8-only', 'dotnet9-only', 'dotnet10-only', 'none', 'dotnet8-and-9')]
    [string]$Case,

    [Parameter(Mandatory)]
    [string]$SetupExe,

    [Parameter(Mandatory)]
    [string]$BaselineRuntimeConfig,

    [string]$BuiltExe,

    [string]$EvidenceDir
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $EvidenceDir) { $EvidenceDir = Join-Path $root "artifacts\lw2-runtime\$Case" }
$evidence = [IO.Path]::GetFullPath($EvidenceDir)
New-Item -ItemType Directory -Force -Path $evidence | Out-Null
Start-Transcript -Path (Join-Path $evidence 'matrix.log') -Force | Out-Null

$SetupExe = (Resolve-Path $SetupExe).Path
$BaselineRuntimeConfig = (Resolve-Path $BaselineRuntimeConfig).Path
if ($BuiltExe) { $BuiltExe = (Resolve-Path $BuiltExe).Path }
$dotnetRoot = Join-Path $env:ProgramFiles 'dotnet'
$env:DOTNET_ROOT = $dotnetRoot
$frameworkDir = Join-Path $dotnetRoot 'shared\Microsoft.WindowsDesktop.App'
$coreFrameworkDir = Join-Path $dotnetRoot 'shared\Microsoft.NETCore.App'
$appData = Join-Path $env:LOCALAPPDATA 'LiveWall'

$checks = [Collections.Generic.List[object]]::new()
$failures = [Collections.Generic.List[string]]::new()
$fatal = $null
$appProcess = $null
$setupExit = $null
$downloadedDotnet = $false
$frameworkVersion = ''
$hasRollForward = $false

function Write-Step([string]$Message) {
    Write-Host "`n=== $Message ==="
}

function Assert-True([bool]$Condition, [string]$Message, [string]$Detail = '') {
    $checks.Add([pscustomobject]@{
        Check = $Message
        Result = if ($Condition) { 'PASS' } else { 'FAIL' }
        Detail = $Detail
    })
    if ($Condition) {
        Write-Host "PASS: $Message"
    } else {
        Write-Host "FAIL: $Message $Detail"
        $failures.Add($Message)
    }
}

function Get-RawRuntimeDirs {
    if (-not (Test-Path -LiteralPath $frameworkDir)) { return @() }
    Get-ChildItem -LiteralPath $frameworkDir -Directory | Sort-Object Name | ForEach-Object { $_.Name }
}

function Get-Versions([string]$FrameworkDir) {
    if (-not (Test-Path -LiteralPath $FrameworkDir)) { return @() }
    Get-ChildItem -LiteralPath $FrameworkDir -Directory | ForEach-Object {
        $parsed = $null
        if ([version]::TryParse($_.Name, [ref]$parsed)) {
            [pscustomobject]@{
                Name = $_.Name
                Version = $parsed
                Major = $parsed.Major
                Path = $_.FullName
            }
        }
    }
}

function Get-DesktopVersions { Get-Versions $frameworkDir }
function Get-CoreVersions { Get-Versions $coreFrameworkDir }

function Get-Majors {
    @((Get-DesktopVersions) | ForEach-Object { $_.Major } | Sort-Object -Unique)
}

function Get-CoreMajors {
    @((Get-CoreVersions) | ForEach-Object { $_.Major } | Sort-Object -Unique)
}

function Get-DesiredMajors {
    switch ($Case) {
        'dotnet8-only' { return @(8) }
        'dotnet9-only' { return @(9) }
        'dotnet10-only' { return @(10) }
        'dotnet8-and-9' { return @(8, 9) }
        'none' { return @() }
    }
}

function Stop-DotnetHosts {
    Get-Process dotnet, VBCSCompiler, MSBuild -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

function Ensure-DesktopRuntime([int]$Major) {
    $hasDesktop = Get-DesktopVersions | Where-Object { $_.Major -eq $Major }
    $hasCore = Get-CoreVersions | Where-Object { $_.Major -eq $Major }
    if ($hasDesktop -and $hasCore) { return }

    $installer = Join-Path $env:RUNNER_TEMP 'dotnet-install.ps1'
    if (-not (Test-Path -LiteralPath $installer)) {
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installer -UseBasicParsing
    }
    Unblock-File -LiteralPath $installer -ErrorAction SilentlyContinue

    Write-Host "Installing .NET $Major.0 Desktop Runtime into $dotnetRoot for the test precondition"
    # Run in a child pwsh: dotnet-install.ps1 calls exit, which would end this script.
    $pwsh = (Get-Process -Id $PID).Path
    & $pwsh -NoProfile -ExecutionPolicy Bypass -File $installer -Channel "$Major.0" -Runtime windowsdesktop -InstallDir $dotnetRoot -NoPath
    if ($LASTEXITCODE -ne 0) { throw "dotnet-install.ps1 failed for major $Major (exit $LASTEXITCODE)" }
    if (-not (Get-DesktopVersions | Where-Object { $_.Major -eq $Major }) -or
        -not (Get-CoreVersions | Where-Object { $_.Major -eq $Major })) {
        throw "dotnet-install.ps1 reported success but no $Major.x Desktop/Core Runtime is present"
    }
}

# The hosted image installs .NET 8 through MSI bundles. Deleting files is not enough:
# Burn still detects the 8.x packages as Present and the official installer no-ops.
# On this disposable runner, remove the 8.x product registrations so the machine
# genuinely looks like a clean 9/10-only (or runtime-free) PC.
function Remove-RuntimeRegistrations([int]$Major) {
    $pattern = "^Microsoft (Windows Desktop Runtime|\.NET Runtime) - $Major\."
    $removed = [Collections.Generic.List[string]]::new()

    foreach ($arpRoot in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
    )) {
        Get-ChildItem $arpRoot -ErrorAction SilentlyContinue | ForEach-Object {
            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
            if ($props.DisplayName -match $pattern) {
                Remove-Item $_.PSPath -Recurse -Force -ErrorAction Stop
                $removed.Add("ARP: $($props.DisplayName)")
            }
        }
    }

    $userData = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Installer\UserData'
    Get-ChildItem $userData -ErrorAction SilentlyContinue | ForEach-Object {
        $productsRoot = Join-Path $_.PSPath 'Products'
        Get-ChildItem $productsRoot -ErrorAction SilentlyContinue | ForEach-Object {
            $props = Get-ItemProperty (Join-Path $_.PSPath 'InstallProperties') -ErrorAction SilentlyContinue
            if ($props.DisplayName -match $pattern) {
                Remove-Item $_.PSPath -Recurse -Force -ErrorAction Stop
                $removed.Add("UserData: $($props.DisplayName)")
            }
        }
    }

    $classes = 'HKLM:\SOFTWARE\Classes\Installer\Products'
    Get-ChildItem $classes -ErrorAction SilentlyContinue | ForEach-Object {
        $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
        if ($props.ProductName -match $pattern) {
            Remove-Item $_.PSPath -Recurse -Force -ErrorAction Stop
            $removed.Add("Classes: $($props.ProductName)")
        }
    }

    foreach ($entry in $removed) { Write-Host "Removed registration $entry" }
    if ($removed.Count -gt 0) {
        Add-Content -LiteralPath (Join-Path $evidence 'removed-registrations.txt') -Value $removed -Encoding utf8
    }
}

function Remove-RuntimeVersion($Version) {
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item -LiteralPath $Version.Path -Recurse -Force -ErrorAction Stop
            Write-Host "Removed runtime $($Version.Name)"
            return
        } catch {
            if ($attempt -eq 3) { throw "Could not remove $($Version.Path): $($_.Exception.Message)" }
            Start-Sleep -Seconds 1
        }
    }
}

function Set-RuntimeState {
    $desired = Get-DesiredMajors
    foreach ($major in $desired) { Ensure-DesktopRuntime $major }
    foreach ($version in Get-DesktopVersions) {
        if ($version.Major -notin $desired) { Remove-RuntimeVersion $version }
    }
    foreach ($version in Get-CoreVersions) {
        if ($version.Major -notin $desired) { Remove-RuntimeVersion $version }
    }
    foreach ($major in 8, 9, 10, 11) {
        if ($major -notin $desired) { Remove-RuntimeRegistrations $major }
    }
}

function Save-RuntimeState([string]$Name) {
    $lines = @(
        "Raw Microsoft.WindowsDesktop.App directories:"
        (Get-RawRuntimeDirs | ForEach-Object { "  $_" })
        ''
        'dotnet --list-runtimes:'
        (& dotnet --list-runtimes 2>&1 | ForEach-Object { "  $_" })
        ''
        "Parsed Desktop Runtime majors: $((Get-Majors) -join ', ')"
        "Parsed Core Runtime majors: $((Get-CoreMajors) -join ', ')"
    )
    Set-Content -LiteralPath (Join-Path $evidence $Name) -Value $lines -Encoding utf8
}

function Save-RuntimeSnapshot([string]$Name) {
    $lines = @('Microsoft.WindowsDesktop.App:')
    $desktop = Get-DesktopVersions
    $lines += if ($desktop) { $desktop | ForEach-Object { "  $($_.Name)`t$($_.Path)" } } else { @('  MISSING') }
    $lines += ''
    $lines += 'Microsoft.NETCore.App:'
    $core = Get-CoreVersions
    $lines += if ($core) { $core | ForEach-Object { "  $($_.Name)`t$($_.Path)" } } else { @('  MISSING') }
    Set-Content -LiteralPath (Join-Path $evidence $Name) -Value $lines -Encoding utf8
}

try {
    Write-Step 'Environment'
    $os = [Environment]::OSVersion.VersionString
    $image = if ($env:ImageOS) { "$env:ImageOS $env:ImageVersion" } else { "$env:RUNNER_OS $env:RUNNER_IMAGE_VERSION" }
    Write-Host "Runner: $env:RUNNER_OS / $image"
    Write-Host $os
    @(
        "Runner OS: $env:RUNNER_OS"
        "Runner image: $image"
        "Windows: $os"
        "Case: $Case"
        "Setup: $SetupExe"
        "dotnet root: $dotnetRoot"
        ''
        'dotnet --info:'
        (& dotnet --info 2>&1 | ForEach-Object { "  $_" })
    ) | Set-Content -LiteralPath (Join-Path $evidence 'environment.txt') -Encoding utf8

    Write-Step "Establish $Case runtime state"
    Stop-DotnetHosts
    Set-RuntimeState
    Save-RuntimeSnapshot 'runtime-state-before.txt'
    Save-RuntimeState 'runtime-state-before.txt.details'
    & reg query 'HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx' /s 2>&1 |
        Set-Content -LiteralPath (Join-Path $evidence 'registry-before.txt') -Encoding utf8

    $desired = Get-DesiredMajors
    $beforeMajors = Get-Majors
    $beforeCoreMajors = Get-CoreMajors
    Assert-True (($beforeMajors -join ',') -eq (($desired | Sort-Object) -join ',')) `
        'Test precondition has the requested Desktop Runtime majors' `
        "expected [$($desired -join ',')], got [$($beforeMajors -join ',')]"
    Assert-True (($beforeCoreMajors -join ',') -eq (($desired | Sort-Object) -join ',')) `
        'Test precondition has the requested Core Runtime majors' `
        "expected [$($desired -join ',')], got [$($beforeCoreMajors -join ',')]"
    Assert-True (($Case -ne 'dotnet9-only' -and $Case -ne 'dotnet10-only') -or ($beforeMajors -notcontains 8)) `
        'No 8.x Desktop Runtime is present before setup' `
        "majors: [$($beforeMajors -join ',')]"
    if ($Case -eq 'none') {
        Assert-True ($beforeMajors.Count -eq 0) 'No Desktop Runtime is present before setup'
    }

    Write-Step 'Run LiveWallSetup.exe --silent'
    $installDir = Join-Path $env:RUNNER_TEMP "lw2-$Case\app"
    if (Test-Path -LiteralPath $installDir) { Remove-Item -LiteralPath $installDir -Recurse -Force }
    $setupLog = Join-Path $env:TEMP 'livewall-setup.log'
    Remove-Item -LiteralPath $setupLog -Force -ErrorAction SilentlyContinue

    $psi = [Diagnostics.ProcessStartInfo]::new($SetupExe)
    $psi.UseShellExecute = $false
    $psi.WorkingDirectory = Split-Path $SetupExe
    [void]$psi.ArgumentList.Add('--silent')
    [void]$psi.ArgumentList.Add($installDir)
    $setup = [Diagnostics.Process]::Start($psi)
    if (-not $setup.WaitForExit(600000)) {
        $setup.Kill()
        $setupExit = -1
        Write-Host 'Setup timed out after 10 minutes'
    } else {
        $setupExit = $setup.ExitCode
    }

    $logText = ''
    if (Test-Path -LiteralPath $setupLog) {
        Copy-Item -LiteralPath $setupLog -Destination (Join-Path $evidence 'setup.log') -Force
        $logText = Get-Content -LiteralPath $setupLog -Raw
    }
    Set-Content -LiteralPath (Join-Path $evidence 'setup-exit-code.txt') -Value $setupExit -Encoding utf8

    Assert-True ($setupExit -eq 0) 'Setup exited successfully' "exit code: $setupExit"
    Assert-True ($logText -match '(?m)^done') 'Setup log reports done'
    Assert-True ($logText -notmatch '(?m)^failed:') 'Setup log has no failure line'

    $downloadedDotnet = $logText -match 'Downloading \.NET 8 Desktop Runtime'
    $expectedDownload = $Case -in @('dotnet9-only', 'dotnet10-only', 'none')
    Assert-True ($downloadedDotnet -eq $expectedDownload) `
        'Installer .NET action matches the policy' `
        "expected download=$expectedDownload, actual download=$downloadedDotnet"

    Save-RuntimeSnapshot 'runtime-state-after.txt'
    Save-RuntimeState 'runtime-state-after.txt.details'
    & reg query 'HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx' /s 2>&1 |
        Set-Content -LiteralPath (Join-Path $evidence 'registry-after.txt') -Encoding utf8
    $afterMajors = Get-Majors
    $afterCoreMajors = Get-CoreMajors
    Assert-True ($afterMajors -contains 8) 'An 8.x Desktop Runtime is present after setup' "majors: [$($afterMajors -join ',')]"
    Assert-True ($afterCoreMajors -contains 8) 'An 8.x Core Runtime is present after setup' "majors: [$($afterCoreMajors -join ',')]"
    if ($Case -eq 'dotnet8-and-9') {
        Assert-True ($afterMajors -contains 9) 'The pre-existing 9.x runtime is left in place'
        Assert-True ($afterCoreMajors -contains 9) 'The pre-existing 9.x Core Runtime is left in place'
    }

    Write-Step 'Inspect runtimeconfig and the installed binary'
    Copy-Item -LiteralPath $BaselineRuntimeConfig -Destination (Join-Path $evidence 'runtimeconfig.baseline.json') -Force
    $config = Get-Content -LiteralPath $BaselineRuntimeConfig -Raw | ConvertFrom-Json
    $frameworks = @($config.runtimeOptions.frameworks)
    if ($frameworks.Count -eq 0) { $frameworks = @($config.runtimeOptions.framework) }
    $desktopFramework = $frameworks | Where-Object { $_.name -eq 'Microsoft.WindowsDesktop.App' } | Select-Object -First 1
    if ($desktopFramework) { $frameworkVersion = $desktopFramework.version }
    $hasRollForward = $null -ne $config.runtimeOptions.PSObject.Properties['rollForward']
    Assert-True ($frameworkVersion -eq '8.0.0') 'runtimeconfig requests WindowsDesktop 8.0.0' "version: $frameworkVersion"
    Assert-True (-not $hasRollForward) 'runtimeconfig has no cross-major rollForward policy'

    $installedRuntimeConfig = Join-Path $installDir 'LiveWall.runtimeconfig.json'
    $installedExe = Join-Path $installDir 'LiveWall.exe'
    $baselineHash = (Get-FileHash -LiteralPath $BaselineRuntimeConfig -Algorithm SHA256).Hash
    $installedExeHash = if (Test-Path -LiteralPath $installedExe) { (Get-FileHash -LiteralPath $installedExe -Algorithm SHA256).Hash } else { '' }
    $builtExeHash = if ($BuiltExe) { (Get-FileHash -LiteralPath $BuiltExe -Algorithm SHA256).Hash } else { '' }

    if (Test-Path -LiteralPath $installedRuntimeConfig) {
        Copy-Item -LiteralPath $installedRuntimeConfig -Destination (Join-Path $evidence 'runtimeconfig.installed.json') -Force
        $installedHash = (Get-FileHash -LiteralPath $installedRuntimeConfig -Algorithm SHA256).Hash
        $diff = Compare-Object (Get-Content -LiteralPath $BaselineRuntimeConfig) (Get-Content -LiteralPath $installedRuntimeConfig)
        $diffText = if (-not $diff) {
            "IDENTICAL`nBaseline SHA256:  $baselineHash`nInstalled SHA256: $installedHash`n`nSetup does not edit runtimeconfig; both request Microsoft.WindowsDesktop.App 8.0.0 with no rollForward."
        } else {
            ($diff | Format-Table -AutoSize | Out-String)
        }
        Set-Content -LiteralPath (Join-Path $evidence 'runtimeconfig.diff') -Value $diffText -Encoding utf8
        Assert-True ($installedHash -eq $baselineHash) 'Installed runtimeconfig is identical to the built runtimeconfig'
    } else {
        # PublishSingleFile embeds runtimeconfig into the apphost, so there is no sidecar file.
        # Prove setup installed the exact tested binary instead; the loaded-module checks below
        # prove which runtime that binary resolved.
        $diffText = @(
            'Single-file publish: LiveWall.runtimeconfig.json is embedded in LiveWall.exe.'
            "Baseline runtimeconfig SHA256: $baselineHash"
            "Installed LiveWall.exe SHA256: $installedExeHash"
            "Built payload LiveWall.exe SHA256: $builtExeHash"
            ''
            'No runtimeconfig sidecar to diff. Setup copies the tested payload byte-for-byte, so the'
            'embedded contract remains Microsoft.WindowsDesktop.App 8.0.0 with no rollForward.'
        )
        Set-Content -LiteralPath (Join-Path $evidence 'runtimeconfig.diff') -Value $diffText -Encoding utf8
        Assert-True ($builtExeHash -ne '' -and $installedExeHash -eq $builtExeHash) `
            'Installed LiveWall.exe is byte-identical to the tested payload' `
            "built $builtExeHash, installed $installedExeHash"
    }

    Write-Step 'Launch the installed app'
    Remove-Item -LiteralPath $appData -Recurse -Force -ErrorAction SilentlyContinue
    $appExe = Join-Path $installDir 'LiveWall.exe'
    Assert-True (Test-Path -LiteralPath $appExe) 'Installed app has LiveWall.exe'

    $appDirCreated = $false
    $alive = $false
    $appExitCode = $null
    $coreclrPath = ''
    $winFormsPath = ''

    if (Test-Path -LiteralPath $appExe) {
        $appProcess = Start-Process -FilePath $appExe -ArgumentList '--background' -PassThru
        $deadline = (Get-Date).AddSeconds(30)
        while ((Get-Date) -lt $deadline -and -not (Test-Path -LiteralPath $appData) -and -not $appProcess.HasExited) {
            Start-Sleep -Milliseconds 250
        }
        $appDirCreated = Test-Path -LiteralPath $appData

        # Give the tray app time to finish startup, then inspect what it loaded.
        Start-Sleep -Seconds 5
        $appProcess.Refresh()
        $alive = -not $appProcess.HasExited
        if ($appProcess.HasExited) { $appExitCode = $appProcess.ExitCode }

        if ($alive) {
            try {
                $modulePaths = $appProcess.Modules | ForEach-Object { $_.FileName }
                $coreclrPath = $modulePaths | Where-Object {
                    (Split-Path $_ -Leaf) -eq 'coreclr.dll' -and $_ -match 'Microsoft\.NETCore\.App\\8\.'
                } | Select-Object -First 1
                $winFormsPath = $modulePaths | Where-Object {
                    (Split-Path $_ -Leaf) -eq 'System.Windows.Forms.dll' -and $_ -match 'Microsoft\.WindowsDesktop\.App\\8\.'
                } | Select-Object -First 1
            } catch {
                Write-Host "Could not read loaded modules: $($_.Exception.Message)"
            }
        }

        $launchReport = @(
            "Case: $Case"
            "Executable: $appExe"
            "AppData created (%LOCALAPPDATA%\LiveWall): $appDirCreated"
            "Process alive after startup wait: $alive"
            "Process exit code: $appExitCode"
            "Loaded coreclr.dll (8.x): $coreclrPath"
            "Loaded System.Windows.Forms.dll (8.x): $winFormsPath"
            ''
            '8.x framework modules loaded by the app:'
            ($modulePaths | Where-Object { $_ -match 'Microsoft\.(NETCore|WindowsDesktop)\.App\\8\.' } | Sort-Object | ForEach-Object { "  $_" })
        )
        Set-Content -LiteralPath (Join-Path $evidence 'app-launch.txt') -Value $launchReport -Encoding utf8

        Assert-True $appDirCreated 'App reached managed Main (it created %LOCALAPPDATA%\LiveWall)'
        Assert-True $alive 'App process is still running after startup' "exit code: $appExitCode"
        Assert-True ($coreclrPath -ne '') 'App loaded coreclr.dll from the 8.x Microsoft.NETCore.App runtime' "loaded: $coreclrPath"
        Assert-True ($winFormsPath -ne '') 'App loaded the 8.x Microsoft.WindowsDesktop.App runtime' "loaded: $winFormsPath"
    }

    $action = [ordered]@{
        case = $Case
        runnerOs = $env:RUNNER_OS
        runnerImage = $image
        windows = $os
        setupExe = $SetupExe
        setupExitCode = $setupExit
        runtimeMajorsBefore = @($beforeMajors)
        coreMajorsBefore = @($beforeCoreMajors)
        runtimeMajorsAfter = @(Get-Majors)
        coreMajorsAfter = @(Get-CoreMajors)
        expectedDotnetDownload = $expectedDownload
        actualDotnetDownload = $downloadedDotnet
        runtimeConfigFrameworkVersion = $frameworkVersion
        runtimeConfigHasRollForward = [bool]$hasRollForward
        appDataCreated = $appDirCreated
        appProcessAlive = $alive
        appExitCode = $appExitCode
        loadedCoreClr = $coreclrPath
        loadedWindowsForms = $winFormsPath
        checks = @($checks)
    }
    $action | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'installer-action.json') -Encoding utf8
}
catch {
    $fatal = $_
    Write-Host "FATAL: $($_.Exception.Message)"
    Write-Host $_.ScriptStackTrace
    Set-Content -LiteralPath (Join-Path $evidence 'fatal.txt') -Value ($_ | Out-String) -Encoding utf8
}
finally {
    if ($appProcess -and -not $appProcess.HasExited) {
        Stop-Process -Id $appProcess.Id -Force -ErrorAction SilentlyContinue
    }
    Get-Process LiveWall -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

    $result = if ($failures.Count -eq 0 -and -not $fatal) { 'PASS' } else { 'FAIL' }
    $summary = @(
        "# LW-2 runtime matrix: $Case"
        ''
        "- Result: **$result**"
        "- Runner: $env:RUNNER_OS / $image"
        "- Windows: $os"
        "- .NET download expected: $($Case -in @('dotnet9-only', 'dotnet10-only', 'none'))"
        "- .NET download observed: $downloadedDotnet"
        "- Setup exit code: $setupExit"
        ''
        '| Check | Result | Detail |'
        '| --- | --- | --- |'
        $checks | ForEach-Object { "| $($_.Check) | $($_.Result) | $($_.Detail) |" }
    )
    if ($fatal) { $summary += @('', "Fatal: $($fatal.Exception.Message)") }
    Set-Content -LiteralPath (Join-Path $evidence 'summary.md') -Value $summary -Encoding utf8

    Stop-Transcript | Out-Null
}

if ($failures.Count -gt 0 -or $fatal) {
    Write-Error "LW-2 runtime case '$Case' failed: $($failures.Count) assertion(s)$(if ($fatal) { ' plus a fatal error' })"
    exit 1
}
exit 0
