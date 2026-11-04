# Tests for artifacts/vm/drop/restore-admin.ps1 (T3.2.5 VM preparation tooling). No admin, no VM, no real system access:
# the restore logic runs against an in-memory fake adapter.   Run:  powershell -NoProfile -File artifacts\vm\tests\restore-admin.tests.ps1
# Exit code 0 = all pass.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\drop\restore-admin.ps1') -LibraryOnly

$script:pass = 0; $script:failed = @()
function Check([string]$name, $cond) { if ($cond) { $script:pass++ } else { $script:failed += $name; Write-Host "  FAIL: $name" -ForegroundColor Red } }
function Section($t) { Write-Host "== $t" }

# ---- fake system ----
function New-Fake {
  $fx = @{
    Svc = @{ WSearch = @{ StartMode = 'Disabled'; State = 'Stopped' }; SysMain = @{ StartMode = 'Disabled'; State = 'Stopped' } }
    Delayed = @{ WSearch = @{ Exists = $false; Kind = $null; Data = $null }; SysMain = @{ Exists = $false; Kind = $null; Data = $null } }
    Dump = @{ KeyExists = $true; Values = @{ DumpType = @{ Kind = 'DWord'; Data = 2 }; DumpCount = @{ Kind = 'DWord'; Data = 3 }; DumpFolder = @{ Kind = 'ExpandString'; Data = 'C:\dumps' } } }
    Day = 8; Excl = @('C:\Existing', 'C:\T325'); RT = $true; Tamper = $true
    Stuck = @{}            # service name -> 'stop' or 'start': the SCM never completes that transition
    Writes = New-Object System.Collections.ArrayList; Logs = New-Object System.Collections.ArrayList
  }
  $w = { param($m) [void]$fx.Writes.Add($m) }.GetNewClosure()
  $fx.Sys = @{
    Log = { param($m) [void]$fx.Logs.Add($m) }
    SvcGet = { param($n) @{ StartMode = $fx.Svc[$n].StartMode; State = $fx.Svc[$n].State } }
    SvcSetStartMode = { param($n, $mode) & $w "mode $n $mode"; $fx.Svc[$n].StartMode = $mode }
    SvcStart = { param($n) & $w "start $n"; if ($fx.Svc[$n].StartMode -eq 'Disabled') { throw 'Cannot start service: it is disabled' }; if ($fx.Stuck[$n] -ne 'start') { $fx.Svc[$n].State = 'Running' } }
    SvcStop  = { param($n) & $w "stop $n"; if ($fx.Stuck[$n] -ne 'stop') { $fx.Svc[$n].State = 'Stopped' } }
    SvcWait  = { param($n, $st) $fx.Svc[$n].State -eq $st }
    DelayedGet = { param($n) $d = $fx.Delayed[$n]; @{ Exists = $d.Exists; Kind = $d.Kind; Data = $d.Data } }
    DelayedSet = { param($n, $k, $v) & $w "delayed-set $n"; $fx.Delayed[$n] = @{ Exists = $true; Kind = $k; Data = $v } }
    DelayedRemove = { param($n) & $w "delayed-remove $n"; $fx.Delayed[$n] = @{ Exists = $false; Kind = $null; Data = $null } }
    DumpGet = { $v = @{}; foreach ($k in $fx.Dump.Values.Keys) { $v[$k] = $fx.Dump.Values[$k] }; @{ KeyExists = $fx.Dump.KeyExists; Values = $v } }
    DumpEnsureKey = { & $w 'dump-ensure-key'; $fx.Dump.KeyExists = $true }
    DumpSet = { param($n, $k, $v) & $w "dump-set $n"; $fx.Dump.Values[$n] = @{ Kind = $k; Data = $v } }
    DumpRemove = { param($n) & $w "dump-remove $n"; $fx.Dump.Values.Remove($n) }
    DumpKeyRemove = { & $w 'dump-key-remove'; $fx.Dump.KeyExists = $false; $fx.Dump.Values = @{} }
    MpDayGet = { $fx.Day }
    MpDaySet = { param($d) & $w "day $d"; $fx.Day = $d }
    MpExclusionsGet = { @($fx.Excl) }
    MpExclusionRemove = { param($p) & $w "excl-remove $p"; $fx.Excl = @($fx.Excl | Where-Object { $_ -ne $p }) }
    MpStatusGet = { @{ RealTime = $fx.RT; Tamper = $fx.Tamper } }
  }
  foreach ($k in @($fx.Sys.Keys)) { $fx.Sys[$k] = $fx.Sys[$k].GetNewClosure() }   # bind $fx and $w: scriptblocks otherwise resolve variables from the CALLER
  $fx
}
# Original state as prep-admin.ps1 would write it, round-tripped through JSON exactly like the real restore reads it.
function New-Orig([hashtable]$over = @{}) {
  $svc = { param($mode, $state, $da) @{ StartMode = $mode; State = $state; DelayedAutostart = $da } }
  $absent = @{ Exists = $false; Kind = $null; Data = $null }
  $o = @{
    Recorded = '2026-11-04T00:00:00'
    LocalDumpsExplorer = @{ KeyExists = $false; Values = @{} }
    Services = @{ WSearch = (& $svc 'Auto' 'Running' $absent); SysMain = (& $svc 'Auto' 'Running' $absent) }
    Defender = @{ ScanScheduleDay = 0; ExclusionPath = @('C:\Existing'); RealTimeProtectionEnabled = $true; IsTamperProtected = $true }
    ExclusionsAddedByScript = @('C:\T325')
  }
  foreach ($k in $over.Keys) { $o[$k] = $over[$k] }
  ($o | ConvertTo-Json -Depth 8) | ConvertFrom-Json
}
function Run($o, $fx) { $r = Invoke-Restore $o $fx.Sys; if ($r.Fails.Count) { Write-Host "    (restore reported: $($r.Fails -join ' | '))" }; $r }
function Logged($fx, $prefix, $text) { @($fx.Logs | Where-Object { $_.StartsWith($prefix) -and $_ -like "*$text*" }).Count -ge 1 }

