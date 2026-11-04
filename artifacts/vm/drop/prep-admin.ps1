# T3.2.5 VM preparation, ELEVATED part. Run in the VM from an elevated PowerShell:
#   powershell -NoProfile -ExecutionPolicy Bypass -File Z:\prep-admin.ps1            (or \\VBoxSvr\drop\prep-admin.ps1)
# Originals go to <out>\prep-admin.log (human) and <out>\prep-admin.orig.json (read by restore-admin.ps1); <out> is X:\ or \\VBoxSvr\out\.
# Nothing is changed until preflight passes and the originals file is written and CONFIRMED (byte-identical, parsed, fully validated). Windows Update configuration is NOT changed.
# Defender real-time protection and Tamper Protection are NOT touched. Preparation refuses to start if real-time protection is off,
# or if WSearch/SysMain are missing or in a state/start mode restore cannot reproduce. The only exclusion is C:\T325.
# Exit code: 0 = prepared and every VERIFY passed; 1 = refused, errored, or a VERIFY failed. The originals JSON is never deleted.
# Tests dot-source this file with -LibraryOnly and call the two functions below (artifacts/vm/tests/prep-admin.tests.ps1).
param([switch]$LibraryOnly)
$ErrorActionPreference = 'Stop'

# Preflight: returns a list of problems (empty = ok). Restore can only reproduce Running/Stopped and Auto/Manual/Disabled,
# so a transitional state (StartPending, StopPending, Paused, ...) or another start mode is refused rather than mis-recorded.
function Test-ServicePreflight($svcs) {
  foreach ($s in $svcs) {
    if (-not $s.Exists) { "service $($s.Name) does not exist"; continue }
    if ($s.State -notin 'Running', 'Stopped') { "service $($s.Name) is in state '$($s.State)' (supported: Running, Stopped)" }
    if ($s.StartMode -notin 'Auto', 'Manual', 'Disabled') { "service $($s.Name) has start mode '$($s.StartMode)' (supported: Auto, Manual, Disabled)" }
  }
}

# Post-change verification: $live is what was read back from the machine; returns the log lines, Ok and the exit code.
function Test-PrepResult($live, $tamperOriginal, $wanted) {
  $lines = New-Object System.Collections.ArrayList; $ok = $true
  function Add($good, $okText, $failText) { if ($good) { [void]$lines.Add("VERIFY ok: $okText") } else { [void]$lines.Add("VERIFY FAIL: $failText"); $script:ok2 = $false } }
  $script:ok2 = $true
  $d = $live.Dump
  Add ($d.DumpType -eq 2 -and $d.DumpFolder -eq 'C:\dumps' -and $d.DumpCount -eq 3) 'LocalDumps DumpType=2 (full dump), DumpCount=3, DumpFolder=C:\dumps' "LocalDumps values (DumpType=$($d.DumpType) DumpCount=$($d.DumpCount) DumpFolder=$($d.DumpFolder))"
  foreach ($n in 'WSearch', 'SysMain') {
    $c = $live.Services[$n]
    Add ($c.StartMode -eq 'Disabled' -and $c.State -eq 'Stopped') "$n Disabled/Stopped" "$n $($c.StartMode)/$($c.State)"
  }
  Add ($live.Day -eq 8) 'ScanScheduleDay=8' "ScanScheduleDay=$($live.Day) (Tamper Protection may have blocked it)"
  foreach ($p in $wanted) { Add ($live.Exclusions -contains $p) "exclusion $p present" "exclusion $p missing (Tamper Protection may have blocked it)" }
  Add ($live.RealTime) 'real-time protection still enabled' 'real-time protection is NOT enabled after preparation'
  Add ($live.Tamper -eq $tamperOriginal) "Tamper Protection unchanged ($($live.Tamper))" "Tamper Protection status changed (was $tamperOriginal, now $($live.Tamper))"
  $ok = $script:ok2
  [pscustomobject]@{ Ok = $ok; Lines = @($lines); ExitCode = $(if ($ok) { 0 } else { 1 }) }
}

