<#
  Resource regression check for REQ-080 / NFR-06 (specs/06 § Resource budgets, ADR-016).

  Starts a fresh tray process and measures, in order:
    1. tray-only idle: private working set, private bytes, CPU over an idle window
    2. N editor open/close cycles (default 8): memory with the editor open, and 10 s after close
    3. N main window open/close cycles (default 8): the library on All notes, open and 10 s after close
    4. idle CPU again after the windows were used
  Then checks the budgets and the cycle-to-cycle growth, prints a table, and exits 1 on a failure.

  The editor is opened with `--edit` on an empty scratch folder (-Folder) and closed without typing, so it is a
  draft that is never saved (ADR-014): the check leaves nothing in the notes database.

  Run against a Release build with Windows PowerShell 5.1:
    powershell -File eng\perf-check.ps1                       # 8 editor + 8 main window cycles, ~7 minutes
    powershell -File eng\perf-check.ps1 -Cycles 120 -GrowthBudgetMB 8   # extended session (~30 minutes)
  It kills any running FolderNotes process first.
#>
param(
    [string]$Exe = (Join-Path $PSScriptRoot '..\src\FolderNotes.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\FolderNotes.exe'),
    [int]$Cycles = 8,
    [int]$MainCycles = 8,
    [string]$Folder = 'C:\Temp\FolderNotesPerf\perf-check',
    [int]$IdleSeconds = 60,
    # Budgets (REQ-080 / NFR-06). Initial targets; re-baselined after Phase 3 (O-16).
    [double]$TrayBudgetMB = 50,
    [double]$EditorBudgetMB = 90,
    [double]$GrowthBudgetMB = 4,     # private bytes after close, cycle 2 -> last cycle
    [double]$IdleCpuBudgetPct = 0.1  # average % of one core while idle
)
$ErrorActionPreference = 'Stop'
$Exe = (Resolve-Path $Exe).Path
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PerfU {
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
'@

$folder = $Folder
$title = (Split-Path $folder -Leaf) + ' ' + [char]0x00B7 + ' Folder Notes'

function Proc { Get-Process FolderNotes -ErrorAction Stop | Select-Object -First 1 }
function Mem {
    $p = Proc
    $pid0 = $p.Id
    # The counter instance name can carry a #n suffix; pick the one whose ID matches.
    $ws = $null
    foreach ($s in (Get-Counter '\Process(FolderNotes*)\ID Process', '\Process(FolderNotes*)\Working Set - Private').CounterSamples) {
        if ($s.Path -like '*\id process' -and [int]$s.CookedValue -eq $pid0) { $inst = $s.InstanceName }
    }
    foreach ($s in (Get-Counter "\Process($inst)\Working Set - Private").CounterSamples) { $ws = $s.CookedValue }
    [pscustomobject]@{ WsMB = [math]::Round($ws / 1MB, 1); PrivMB = [math]::Round($p.PrivateMemorySize64 / 1MB, 1) }
}
function IdleCpu([int]$seconds) {
    $p = Proc; $t0 = $p.TotalProcessorTime; $sw = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Seconds $seconds
    $p.Refresh(); $t1 = $p.TotalProcessorTime
    [math]::Round(100 * ($t1 - $t0).TotalMilliseconds / $sw.Elapsed.TotalMilliseconds, 3)
}
function WaitWindow([string]$windowTitle) {
    $deadline = (Get-Date).AddSeconds(15)
    do {
        # WinUI windows only: the tray's hidden message window is also titled "Folder Notes".
        $h = [PerfU]::FindWindow('WinUIDesktopWin32WindowClass', $windowTitle)
        if ($h -ne [IntPtr]::Zero -and [PerfU]::IsWindowVisible($h)) { return $h }
        Start-Sleep -Milliseconds 100
    } until ((Get-Date) -gt $deadline)
    throw "window '$windowTitle' did not appear"
}
function Cycles([int]$count, [string]$arguments, [string]$windowTitle, [string]$name) {
    $result = @()
    for ($i = 1; $i -le $count; $i++) {
        if ($name -eq 'editor') { New-Item -ItemType Directory -Force $folder | Out-Null }  # must exist, or the editor shows 'folder missing'
        $sw = [Diagnostics.Stopwatch]::StartNew()
        if ($arguments) { Start-Process $Exe $arguments } else { Start-Process $Exe }
        $h = WaitWindow $windowTitle
        $openMs = $sw.ElapsedMilliseconds
        Start-Sleep -Seconds 2
        $open = Mem
        [PerfU]::PostMessage($h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null  # WM_CLOSE
        Start-Sleep -Seconds 10
        $closed = Mem
        $result += [pscustomobject]@{ Cycle = $i; OpenMs = $openMs; OpenWs = $open.WsMB; OpenPriv = $open.PrivMB; ClosedWs = $closed.WsMB; ClosedPriv = $closed.PrivMB }
        Write-Host ("{0,-6} {1,3}:     open {2} / {3} MB, closed {4} / {5} MB (window in {6} ms)" -f $name, $i, $open.WsMB, $open.PrivMB, $closed.WsMB, $closed.PrivMB, $openMs)
    }
    return ,$result
}
function Growth($rows) {
    $base = if ($rows.Count -ge 2) { $rows[1].ClosedPriv } else { $rows[0].ClosedPriv }
    [math]::Round($rows[-1].ClosedPriv - $base, 1)
}

Get-Process FolderNotes -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Start-Process $Exe '--background'
Start-Sleep -Seconds 12
$tray = Mem
$trayCpu = IdleCpu $IdleSeconds
Write-Host ("tray only:      {0} MB working set, {1} MB private bytes, {2}% CPU idle" -f $tray.WsMB, $tray.PrivMB, $trayCpu)

$rows = Cycles $Cycles "--edit `"$folder`"" $title 'editor'
$mainRows = if ($MainCycles -gt 0) { Cycles $MainCycles '' 'Folder Notes' 'main' } else { @() }
$usedCpu = IdleCpu $IdleSeconds
Write-Host ("after use:      {0}% CPU idle" -f $usedCpu)
Get-Process FolderNotes | Stop-Process -Force

$maxOpenWs = ($rows | Measure-Object OpenWs -Maximum).Maximum
$maxClosedWs = ($rows | Measure-Object ClosedWs -Maximum).Maximum
$growth = Growth $rows

$checks = @(
    [pscustomobject]@{ Check = 'tray-only working set'; Budget = "< $TrayBudgetMB MB"; Measured = "$($tray.WsMB) MB"; Pass = $tray.WsMB -lt $TrayBudgetMB }
    [pscustomobject]@{ Check = 'editor open working set (max)'; Budget = "< $EditorBudgetMB MB"; Measured = "$maxOpenWs MB"; Pass = $maxOpenWs -lt $EditorBudgetMB }
    [pscustomobject]@{ Check = 'after close working set (max)'; Budget = "< $EditorBudgetMB MB"; Measured = "$maxClosedWs MB"; Pass = $maxClosedWs -lt $EditorBudgetMB }
    [pscustomobject]@{ Check = "private bytes growth, cycle 2 -> $Cycles"; Budget = "<= $GrowthBudgetMB MB"; Measured = "$growth MB"; Pass = $growth -le $GrowthBudgetMB }
    if ($mainRows.Count -gt 0) {
        $maxMainOpen = ($mainRows | Measure-Object OpenWs -Maximum).Maximum
        $maxMainClosed = ($mainRows | Measure-Object ClosedWs -Maximum).Maximum
        $mainGrowth = Growth $mainRows
        [pscustomobject]@{ Check = 'main window open working set (max)'; Budget = "< $EditorBudgetMB MB"; Measured = "$maxMainOpen MB"; Pass = $maxMainOpen -lt $EditorBudgetMB }
        [pscustomobject]@{ Check = 'main window closed working set (max)'; Budget = "< $EditorBudgetMB MB"; Measured = "$maxMainClosed MB"; Pass = $maxMainClosed -lt $EditorBudgetMB }
        [pscustomobject]@{ Check = "main window growth, cycle 2 -> $MainCycles"; Budget = "<= $GrowthBudgetMB MB"; Measured = "$mainGrowth MB"; Pass = $mainGrowth -le $GrowthBudgetMB }
    }
    [pscustomobject]@{ Check = 'idle CPU, tray only'; Budget = "< $IdleCpuBudgetPct %"; Measured = "$trayCpu %"; Pass = $trayCpu -lt $IdleCpuBudgetPct }
    [pscustomobject]@{ Check = 'idle CPU, after use'; Budget = "< $IdleCpuBudgetPct %"; Measured = "$usedCpu %"; Pass = $usedCpu -lt $IdleCpuBudgetPct }
)
$checks | Format-Table -AutoSize | Out-String | Write-Host
if ($checks | Where-Object { -not $_.Pass }) { Write-Host 'FAIL'; exit 1 }
Write-Host 'PASS'
