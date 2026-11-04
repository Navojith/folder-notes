# Restores what prep-admin.ps1 changed, from <out>\prep-admin.orig.json. Elevated, in the VM:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Z:\restore-admin.ps1         (or \\VBoxSvr\drop\restore-admin.ps1)
# Every setting is restored and verified independently; one failure never stops the others. Writes happen only where the live
# value differs from the original, so it is safe to run any number of times and after a partial prep.
# It never deletes the JSON or the log. Exit code 0 = everything verified, 1 = at least one FAIL line.
# Tests dot-source this file with -LibraryOnly and call Invoke-Restore against a fake system adapter (artifacts/vm/tests).
param([switch]$LibraryOnly)
$ErrorActionPreference = 'Stop'

$LdKey      = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\explorer.exe'
$DumpValues = 'DumpType', 'DumpCount', 'DumpFolder'       # the only LocalDumps values prep changes, so the only ones restored
$ModeToPs   = @{ Auto = 'Automatic'; Manual = 'Manual'; Disabled = 'Disabled' }

# The real system adapter: every touch of the machine goes through one of these scriptblocks.
function SvcKeyPath([string]$n) { "HKLM:\SYSTEM\CurrentControlSet\Services\$n" }
function New-RealAdapter([string]$logPath) {
  $script:LogPath = $logPath
  @{
    Log = { param($m) $line = "$(Get-Date -Format o)  $m"; Write-Host $line; Add-Content -Path $script:LogPath -Value $line }
    SvcGet = { param($n) $c = Get-CimInstance Win32_Service -Filter "Name='$n'"; @{ StartMode = $c.StartMode; State = $c.State } }
    SvcSetStartMode = { param($n, $mode) Set-Service -Name $n -StartupType $ModeToPs[$mode] }
    SvcStart = { param($n) Start-Service -Name $n }
    SvcStop  = { param($n) Stop-Service -Name $n -Force }
    SvcWait  = { param($n, $state) try { (Get-Service -Name $n).WaitForStatus($state, [TimeSpan]::FromSeconds(30)); $true } catch { $false } }
    DelayedGet = { param($n)
      $k = Get-Item (SvcKeyPath $n)
      if ($k.GetValueNames() -contains 'DelayedAutostart') { @{ Exists = $true; Kind = $k.GetValueKind('DelayedAutostart').ToString(); Data = $k.GetValue('DelayedAutostart') } } else { @{ Exists = $false; Kind = $null; Data = $null } } }
    DelayedSet = { param($n, $kind, $data) New-ItemProperty (SvcKeyPath $n) -Name DelayedAutostart -PropertyType $kind -Value $data -Force | Out-Null }
    DelayedRemove = { param($n) Remove-ItemProperty (SvcKeyPath $n) -Name DelayedAutostart -ErrorAction SilentlyContinue }
    DumpGet = {
      if (-not (Test-Path $LdKey)) { return @{ KeyExists = $false; Values = @{} } }
      $k = Get-Item $LdKey; $v = @{}
      foreach ($n in $k.GetValueNames()) { $v[$n] = @{ Kind = $k.GetValueKind($n).ToString(); Data = $k.GetValue($n, $null, 'DoNotExpandEnvironmentNames') } }
      @{ KeyExists = $true; Values = $v } }
    DumpEnsureKey = { if (-not (Test-Path $LdKey)) { New-Item $LdKey -Force | Out-Null } }
    DumpSet = { param($n, $kind, $data) New-ItemProperty $LdKey -Name $n -PropertyType $kind -Value $data -Force | Out-Null }
    DumpRemove = { param($n) Remove-ItemProperty $LdKey -Name $n -ErrorAction SilentlyContinue }
    DumpKeyRemove = { Remove-Item $LdKey -Recurse -Force -ErrorAction SilentlyContinue }
    MpDayGet = { [int](Get-MpPreference).ScanScheduleDay }
    MpDaySet = { param($d) Set-MpPreference -ScanScheduleDay $d }
    MpExclusionsGet = { @((Get-MpPreference).ExclusionPath | Where-Object { $_ }) }
    MpExclusionRemove = { param($p) Remove-MpPreference -ExclusionPath $p }
    MpStatusGet = { $s = Get-MpComputerStatus; @{ RealTime = [bool]$s.RealTimeProtectionEnabled; Tamper = [bool]$s.IsTamperProtected } }
  }
}