# Validator for the originals file: the complete contract restore-admin.ps1 consumes (every property it reads, with types and values).
# Returns a list of problems (empty = valid). Restore reads: Recorded; LocalDumpsExplorer.KeyExists and .Values.{DumpType,DumpCount,DumpFolder}.{Kind,Data};
# Services.{WSearch,SysMain}.{State,StartMode,DelayedAutostart.{Exists,Kind,Data}}; Defender.{ScanScheduleDay,ExclusionPath,RealTimeProtectionEnabled,
# IsTamperProtected}; ExclusionsAddedByScript. Build and DumpsFolderExisted are written for humans and only type-checked.
function Test-OriginalsValid($o) {
  $p = New-Object System.Collections.ArrayList
  function Bad($m) { [void]$p.Add($m) }
  function Has($obj, $name) { $null -ne $obj -and $obj -isnot [string] -and ($obj.PSObject.Properties.Name -contains $name) }
  function IsObj($v) { $v -is [pscustomobject] }
  function IsInt($v) { $v -is [int] -or $v -is [long] }
  function IsBool($v) { $v -is [bool] }
  function IsText($v) { $v -is [string] -and $v.Length -gt 0 }
  function IsTextList($v) { ($v -is [array]) -and (@($v | Where-Object { -not (IsText $_) }).Count -eq 0) }
  # a registry value restore will write back with New-ItemProperty -PropertyType <Kind> -Value <Data>
  function CheckTyped($label, $kind, $data) {
    if ($kind -notin 'DWord', 'QWord', 'String', 'ExpandString') { Bad "$label.Kind '$kind' is not restorable (DWord, QWord, String or ExpandString)"; return }
    if ($kind -in 'DWord', 'QWord') { if (-not (IsInt $data)) { Bad "$label.Data must be an integer for $kind (got '$data')" } }
    elseif ($data -isnot [string]) { Bad "$label.Data must be a string for $kind (got '$data')" }
  }

  if (-not (IsObj $o)) { 'originals is not a JSON object'; return }
  foreach ($n in 'Build', 'Recorded', 'LocalDumpsExplorer', 'DumpsFolderExisted', 'Services', 'Defender', 'ExclusionsAddedByScript') { if (-not (Has $o $n)) { Bad "missing top-level property '$n'" } }
  if ((Has $o 'Build') -and -not (IsText $o.Build)) { Bad 'Build must be a non-empty string' }
  if (Has $o 'Recorded') { $dt = [datetime]::MinValue; if (-not (($o.Recorded -is [datetime]) -or (($o.Recorded -is [string]) -and [datetime]::TryParse($o.Recorded, [ref]$dt)))) { Bad "Recorded '$($o.Recorded)' is not a date/time" } }
  if ((Has $o 'DumpsFolderExisted') -and -not (IsBool $o.DumpsFolderExisted)) { Bad 'DumpsFolderExisted must be true/false' }

  # LocalDumps
  if (Has $o 'LocalDumpsExplorer') {
    $ld = $o.LocalDumpsExplorer
    if (-not (IsObj $ld)) { Bad 'LocalDumpsExplorer must be an object' }
    else {
      $keyExistsOk = (Has $ld 'KeyExists') -and (IsBool $ld.KeyExists)
      if (-not $keyExistsOk) { Bad 'LocalDumpsExplorer.KeyExists must be true/false' }
      if (-not ((Has $ld 'Values') -and (IsObj $ld.Values))) { Bad 'LocalDumpsExplorer.Values must be an object' }
      else {
        if ($keyExistsOk -and -not $ld.KeyExists -and @($ld.Values.PSObject.Properties).Count -gt 0) { Bad 'LocalDumpsExplorer.KeyExists is false but Values is not empty' }
        foreach ($n in 'DumpType', 'DumpCount', 'DumpFolder') {
          if (-not (Has $ld.Values $n)) { continue }
          $v = $ld.Values.$n
          if (-not ((IsObj $v) -and (Has $v 'Kind') -and (Has $v 'Data'))) { Bad "LocalDumpsExplorer.Values.$n must be an object with Kind and Data"; continue }
          CheckTyped "LocalDumpsExplorer.Values.$n" $v.Kind $v.Data
        }
      }
    }
  }

  # Services
  if (Has $o 'Services') {
    if (-not (IsObj $o.Services)) { Bad 'Services must be an object' }
    else {
      foreach ($s in 'WSearch', 'SysMain') {
        if (-not (Has $o.Services $s)) { Bad "Services is missing '$s'"; continue }
        $sv = $o.Services.$s
        if (-not (IsObj $sv)) { Bad "Services.$s must be an object"; continue }
        foreach ($n in 'StartMode', 'State', 'DelayedAutostart') { if (-not (Has $sv $n)) { Bad "Services.$s is missing '$n'" } }
        if ((Has $sv 'State') -and $sv.State -notin 'Running', 'Stopped') { Bad "Services.$s.State '$($sv.State)' is not Running/Stopped" }
        if ((Has $sv 'StartMode') -and $sv.StartMode -notin 'Auto', 'Manual', 'Disabled') { Bad "Services.$s.StartMode '$($sv.StartMode)' is not Auto/Manual/Disabled" }
        if (Has $sv 'DelayedAutostart') {
          $da = $sv.DelayedAutostart
          if (-not (IsObj $da)) { Bad "Services.$s.DelayedAutostart must be an object"; continue }
          foreach ($n in 'Exists', 'Kind', 'Data') { if (-not (Has $da $n)) { Bad "Services.$s.DelayedAutostart is missing '$n'" } }
          if (-not ((Has $da 'Exists') -and (Has $da 'Kind') -and (Has $da 'Data'))) { continue }
          if (-not (IsBool $da.Exists)) { Bad "Services.$s.DelayedAutostart.Exists must be true/false" }
          elseif ($da.Exists) {
            CheckTyped "Services.$s.DelayedAutostart" $da.Kind $da.Data
            if ($da.Kind -ne 'DWord') { Bad "Services.$s.DelayedAutostart.Kind must be DWord (got '$($da.Kind)')" }
          }
          elseif ($null -ne $da.Kind -or $null -ne $da.Data) { Bad "Services.$s.DelayedAutostart.Exists is false but Kind/Data are set" }
        }
      }
    }
  }

  # Defender
  $origExcl = $null
  if (Has $o 'Defender') {
    $d = $o.Defender
    if (-not (IsObj $d)) { Bad 'Defender must be an object' }
    else {
      foreach ($n in 'ScanScheduleDay', 'ExclusionPath', 'RealTimeProtectionEnabled', 'IsTamperProtected') { if (-not (Has $d $n)) { Bad "Defender is missing '$n'" } }
      if ((Has $d 'ScanScheduleDay') -and -not ((IsInt $d.ScanScheduleDay) -and $d.ScanScheduleDay -ge 0 -and $d.ScanScheduleDay -le 8)) { Bad "Defender.ScanScheduleDay '$($d.ScanScheduleDay)' must be an integer 0-8" }
      if (Has $d 'ExclusionPath') { if (IsTextList $d.ExclusionPath) { $origExcl = @($d.ExclusionPath) } else { Bad 'Defender.ExclusionPath must be an array of non-empty strings' } }
      foreach ($n in 'RealTimeProtectionEnabled', 'IsTamperProtected') { if ((Has $d $n) -and -not (IsBool $d.$n)) { Bad "Defender.$n must be true/false" } }
    }
  }
  if (Has $o 'ExclusionsAddedByScript') {
    if (-not (IsTextList $o.ExclusionsAddedByScript)) { Bad 'ExclusionsAddedByScript must be an array of non-empty strings' }
    elseif ($null -ne $origExcl) { foreach ($a in @($o.ExclusionsAddedByScript)) { if ($origExcl -contains $a) { Bad "ExclusionsAddedByScript lists '$a', which was already excluded originally" } } }
  }
  @($p)
}