# ---------------------------------------------------------------- service state
Section 'originally Running service (Auto) -> started and verified'
$fx = New-Fake; $o = New-Orig; $r = Run $o $fx
Check 'WSearch running again'   ($fx.Svc.WSearch.State -eq 'Running')
Check 'WSearch Auto again'      ($fx.Svc.WSearch.StartMode -eq 'Auto')
Check 'no failures'             ($r.Fails.Count -eq 0)
Check 'state VERIFIED logged'   (Logged $fx 'VERIFIED' 'service WSearch state (Running)')

Section 'originally Stopped service (Manual) that is currently Running -> stopped and verified'
$fx = New-Fake; $fx.Svc.SysMain = @{ StartMode = 'Manual'; State = 'Running' }
$o = New-Orig @{ Services = @{ WSearch = @{ StartMode = 'Manual'; State = 'Stopped'; DelayedAutostart = @{ Exists = $false } }; SysMain = @{ StartMode = 'Manual'; State = 'Stopped'; DelayedAutostart = @{ Exists = $false } } } }
$r = Run $o $fx
Check 'SysMain stopped'         ($fx.Svc.SysMain.State -eq 'Stopped')
Check 'SysMain Manual'          ($fx.Svc.SysMain.StartMode -eq 'Manual')
Check 'stop was issued'         ($fx.Writes -contains 'stop SysMain')
Check 'no failures'             ($r.Fails.Count -eq 0)

