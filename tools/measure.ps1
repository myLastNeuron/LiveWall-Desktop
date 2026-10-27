param(
    [string]$Label,
    [int]$RootPid = 0,          # 0 = measure whole system GPU only (baseline)
    [int]$Seconds = 30,
    [string]$Out = ""            # optional: append the per-second rows to this CSV
)
# One combined sample loop: RAM, CPU, GPU % total and per engine type, GPU memory.
function Get-Descendants { param([int]$Root)
    # Stale/recycled ParentProcessId values would pull in unrelated trees (e.g. SearchHost's
    # WebView2), so only accept children created after the root and with a known name.
    $rootProc = Get-CimInstance Win32_Process -Filter "ProcessId=$Root"
    $since = $rootProc.CreationDate
    $names = 'LiveWall|msedgewebview2|Lively|mpv|Lively.Watchdog'
    $all = Get-CimInstance Win32_Process | Where-Object { $_.Name -match $names -and $_.CreationDate -ge $since }
    $set = [System.Collections.Generic.HashSet[int]]::new(); [void]$set.Add($Root)
    $changed = $true
    while ($changed) { $changed = $false; foreach ($p in $all) { if ($set.Contains([int]$p.ParentProcessId) -and -not $set.Contains([int]$p.ProcessId)) { [void]$set.Add([int]$p.ProcessId); $changed = $true } } }
    $set
}

$eng = @{}; $prevCpu = @{}; $rows = @()
$wsSum = 0.0; $privSum = 0.0; $cpuSum = 0.0; $utilSum = 0.0; $utilMax = 0.0; $gmSum = 0.0; $gdSum = 0.0; $pSum = 0

for ($i = 0; $i -lt $Seconds; $i++) {
    Start-Sleep -Seconds 1
    $stamp = Get-Date
    $pids = if ($RootPid -gt 0) { Get-Descendants -Root $RootPid } else { [System.Collections.Generic.HashSet[int]]::new() }
    $procs = if ($RootPid -gt 0) { Get-Process -Id @($pids) -ErrorAction SilentlyContinue } else { @() }

    $ws = 0.0; $priv = 0.0; $cpu = 0.0
    foreach ($p in $procs) {
        $ws += $p.WorkingSet64; $priv += $p.PrivateMemorySize64
        $t = $p.TotalProcessorTime.TotalSeconds
        $cpu += if ($prevCpu.ContainsKey($p.Id)) { $t - $prevCpu[$p.Id] } else { 0 }
        $prevCpu[$p.Id] = $t
    }

    $u = 0.0
    foreach ($s in (Get-Counter '\GPU Engine(*)\Utilization Percentage' -ErrorAction SilentlyContinue).CounterSamples) {
        if ($s.InstanceName -notmatch 'pid_(\d+)_') { continue }
        $ok = if ($RootPid -gt 0) { $pids.Contains([int]$Matches[1]) } else { $true }
        if (-not $ok) { continue }
        $u += $s.CookedValue
        $t = if ($s.InstanceName -match 'engtype_(.+)$') { $Matches[1] } else { '?' }
        if (-not $eng.ContainsKey($t)) { $eng[$t] = 0.0 }
        $eng[$t] += $s.CookedValue
    }

    $gl = 0.0; $gd = 0.0
    foreach ($s in (Get-Counter '\GPU Process Memory(*)\Local Usage', '\GPU Process Memory(*)\Dedicated Usage' -ErrorAction SilentlyContinue).CounterSamples) {
        if ($s.InstanceName -notmatch 'pid_(\d+)_') { continue }
        $ok = if ($RootPid -gt 0) { $pids.Contains([int]$Matches[1]) } else { $false }
        if (-not $ok) { continue }
        if ($s.Path -like '*local usage*') { $gl += $s.CookedValue } else { $gd += $s.CookedValue }
    }

    # the GPU counter query above takes real time, so divide CPU by the true interval
    $elapsed = ((Get-Date) - $stamp).TotalSeconds
    if ($elapsed -le 0) { $elapsed = 1 }
    $wsSum += $ws / 1MB; $privSum += $priv / 1MB; $cpuSum += $cpu * 100 / $elapsed; $utilSum += $u
    if ($u -gt $utilMax) { $utilMax = $u }
    $gmSum += $gl / 1MB; $gdSum += $gd / 1MB; $pSum += $procs.Count

    $rows += [pscustomobject]@{
        app          = $Label
        t            = $i + 1
        procs        = $procs.Count
        ram_ws_mb    = [math]::Round($ws / 1MB, 1)
        ram_priv_mb  = [math]::Round($priv / 1MB, 1)
        cpu_pct      = [math]::Round($cpu * 100 / $elapsed, 1)
        gpu_util_pct = [math]::Round($u, 1)
        gpu_mem_mb   = [math]::Round($gl / 1MB, 1)
        gpu_ded_mb   = [math]::Round($gd / 1MB, 1)
    }
}

if ($Out) { $rows | Export-Csv -Path $Out -Append -NoTypeInformation }

"{0,-42} procs {1,4:N1} | WS {2,7:N1} MB | Priv {3,7:N1} MB | CPU {4,5:N1} % | GPU {5,5:N1} % (max {6,5:N1}) | GPUmem {7,6:N1} MB | dedicated {8,6:N1} MB" -f `
    $Label, ($pSum / $Seconds), ($wsSum / $Seconds), ($privSum / $Seconds), ($cpuSum / $Seconds), ($utilSum / $Seconds), $utilMax, ($gmSum / $Seconds), ($gdSum / $Seconds)
"    engines: " + (($eng.GetEnumerator() | Where-Object { $_.Value -gt 0 } | Sort-Object Value -Descending | ForEach-Object { "{0}={1:N1}%" -f $_.Key, ($_.Value / $Seconds) }) -join '  ')