function Get-BytesHash([byte[]]$bytes) { $sha = [System.Security.Cryptography.SHA256]::Create(); try { [BitConverter]::ToString($sha.ComputeHash($bytes)) } finally { $sha.Dispose() } }

# Confirms a file is exactly the bytes we wrote AND parses AND passes the full validator, reading it the way restore does.
# Returns a list of problems (empty = confirmed).
function Confirm-OriginalsFile([string]$path, [string]$expectedHash, [string]$what) {
  if (-not (Test-Path -LiteralPath $path)) { "$what $path does not exist"; return }
  try { $bytes = [System.IO.File]::ReadAllBytes($path) } catch { "$what could not be read: $($_.Exception.Message)"; return }
  if ((Get-BytesHash $bytes) -ne $expectedHash) { "$what content differs from what was written"; return }
  try { $o = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json } catch { "$what does not parse: $($_.Exception.Message)"; return }
  foreach ($m in @(Test-OriginalsValid $o)) { "$what is invalid: $m" }
}

# Writes the originals file: exclusive temp file in the SAME directory -> confirm -> no-overwrite rename -> confirm the destination again.
# Rules: a pre-existing final or temp file is never overwritten, replaced, renamed or deleted (the call just refuses). Correctness does NOT
# depend on the rename being atomic (that is not established for the VirtualBox shared-folder provider): whatever the rename reports,
# the destination is inspected and accepted only if it is byte-identical to what was written and passes the full validator.
# A destination that cannot be confirmed is left exactly as it is (it is never deleted automatically), and the call throws, so the caller
# cannot go on to change anything. Returns ONE object with Confirmed = $true only after the final file is confirmed.
function Write-OriginalsValidated($state, [string]$finalPath,
    [scriptblock]$Serializer = { param($s) $s | ConvertTo-Json -Depth 6 },
    [scriptblock]$Mover = { param($src, $dst) [System.IO.File]::Move($src, $dst) }) {   # File.Move: no overwrite (throws if dst exists)
  $tmp = "$finalPath.tmp"
  if (Test-Path -LiteralPath $finalPath) { throw "$finalPath already exists; refusing to replace it" }
  if (Test-Path -LiteralPath $tmp) { throw "$tmp already exists (left over from an earlier run?); inspect it and remove it yourself, then run again" }
  $createdTmp = $false; $moveAttempted = $false
  try {
    $text = [string](& $Serializer $state)
    $bytes = [byte[]]((New-Object System.Text.UTF8Encoding($true)).GetPreamble() + (New-Object System.Text.UTF8Encoding($false)).GetBytes($text))
    $hash = Get-BytesHash $bytes
    $fs = New-Object System.IO.FileStream($tmp, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
    $createdTmp = $true
    try { $fs.Write($bytes, 0, $bytes.Length); $fs.Flush($true) } finally { $fs.Dispose() }
    $before = @(Confirm-OriginalsFile $tmp $hash 'temporary originals file')
    if ($before.Count) { throw ($before -join '; ') }
    if (Test-Path -LiteralPath $finalPath) { throw "$finalPath appeared while the temporary file was being written; refusing to touch it" }
    $moveAttempted = $true
    $moveError = $null
    try { & $Mover $tmp $finalPath | Out-Null } catch { $moveError = $_.Exception.Message }
    $after = @(Confirm-OriginalsFile $finalPath $hash 'final originals file')       # inspect the destination whatever the rename said
    if ($after.Count) { throw "rename $(if ($moveError) { "reported '$moveError' and " })did not leave a confirmed final file ($($after -join '; '))" }
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }       # our own temp file (we created it above)
    [pscustomobject]@{ Confirmed = $true; Path = $finalPath; RenameReportedError = $moveError }
  } catch {
    $msg = $_.Exception.Message
    if ($createdTmp -and (Test-Path -LiteralPath $tmp)) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    if ($moveAttempted -and (Test-Path -LiteralPath $finalPath)) { $msg += " A file at $finalPath was left exactly as found (it is never deleted or renamed automatically); inspect it before running again." }
    throw "could not write validated originals: $msg"
  }
}