Section 'a service that cannot be stopped is NOT reported VERIFIED'
$fx = New-Fake; $fx.Svc.SysMain = @{ StartMode = 'Manual'; State = 'Running' }; $fx.Stuck.SysMain = 'stop'
$o = New-Orig @{ Services = @{ WSearch = @{ StartMode = 'Auto'; State = 'Running'; DelayedAutostart = @{ Exists = $false } }; SysMain = @{ StartMode = 'Manual'; State = 'Stopped'; DelayedAutostart = @{ Exists = $false } } } }
$r = Run $o $fx
Check 'FAIL logged for SysMain state'       (Logged $fx 'FAIL' 'service SysMain state (Stopped)')
Check 'not VERIFIED for SysMain state'      (-not (Logged $fx 'VERIFIED' 'service SysMain state'))
Check 'failure is reported in the result'   ($r.Fails -contains 'service SysMain state (Stopped)')
Check 'independent steps still ran: WSearch restored' ($fx.Svc.WSearch.State -eq 'Running')
Check 'independent steps still ran: SysMain start mode' (Logged $fx 'VERIFIED' 'service SysMain start mode (Manual)')

Section 'a service that will not start is reported as FAIL'
$fx = New-Fake; $fx.Stuck.WSearch = 'start'; $o = New-Orig; $r = Run $o $fx
Check 'FAIL for WSearch state' ($r.Fails -contains 'service WSearch state (Running)')
Check 'SysMain still restored' ($fx.Svc.SysMain.State -eq 'Running')

# ---------------------------------------------------------------- startup configuration
Section 'originally Auto and originally Disabled startup, both stopped by prep'
$fx = New-Fake
$o = New-Orig @{ Services = @{ WSearch = @{ StartMode = 'Auto'; State = 'Running'; DelayedAutostart = @{ Exists = $false } }; SysMain = @{ StartMode = 'Disabled'; State = 'Stopped'; DelayedAutostart = @{ Exists = $false } } } }
$r = Run $o $fx
Check 'Auto service: Auto/Running'      ($fx.Svc.WSearch.StartMode -eq 'Auto' -and $fx.Svc.WSearch.State -eq 'Running')
Check 'Disabled service: Disabled/Stopped' ($fx.Svc.SysMain.StartMode -eq 'Disabled' -and $fx.Svc.SysMain.State -eq 'Stopped')
Check 'no failures' ($r.Fails.Count -eq 0)

Section 'originally Running with a DISABLED startup configuration'
$fx = New-Fake   # prep left it Disabled/Stopped; the fake refuses to start a Disabled service, like Windows
$o = New-Orig @{ Services = @{ WSearch = @{ StartMode = 'Disabled'; State = 'Running'; DelayedAutostart = @{ Exists = $false } }; SysMain = @{ StartMode = 'Auto'; State = 'Running'; DelayedAutostart = @{ Exists = $false } } } }
$r = Run $o $fx
Check 'ends Running'            ($fx.Svc.WSearch.State -eq 'Running')
Check 'ends Disabled'           ($fx.Svc.WSearch.StartMode -eq 'Disabled')
Check 'went through a startable mode first' ($fx.Writes -contains 'mode WSearch Manual')
Check 'no failures'             ($r.Fails.Count -eq 0)

# ---------------------------------------------------------------- DelayedAutostart
Section 'DelayedAutostart originally ABSENT, currently present -> removed'
$fx = New-Fake; $fx.Delayed.WSearch = @{ Exists = $true; Kind = 'DWord'; Data = 1 }; $o = New-Orig; $r = Run $o $fx
Check 'value removed'           (-not $fx.Delayed.WSearch.Exists)
Check 'VERIFIED logged'         (Logged $fx 'VERIFIED' 'service WSearch DelayedAutostart (absent)')
Check 'no failures'             ($r.Fails.Count -eq 0)

Section 'DelayedAutostart originally ZERO (present) -> stays present as DWord 0, not removed'
$zero = @{ Exists = $true; Kind = 'DWord'; Data = 0 }
$fx = New-Fake   # currently absent
$o = New-Orig @{ Services = @{ WSearch = @{ StartMode = 'Auto'; State = 'Running'; DelayedAutostart = $zero }; SysMain = @{ StartMode = 'Auto'; State = 'Running'; DelayedAutostart = $zero } } }
$r = Run $o $fx
Check 'value present'           ($fx.Delayed.WSearch.Exists)
Check 'value is DWord 0'        ($fx.Delayed.WSearch.Kind -eq 'DWord' -and $fx.Delayed.WSearch.Data -eq 0)
Check 'never removed'           (-not ($fx.Writes -contains 'delayed-remove WSearch'))
Check 'no failures'             ($r.Fails.Count -eq 0)