function Invoke-Restore($o, $Sys) {
  $script:RestoreFails = @()
  # $do restores; $check returns '' when the live value equals the original, else a message. Both guarded; a failure is logged and counted.
  function Step([string]$name, [scriptblock]$do, [scriptblock]$check) {
    $bad = $null
    try { & $do | Out-Null } catch { $bad = "action error: $($_.Exception.Message)" }
    try { $r = (@(& $check) -join '; '); if ($r) { $bad = if ($bad) { "$bad; $r" } else { $r } } } catch { $r = "verify error: $($_.Exception.Message)"; $bad = if ($bad) { "$bad; $r" } else { $r } }
    if ($bad) { & $Sys.Log "FAIL     $name : $bad"; $script:RestoreFails += $name } else { & $Sys.Log "VERIFIED $name" }
  }
  if ($null -eq $o) { & $Sys.Log 'RESTORE: no recorded originals, so prep-admin.ps1 changed nothing. Nothing to restore.'; return [pscustomobject]@{ Fails = @() } }
  & $Sys.Log "RESTORE start (originals recorded $($o.Recorded))"

  # 1. LocalDumps\explorer.exe: ONLY DumpType, DumpCount, DumpFolder (CustomDumpFlags and anything else is never touched)
  $ldo = $o.LocalDumpsExplorer
  Step 'LocalDumps DumpType/DumpCount/DumpFolder' {
    $cur = & $Sys.DumpGet
    if ($ldo.KeyExists) { if (-not $cur.KeyExists) { & $Sys.DumpEnsureKey } }
    foreach ($n in $DumpValues) {
      $v = $ldo.Values.$n; $c = $cur.Values[$n]
      if ($ldo.KeyExists -and $v) { if (-not $c -or "$($c.Data)" -ne "$($v.Data)" -or $c.Kind -ne $v.Kind) { & $Sys.DumpSet $n $v.Kind $v.Data } }
      elseif ($c) { & $Sys.DumpRemove $n }
    }
    if (-not $ldo.KeyExists) {                       # key was absent: remove it again, but only if nothing else lives in it
      $after = & $Sys.DumpGet
      if ($after.KeyExists -and $after.Values.Count -eq 0) { & $Sys.DumpKeyRemove }
      elseif ($after.KeyExists) { & $Sys.Log "NOTE      LocalDumps key left in place: it now holds other values ($($after.Values.Keys -join ','))" }
    }
  } {
    $cur = & $Sys.DumpGet; $diff = @()
    foreach ($n in $DumpValues) {
      $v = $ldo.Values.$n; $c = $cur.Values[$n]
      if ($ldo.KeyExists -and $v) { if (-not $c) { $diff += "$n absent (want $($v.Data))" } elseif ("$($c.Data)" -ne "$($v.Data)" -or $c.Kind -ne $v.Kind) { $diff += "$n=$($c.Data)/$($c.Kind) (want $($v.Data)/$($v.Kind))" } }
      elseif ($c) { $diff += "$n still present (originally absent)" }
    }
    if (-not $ldo.KeyExists -and $cur.KeyExists -and $cur.Values.Count -eq 0) { $diff += 'empty key still exists (originally absent)' }
    $diff
  }

  # 2. Services, each in three independent steps: run state, start mode, DelayedAutostart (exact existence/type/data)
  foreach ($s in 'WSearch', 'SysMain') {
    $so = $o.Services.$s
    $wantState = if ($so.State -eq 'Running') { 'Running' } else { 'Stopped' }
    Step "service $s state ($wantState)" {
      $cur = & $Sys.SvcGet $s
      if ($wantState -eq 'Running' -and $cur.State -ne 'Running') {
        # a Disabled service cannot be started: give it a startable mode first; the start-mode step then sets the original mode
        if ($cur.StartMode -eq 'Disabled') { & $Sys.SvcSetStartMode $s $(if ($so.StartMode -eq 'Disabled') { 'Manual' } else { $so.StartMode }) }
        & $Sys.SvcStart $s
        if (-not (& $Sys.SvcWait $s 'Running')) { throw 'did not reach Running within the wait' }
      } elseif ($wantState -eq 'Stopped' -and $cur.State -ne 'Stopped') {
        & $Sys.SvcStop $s
        if (-not (& $Sys.SvcWait $s 'Stopped')) { throw 'did not reach Stopped within the wait' }
      }
    } {
      $now = (& $Sys.SvcGet $s).State; if ($now -ne $wantState) { "State=$now (want $wantState)" }
    }
    Step "service $s start mode ($($so.StartMode))" {
      if ((& $Sys.SvcGet $s).StartMode -ne $so.StartMode) { & $Sys.SvcSetStartMode $s $so.StartMode }
    } {
      $now = (& $Sys.SvcGet $s).StartMode; if ($now -ne $so.StartMode) { "StartMode=$now (want $($so.StartMode))" }
    }
    $da = $so.DelayedAutostart
    $daText = if ($da.Exists) { "$($da.Kind) $($da.Data)" } else { 'absent' }
    Step "service $s DelayedAutostart ($daText)" {
      $cur = & $Sys.DelayedGet $s
      if ($da.Exists) { if (-not $cur.Exists -or $cur.Kind -ne $da.Kind -or "$($cur.Data)" -ne "$($da.Data)") { & $Sys.DelayedSet $s $da.Kind $da.Data } }
      elseif ($cur.Exists) { & $Sys.DelayedRemove $s }
    } {
      $cur = & $Sys.DelayedGet $s
      if ($da.Exists) { if (-not $cur.Exists) { 'absent (want present)' } elseif ($cur.Kind -ne $da.Kind -or "$($cur.Data)" -ne "$($da.Data)") { "now $($cur.Kind) $($cur.Data)" } }
      elseif ($cur.Exists) { "now $($cur.Kind) $($cur.Data) (originally absent)" }
    }
  }

  # 3. Defender scan day (the only schedule setting prep changed)
  Step 'Defender ScanScheduleDay' { if ((& $Sys.MpDayGet) -ne [int]$o.Defender.ScanScheduleDay) { & $Sys.MpDaySet ([int]$o.Defender.ScanScheduleDay) } } {
    $now = & $Sys.MpDayGet; if ($now -ne [int]$o.Defender.ScanScheduleDay) { "ScanScheduleDay=$now (want $($o.Defender.ScanScheduleDay))" }
  }

  # 4. Defender exclusions: remove ONLY the paths prep recorded as added by itself, one at a time
  foreach ($p in @($o.ExclusionsAddedByScript | Where-Object { $_ })) {
    Step "Defender exclusion $p removed" { if ((& $Sys.MpExclusionsGet) -contains $p) { & $Sys.MpExclusionRemove $p } } {
      if ((& $Sys.MpExclusionsGet) -contains $p) { 'still present' }
    }
  }
  Step 'Defender original exclusions intact' { } {
    $now = @(& $Sys.MpExclusionsGet); $lost = @($o.Defender.ExclusionPath | Where-Object { $_ -and ($now -notcontains $_) })
    if ($lost.Count) { "missing: $($lost -join ';')" }
  }

  # 5. Report-only: real-time and Tamper Protection must equal what was recorded (never changed by prep or restore)
  Step 'Defender real-time / Tamper Protection unchanged' { } {
    $st = & $Sys.MpStatusGet; $diff = @()
    if ($st.RealTime -ne [bool]$o.Defender.RealTimeProtectionEnabled) { $diff += "RealTime=$($st.RealTime) (was $($o.Defender.RealTimeProtectionEnabled))" }
    if ($st.Tamper -ne [bool]$o.Defender.IsTamperProtected) { $diff += "Tamper=$($st.Tamper) (was $($o.Defender.IsTamperProtected))" }
    $diff
  }
  [pscustomobject]@{ Fails = @($script:RestoreFails) }
}

if ($LibraryOnly) { return }

# ---- main ----
$out = $null
foreach ($p in 'X:\', '\\VBoxSvr\out\') { if (Test-Path $p) { $out = $p; break } }
if (-not $out) { throw 'Neither X:\ nor \\VBoxSvr\out\ is reachable; nothing was changed.' }
$log = Join-Path $out 'prep-admin.log'; $orig = Join-Path $out 'prep-admin.orig.json'
$id = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $id.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an elevated PowerShell.' }
$sys = New-RealAdapter $log
$o = if (Test-Path $orig) { Get-Content $orig -Raw | ConvertFrom-Json } else { $null }
$res = Invoke-Restore $o $sys
if ($res.Fails.Count) { & $sys.Log "RESTORE finished WITH $($res.Fails.Count) FAILURE(S): $($res.Fails -join ' | '). Re-run after fixing; originals kept in $orig"; exit 1 }
& $sys.Log "RESTORE finished: every setting verified. $orig and this log are kept; archive them yourself. C:\dumps (and any dumps) left in place; C:\dotnet untouched."
exit 0