# Verification wrapper: any exception while reading live state (registry, services, Defender) becomes a VERIFY ERROR with exit code 1.
function Invoke-Verify([scriptblock]$readLive, $tamperOriginal, $wanted, [string]$origPath) {
  try {
    $live = & $readLive
    Test-PrepResult $live $tamperOriginal $wanted
  } catch {
    [pscustomobject]@{
      Ok = $false; ExitCode = 1
      Lines = @("VERIFY ERROR: could not read back the machine state ($($_.Exception.Message)). The changes were applied but are UNVERIFIED. Originals are kept in $origPath; run restore-admin.ps1.")
    }
  }
}

if ($LibraryOnly) { return }

# ---- main ----
$out = $null
foreach ($p in 'X:\', '\\VBoxSvr\out\') { if (Test-Path $p) { $out = $p; break } }
if (-not $out) { Write-Host 'Neither X:\ nor \\VBoxSvr\out\ is reachable; nothing was changed.'; exit 1 }
$log  = Join-Path $out 'prep-admin.log'
$orig = Join-Path $out 'prep-admin.orig.json'
function L($m) { $line = "$(Get-Date -Format o)  $m"; Write-Host $line; Add-Content -Path $log -Value $line }
if (Test-Path $orig) { L "REFUSED: $orig already exists. Run restore-admin.ps1 and verify it, then move that JSON and the log aside before preparing again. Nothing was changed."; exit 1 }
$id = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $id.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Write-Host 'Run this from an elevated PowerShell. Nothing was changed.'; exit 1 }
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
L "build $($cv.CurrentBuildNumber).$($cv.UBR); output folder $out"