Section 'DelayedAutostart originally 1, currently 0 -> set back to 1'
$fx = New-Fake; $fx.Delayed.WSearch = @{ Exists = $true; Kind = 'DWord'; Data = 0 }
$one = @{ Exists = $true; Kind = 'DWord'; Data = 1 }
$o = New-Orig @{ Services = @{ WSearch = @{ StartMode = 'Auto'; State = 'Running'; DelayedAutostart = $one }; SysMain = @{ StartMode = 'Auto'; State = 'Running'; DelayedAutostart = @{ Exists = $false } } } }
$r = Run $o $fx
Check 'value is 1'              ($fx.Delayed.WSearch.Data -eq 1)
Check 'no failures'             ($r.Fails.Count -eq 0)

# ---------------------------------------------------------------- LocalDumps
Section 'LocalDumps originally ABSENT -> the three values and the key are removed'
$fx = New-Fake; $o = New-Orig; $r = Run $o $fx
Check 'key gone'                (-not $fx.Dump.KeyExists)
Check 'no failures'             ($r.Fails.Count -eq 0)

Section 'LocalDumps originally absent but another value lives in the key -> three values removed, key and other value kept'
$fx = New-Fake; $fx.Dump.Values.CustomDumpFlags = @{ Kind = 'DWord'; Data = 5 }; $o = New-Orig; $r = Run $o $fx
Check 'key kept'                ($fx.Dump.KeyExists)
Check 'CustomDumpFlags untouched' ($fx.Dump.Values.CustomDumpFlags.Data -eq 5 -and -not ($fx.Writes -contains 'dump-remove CustomDumpFlags'))
Check 'three values gone'       (-not $fx.Dump.Values.ContainsKey('DumpType') -and -not $fx.Dump.Values.ContainsKey('DumpCount') -and -not $fx.Dump.Values.ContainsKey('DumpFolder'))
Check 'no failures'             ($r.Fails.Count -eq 0)

Section 'LocalDumps originally present -> exactly the three values restored; CustomDumpFlags never touched'
$fx = New-Fake; $fx.Dump.Values.CustomDumpFlags = @{ Kind = 'DWord'; Data = 99 }
$ldOrig = @{ KeyExists = $true; Values = @{ DumpType = @{ Kind = 'DWord'; Data = 1 }; DumpCount = @{ Kind = 'DWord'; Data = 10 }; CustomDumpFlags = @{ Kind = 'DWord'; Data = 7 } } }  # DumpFolder was absent
$o = New-Orig @{ LocalDumpsExplorer = $ldOrig }; $r = Run $o $fx
Check 'DumpType back to 1'      ($fx.Dump.Values.DumpType.Data -eq 1)
Check 'DumpCount back to 10'    ($fx.Dump.Values.DumpCount.Data -eq 10)
Check 'DumpFolder removed (was absent)' (-not $fx.Dump.Values.ContainsKey('DumpFolder'))
Check 'CustomDumpFlags not modified (still 99, not the recorded 7)' ($fx.Dump.Values.CustomDumpFlags.Data -eq 99)
Check 'no CustomDumpFlags write' (@($fx.Writes | Where-Object { $_ -like '*CustomDumpFlags*' }).Count -eq 0)
Check 'no failures'             ($r.Fails.Count -eq 0)

# ---------------------------------------------------------------- Defender
Section 'Defender: day restored; only the exclusion prep added is removed'
$fx = New-Fake; $fx.Excl = @('C:\Existing', 'C:\T325'); $o = New-Orig; $r = Run $o $fx
Check 'day back to 0'           ($fx.Day -eq 0)
Check 'T325 removed'            ($fx.Excl -notcontains 'C:\T325')
Check 'pre-existing exclusion kept' ($fx.Excl -contains 'C:\Existing')
Check 'no failures'             ($r.Fails.Count -eq 0)

Section 'Defender: an exclusion that already existed before prep is not removed'
$fx = New-Fake; $fx.Excl = @('C:\T325'); $o = New-Orig @{ ExclusionsAddedByScript = @(); Defender = @{ ScanScheduleDay = 0; ExclusionPath = @('C:\T325'); RealTimeProtectionEnabled = $true; IsTamperProtected = $true } }
$r = Run $o $fx
Check 'T325 kept'               ($fx.Excl -contains 'C:\T325')
Check 'no failures'             ($r.Fails.Count -eq 0)

Section 'Defender: real-time protection found disabled is reported, never changed'
$fx = New-Fake; $fx.RT = $false; $o = New-Orig; $r = Run $o $fx
Check 'reported as failure'     ($r.Fails -contains 'Defender real-time / Tamper Protection unchanged')
Check 'left as found'           ($fx.RT -eq $false)

# ---------------------------------------------------------------- partial preparation
Section 'partial prep: only LocalDumps was changed, services and Defender untouched'
$fx = New-Fake
$fx.Svc.WSearch = @{ StartMode = 'Auto'; State = 'Running' }; $fx.Svc.SysMain = @{ StartMode = 'Auto'; State = 'Running' }
$fx.Day = 0; $fx.Excl = @('C:\Existing')
$o = New-Orig; $r = Run $o $fx
Check 'dumps restored'          (-not $fx.Dump.KeyExists)
Check 'services never touched (no start/stop/mode writes)' (@($fx.Writes | Where-Object { $_ -like 'start*' -or $_ -like 'stop*' -or $_ -like 'mode*' }).Count -eq 0)
Check 'Defender never touched (no day/exclusion writes)'   (@($fx.Writes | Where-Object { $_ -like 'day*' -or $_ -like 'excl*' }).Count -eq 0)
Check 'all steps verified, no failures' ($r.Fails.Count -eq 0)

Section 'partial prep: failed before the JSON was written -> nothing to restore'
$fx = New-Fake; $r = Run $null $fx
Check 'no failures'             ($r.Fails.Count -eq 0)
Check 'nothing written'         ($fx.Writes.Count -eq 0)

Section 'partial prep: services stopped but Defender and LocalDumps untouched'
$fx = New-Fake; $fx.Dump = @{ KeyExists = $false; Values = @{} }; $fx.Day = 0; $fx.Excl = @('C:\Existing')
$o = New-Orig; $r = Run $o $fx
Check 'services restored'       ($fx.Svc.WSearch.State -eq 'Running' -and $fx.Svc.SysMain.State -eq 'Running')
Check 'no dump writes'          (@($fx.Writes | Where-Object { $_ -like 'dump*' }).Count -eq 0)
Check 'no failures'             ($r.Fails.Count -eq 0)

# ---------------------------------------------------------------- repeated restoration
Section 'repeated restoration: the second and third runs change nothing and still verify'
$fx = New-Fake; $o = New-Orig
$r1 = Run $o $fx; $n1 = $fx.Writes.Count
$r2 = Run $o $fx; $n2 = $fx.Writes.Count
$r3 = Run $o $fx
Check 'first run: no failures'  ($r1.Fails.Count -eq 0 -and $n1 -gt 0)
Check 'second run: no failures' ($r2.Fails.Count -eq 0)
Check 'second run made zero writes' ($n2 -eq $n1)
Check 'third run made zero writes'  ($fx.Writes.Count -eq $n1 -and $r3.Fails.Count -eq 0)
Check 'final state is the original' ($fx.Svc.WSearch.State -eq 'Running' -and $fx.Day -eq 0 -and -not $fx.Dump.KeyExists -and $fx.Excl -notcontains 'C:\T325')

Write-Host ''
Write-Host "passed $script:pass, failed $($script:failed.Count)"
if ($script:failed.Count) { $script:failed | ForEach-Object { Write-Host "  - $_" }; exit 1 }
exit 0