# ---- Phases 0 and 1 are READ-ONLY apart from writing the originals file; any error here exits 1 with nothing changed ----
try {
# ---- Phase 0: PREFLIGHT (any refusal leaves the machine and the output folder's JSON untouched) ----
$probe = foreach ($s in 'WSearch', 'SysMain') {
  $c = Get-CimInstance Win32_Service -Filter "Name='$s'" -ErrorAction SilentlyContinue
  [pscustomobject]@{ Name = $s; Exists = [bool]$c; StartMode = $c.StartMode; State = $c.State }
}
$problems = @(Test-ServicePreflight $probe)
if ($problems.Count) { foreach ($m in $problems) { L "REFUSED: $m" }; L 'Nothing was changed (no JSON written).'; exit 1 }
$mp = Get-MpPreference
$st = Get-MpComputerStatus
if (-not $st.RealTimeProtectionEnabled) { L 'REFUSED: Defender real-time protection is already disabled. Nothing was changed (no JSON written). Enable it, then run again.'; exit 1 }
if (-not $st.IsTamperProtected) { L 'WARNING: Tamper Protection is not enabled on this machine (recorded as the original; preparation neither enables nor disables it).' }

# ---- Phase 1: READ AND RECORD every original value (no changes yet) ----
$ld = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\explorer.exe'
$ldOrig = [ordered]@{ KeyExists = (Test-Path $ld); Values = [ordered]@{} }
if ($ldOrig.KeyExists) {
  $k = Get-Item $ld
  foreach ($n in $k.GetValueNames()) { $ldOrig.Values[$n] = @{ Kind = $k.GetValueKind($n).ToString(); Data = $k.GetValue($n, $null, 'DoNotExpandEnvironmentNames') } }
}
$svcOrig = [ordered]@{}
foreach ($pr in $probe) {
  $s = $pr.Name
  $sk = Get-Item "HKLM:\SYSTEM\CurrentControlSet\Services\$s"
  $delayed = if ($sk.GetValueNames() -contains 'DelayedAutostart') { [ordered]@{ Exists = $true; Kind = $sk.GetValueKind('DelayedAutostart').ToString(); Data = $sk.GetValue('DelayedAutostart') } } else { [ordered]@{ Exists = $false; Kind = $null; Data = $null } }
  $svcOrig[$s] = [ordered]@{ StartMode = $pr.StartMode; State = $pr.State; DelayedAutostart = $delayed }
}
function TimeText($t) { if ($t -is [datetime]) { $t.TimeOfDay.ToString() } else { "$t" } }
$mpOrig = [ordered]@{
  ScanScheduleDay = [int]$mp.ScanScheduleDay; ScanScheduleTime = (TimeText $mp.ScanScheduleTime); ScanScheduleQuickScanTime = (TimeText $mp.ScanScheduleQuickScanTime)
  ScanParameters = [int]$mp.ScanParameters; RandomizeScheduleTaskTimes = $mp.RandomizeScheduleTaskTimes
  DisableCatchupFullScan = $mp.DisableCatchupFullScan; DisableCatchupQuickScan = $mp.DisableCatchupQuickScan
  ExclusionPath = @($mp.ExclusionPath | Where-Object { $_ }); ExclusionProcess = @($mp.ExclusionProcess | Where-Object { $_ }); ExclusionExtension = @($mp.ExclusionExtension | Where-Object { $_ })
  RealTimeProtectionEnabled = $st.RealTimeProtectionEnabled; IsTamperProtected = $st.IsTamperProtected
}
$wanted = @('C:\T325')
$toAdd  = @($wanted | Where-Object { $mpOrig.ExclusionPath -notcontains $_ })
$state = [ordered]@{
  Build = "$($cv.CurrentBuildNumber).$($cv.UBR)"; Recorded = (Get-Date -Format o)
  LocalDumpsExplorer = $ldOrig; DumpsFolderExisted = (Test-Path 'C:\dumps')
  Services = $svcOrig; Defender = $mpOrig; ExclusionsAddedByScript = $toAdd
}
$w = @(Write-OriginalsValidated $state $orig)    # exclusive temp file -> confirm -> no-overwrite rename -> confirm destination; throws (caught below) before ANY change is made
if ($w.Count -ne 1 -or $w[0].Confirmed -ne $true) { throw 'the originals file was not confirmed; refusing to change anything' }
if ($w[0].RenameReportedError) { L "NOTE: the rename reported '$($w[0].RenameReportedError)', but the destination was inspected and is byte-identical and valid." }
L "ORIGINAL state written and CONFIRMED valid: $orig"
L ("ORIGINAL LocalDumps\explorer.exe exists=$($ldOrig.KeyExists) values=" + (($ldOrig.Values.Keys | ForEach-Object { "$_=$($ldOrig.Values[$_].Data)" }) -join ';'))
foreach ($s in $svcOrig.Keys) { L "ORIGINAL service $s StartMode=$($svcOrig[$s].StartMode) State=$($svcOrig[$s].State) DelayedAutostart=$(if ($svcOrig[$s].DelayedAutostart.Exists) { "$($svcOrig[$s].DelayedAutostart.Kind) $($svcOrig[$s].DelayedAutostart.Data)" } else { 'absent' })" }
L "ORIGINAL Defender ScanScheduleDay=$($mpOrig.ScanScheduleDay) ScanScheduleTime=$($mpOrig.ScanScheduleTime) QuickScanTime=$($mpOrig.ScanScheduleQuickScanTime) ScanParameters=$($mpOrig.ScanParameters) RandomizeScheduleTaskTimes=$($mpOrig.RandomizeScheduleTaskTimes) CatchupFullDisabled=$($mpOrig.DisableCatchupFullScan) CatchupQuickDisabled=$($mpOrig.DisableCatchupQuickScan)"
L "ORIGINAL Defender ExclusionPath=$($mpOrig.ExclusionPath -join ';') ExclusionProcess=$($mpOrig.ExclusionProcess -join ';') ExclusionExtension=$($mpOrig.ExclusionExtension -join ';') RealTime=$($mpOrig.RealTimeProtectionEnabled) TamperProtected=$($mpOrig.IsTamperProtected)"
L "ORIGINAL C:\dumps existed=$($state.DumpsFolderExisted)"
} catch {
  L "ERROR before any change was made: $($_.Exception.Message). Nothing was changed."
  exit 1
}

# ---- Phase 2: CHANGES (an error here is logged and exits 1; the originals JSON stays so restore-admin.ps1 can undo what happened) ----
try {
  # 1. Crash capture: FULL dumps (DumpType=2) of explorer.exe to C:\dumps, keep only 3 (host disk headroom)
  New-Item -Path $ld -Force | Out-Null
  New-ItemProperty $ld -Name DumpType   -PropertyType DWord        -Value 2          -Force | Out-Null
  New-ItemProperty $ld -Name DumpCount  -PropertyType DWord        -Value 3          -Force | Out-Null
  New-ItemProperty $ld -Name DumpFolder -PropertyType ExpandString -Value 'C:\dumps' -Force | Out-Null
  if (-not (Test-Path 'C:\dumps')) { New-Item -ItemType Directory 'C:\dumps' | Out-Null }
  L 'CHANGED LocalDumps\explorer.exe DumpType=2 (full) DumpCount=3 DumpFolder=C:\dumps'

  # 2. Quiet configuration: Search indexing and SysMain stopped and disabled for the session
  foreach ($s in 'WSearch', 'SysMain') {
    Stop-Service $s -Force -ErrorAction SilentlyContinue
    Set-Service $s -StartupType Disabled
    L "CHANGED service $s stopped, StartupType=Disabled"
  }

  # 3. Defender: no scheduled scan during the session; one exclusion (the test folder). Real-time protection stays ON.
  Set-MpPreference -ScanScheduleDay 8   # 8 = never
  if ($toAdd.Count) { Add-MpPreference -ExclusionPath $toAdd }
  L "CHANGED Defender ScanScheduleDay=8 (never); ExclusionPath added: $($toAdd -join ';') (already present, left alone: $((@($wanted | Where-Object { $toAdd -notcontains $_ })) -join ';'))"
} catch {
  L "ERROR during changes: $($_.Exception.Message). Originals are kept in $orig; run restore-admin.ps1."
  exit 1
}

# ---- Phase 3: VERIFY ----
$readLive = {
  $live = @{
    Dump = (Get-ItemProperty $ld)
    Services = @{}
    Day = [int](Get-MpPreference).ScanScheduleDay
    Exclusions = @((Get-MpPreference).ExclusionPath | Where-Object { $_ })
  }
  foreach ($s in 'WSearch', 'SysMain') { $c = Get-CimInstance Win32_Service -Filter "Name='$s'"; $live.Services[$s] = @{ StartMode = $c.StartMode; State = $c.State } }
  $s2 = Get-MpComputerStatus; $live.RealTime = [bool]$s2.RealTimeProtectionEnabled; $live.Tamper = [bool]$s2.IsTamperProtected
  $live
}
$res = Invoke-Verify $readLive ([bool]$mpOrig.IsTamperProtected) $wanted $orig
foreach ($line in $res.Lines) { L $line }
L ($(if ($res.Ok) { 'done: all checks passed' } else { "done WITH FAILURES: see VERIFY FAIL lines; originals kept in $orig; restore-admin.ps1 is safe to run" }))
exit $res.ExitCode
