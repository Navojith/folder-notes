# Tests for the decision logic of artifacts/vm/drop/prep-admin.ps1: service preflight and the post-change verdict/exit code.
# The script's -LibraryOnly mode defines the two functions and returns before it reads or changes anything, so this is safe on any machine.
# Not covered here: the real registry/service/Defender calls (first exercised by the elevated run in the VM).
#   Run:  powershell -NoProfile -File artifacts\vm\tests\prep-admin.tests.ps1      Exit code 0 = all pass.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\drop\prep-admin.ps1') -LibraryOnly

$script:pass = 0; $script:failed = @()
function Check([string]$name, $cond) { if ($cond) { $script:pass++ } else { $script:failed += $name; Write-Host "  FAIL: $name" -ForegroundColor Red } }
function Section($t) { Write-Host "== $t" }

function Svc($name, $mode, $state, $exists = $true) { [pscustomobject]@{ Name = $name; Exists = $exists; StartMode = $mode; State = $state } }
function Problems($a, $b) { @(Test-ServicePreflight @($a, $b)) }

# ---------------------------------------------------------------- preflight
Section 'preflight accepts every supported state/mode combination'
foreach ($mode in 'Auto', 'Manual', 'Disabled') {
  foreach ($state in 'Running', 'Stopped') {
    Check "accepts $mode/$state" ((Problems (Svc 'WSearch' $mode $state) (Svc 'SysMain' $mode $state)).Count -eq 0)
  }
}

Section 'preflight refuses a missing service'
$p = @(Problems (Svc 'WSearch' $null $null $false) (Svc 'SysMain' 'Auto' 'Running'))
Check 'one problem'                 ($p.Count -eq 1)
Check 'names WSearch, says missing' ($p[0] -like '*WSearch*does not exist*')
$p = @(Problems (Svc 'WSearch' 'Auto' 'Running') (Svc 'SysMain' $null $null $false))
Check 'missing SysMain is refused'  ($p.Count -eq 1 -and $p[0] -like '*SysMain*does not exist*')

Section 'preflight refuses transitional or unsupported states'
foreach ($state in 'Start Pending', 'Stop Pending', 'Paused', 'Pause Pending', 'Continue Pending', 'Unknown', $null, '') {
  $p = @(Problems (Svc 'WSearch' 'Auto' $state) (Svc 'SysMain' 'Auto' 'Running'))
  Check "refuses state '$state'" ($p.Count -eq 1 -and $p[0] -like '*WSearch*state*')
}

Section 'preflight refuses unsupported start modes'
foreach ($mode in 'Boot', 'System', 'Unknown', $null, '') {
  $p = @(Problems (Svc 'WSearch' 'Auto' 'Running') (Svc 'SysMain' $mode 'Running'))
  Check "refuses start mode '$mode'" ($p.Count -eq 1 -and $p[0] -like '*SysMain*start mode*')
}

Section 'preflight reports every problem, not just the first'
$p = @(Problems (Svc 'WSearch' 'Boot' 'Paused') (Svc 'SysMain' $null $null $false))
Check 'WSearch gives two problems, SysMain one' ($p.Count -eq 3)

# ---------------------------------------------------------------- verdict and exit code
function Live([hashtable]$over = @{}) {
  $l = @{
    Dump = [pscustomobject]@{ DumpType = 2; DumpCount = 3; DumpFolder = 'C:\dumps' }
    Services = @{ WSearch = @{ StartMode = 'Disabled'; State = 'Stopped' }; SysMain = @{ StartMode = 'Disabled'; State = 'Stopped' } }
    Day = 8; Exclusions = @('C:\Other', 'C:\T325'); RealTime = $true; Tamper = $true
  }
  foreach ($k in $over.Keys) { $l[$k] = $over[$k] }
  $l
}
$wanted = @('C:\T325')

Section 'everything as expected -> Ok, exit code 0, every line is a VERIFY ok'
$r = Test-PrepResult (Live) $true $wanted
Check 'Ok'                  ($r.Ok -eq $true)
Check 'exit code 0'         ($r.ExitCode -eq 0)
Check 'no VERIFY FAIL'      (@($r.Lines | Where-Object { $_ -like 'VERIFY FAIL*' }).Count -eq 0)
Check 'full dump reported'  (@($r.Lines | Where-Object { $_ -like '*DumpType=2 (full dump)*' }).Count -eq 1)
Check 'real-time reported'  (@($r.Lines | Where-Object { $_ -like 'VERIFY ok: real-time protection still enabled' }).Count -eq 1)

Section 'each single failure -> not Ok, exit code 1, a VERIFY FAIL line, other checks unaffected'
$cases = [ordered]@{
  'wrong DumpType'          = @{ Dump = [pscustomobject]@{ DumpType = 1; DumpCount = 3; DumpFolder = 'C:\dumps' } }
  'wrong DumpCount'         = @{ Dump = [pscustomobject]@{ DumpType = 2; DumpCount = 10; DumpFolder = 'C:\dumps' } }
  'wrong DumpFolder'        = @{ Dump = [pscustomobject]@{ DumpType = 2; DumpCount = 3; DumpFolder = 'C:\x' } }
  'WSearch still Running'   = @{ Services = @{ WSearch = @{ StartMode = 'Disabled'; State = 'Running' }; SysMain = @{ StartMode = 'Disabled'; State = 'Stopped' } } }
  'SysMain not Disabled'    = @{ Services = @{ WSearch = @{ StartMode = 'Disabled'; State = 'Stopped' }; SysMain = @{ StartMode = 'Manual'; State = 'Stopped' } } }
  'scan day not changed'    = @{ Day = 0 }
  'exclusion missing'       = @{ Exclusions = @('C:\Other') }
  'real-time protection off' = @{ RealTime = $false }
  'Tamper Protection changed' = @{ Tamper = $false }
}
foreach ($name in $cases.Keys) {
  $r = Test-PrepResult (Live $cases[$name]) $true $wanted
  Check "$name -> exit 1"           ($r.ExitCode -eq 1 -and $r.Ok -eq $false)
  Check "$name -> exactly one FAIL" (@($r.Lines | Where-Object { $_ -like 'VERIFY FAIL*' }).Count -eq 1)
}

Section 'several failures at once are all reported'
$r = Test-PrepResult (Live @{ Day = 0; RealTime = $false; Exclusions = @() }) $true $wanted
Check 'three FAIL lines, exit 1' (@($r.Lines | Where-Object { $_ -like 'VERIFY FAIL*' }).Count -eq 3 -and $r.ExitCode -eq 1)

Section 'Tamper Protection: only "unchanged" is required, so originally-off and still-off passes'
$r = Test-PrepResult (Live @{ Tamper = $false }) $false $wanted
Check 'exit 0 when it was off and stayed off' ($r.ExitCode -eq 0)
$r = Test-PrepResult (Live @{ Tamper = $true }) $false $wanted
Check 'exit 1 when it was off and is now on (changed)' ($r.ExitCode -eq 1)

Section 'the verdict function is repeatable (no state leaks between calls)'
$bad = Test-PrepResult (Live @{ Day = 0 }) $true $wanted
$good = Test-PrepResult (Live) $true $wanted
Check 'a failing call does not poison the next one' ($bad.ExitCode -eq 1 -and $good.ExitCode -eq 0)

# ---------------------------------------------------------------- originals: full validation contract
function GoodState {            # as prep would record it on a machine where nothing exists yet
  $da = [ordered]@{ Exists = $false; Kind = $null; Data = $null }
  [ordered]@{
    Build = '26300.9457'; Recorded = '2026-11-04T00:00:00'
    LocalDumpsExplorer = [ordered]@{ KeyExists = $false; Values = [ordered]@{} }; DumpsFolderExisted = $true
    Services = [ordered]@{ WSearch = [ordered]@{ StartMode = 'Auto'; State = 'Running'; DelayedAutostart = $da }; SysMain = [ordered]@{ StartMode = 'Auto'; State = 'Running'; DelayedAutostart = $da } }
    Defender = [ordered]@{ ScanScheduleDay = 0; ExclusionPath = @(); RealTimeProtectionEnabled = $true; IsTamperProtected = $true }
    ExclusionsAddedByScript = @('C:\T325')
  }
}
function RichState {            # every optional part populated
  $s = GoodState
  $s.LocalDumpsExplorer = [ordered]@{ KeyExists = $true; Values = [ordered]@{
    DumpType = [ordered]@{ Kind = 'DWord'; Data = 1 }; DumpCount = [ordered]@{ Kind = 'DWord'; Data = 10 }
    DumpFolder = [ordered]@{ Kind = 'ExpandString'; Data = '%LOCALAPPDATA%\CrashDumps' }; CustomDumpFlags = [ordered]@{ Kind = 'Binary'; Data = @(1, 2, 3) } } }
  $s.Services.WSearch.DelayedAutostart = [ordered]@{ Exists = $true; Kind = 'DWord'; Data = 1 }
  $s.Services.SysMain.DelayedAutostart = [ordered]@{ Exists = $true; Kind = 'DWord'; Data = 0 }
  $s.Services.SysMain.StartMode = 'Disabled'; $s.Services.SysMain.State = 'Stopped'
  $s.Defender.ExclusionPath = @('C:\Existing', 'D:\Other'); $s.Defender.ScanScheduleDay = 8
  $s
}
function RoundTrip($s) { ($s | ConvertTo-Json -Depth 6) | ConvertFrom-Json }
function Problems($s) { @(Test-OriginalsValid (RoundTrip $s)) }
function NewDir { $d = Join-Path ([IO.Path]::GetTempPath()) ("prep-tests-" + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory $d | Out-Null; $d }
function Threw([scriptblock]$b) { try { & $b | Out-Null; $false } catch { $true } }
function ThrowMsg([scriptblock]$b) { try { & $b | Out-Null; '' } catch { $_.Exception.Message } }
function FileText($p) { [IO.File]::ReadAllText($p) }

Section 'validator: realistic states are valid (empty and fully populated)'
Check 'GoodState valid'  ((Problems (GoodState)).Count -eq 0)
Check 'RichState valid'  ((Problems (RichState)).Count -eq 0)
$null = $null
Check 'null / array / string / number are not originals' ((@(Test-OriginalsValid $null).Count -ge 1) -and (@(Test-OriginalsValid @(1, 2)).Count -ge 1) -and (@(Test-OriginalsValid 'x').Count -ge 1) -and (@(Test-OriginalsValid 5).Count -ge 1))

Section 'validator: every required property is enforced (nested, one at a time)'
$removals = [ordered]@{
  'Build' = { param($s) $s.Remove('Build') }; 'Recorded' = { param($s) $s.Remove('Recorded') }
  'LocalDumpsExplorer' = { param($s) $s.Remove('LocalDumpsExplorer') }; 'DumpsFolderExisted' = { param($s) $s.Remove('DumpsFolderExisted') }
  'Services' = { param($s) $s.Remove('Services') }; 'Defender' = { param($s) $s.Remove('Defender') }; 'ExclusionsAddedByScript' = { param($s) $s.Remove('ExclusionsAddedByScript') }
  'LocalDumps.KeyExists' = { param($s) $s.LocalDumpsExplorer.Remove('KeyExists') }; 'LocalDumps.Values' = { param($s) $s.LocalDumpsExplorer.Remove('Values') }
  'Services.WSearch' = { param($s) $s.Services.Remove('WSearch') }; 'Services.SysMain' = { param($s) $s.Services.Remove('SysMain') }
  'WSearch.StartMode' = { param($s) $s.Services.WSearch.Remove('StartMode') }; 'WSearch.State' = { param($s) $s.Services.WSearch.Remove('State') }
  'WSearch.DelayedAutostart' = { param($s) $s.Services.WSearch.Remove('DelayedAutostart') }
  'Delayed.Exists' = { param($s) $s.Services.WSearch.DelayedAutostart.Remove('Exists') }; 'Delayed.Kind' = { param($s) $s.Services.SysMain.DelayedAutostart.Remove('Kind') }
  'Delayed.Data' = { param($s) $s.Services.SysMain.DelayedAutostart.Remove('Data') }
  'Defender.ScanScheduleDay' = { param($s) $s.Defender.Remove('ScanScheduleDay') }; 'Defender.ExclusionPath' = { param($s) $s.Defender.Remove('ExclusionPath') }
  'Defender.RealTimeProtectionEnabled' = { param($s) $s.Defender.Remove('RealTimeProtectionEnabled') }; 'Defender.IsTamperProtected' = { param($s) $s.Defender.Remove('IsTamperProtected') }
  'DumpType.Kind' = { param($s) $s.LocalDumpsExplorer.Values.DumpType.Remove('Kind') }; 'DumpFolder.Data' = { param($s) $s.LocalDumpsExplorer.Values.DumpFolder.Remove('Data') }
}
foreach ($n in $removals.Keys) { $s = RichState; & $removals[$n] $s; Check "missing $n is invalid" ((Problems $s).Count -ge 1) }

Section 'validator: wrong types and values are rejected'
$mut = [ordered]@{
  'Build empty' = { param($s) $s.Build = '' }; 'Build number' = { param($s) $s.Build = 26300 }
  'Recorded not a date' = { param($s) $s.Recorded = 'yesterday-ish' }; 'Recorded number' = { param($s) $s.Recorded = 5 }
  'DumpsFolderExisted string' = { param($s) $s.DumpsFolderExisted = 'yes' }
  'KeyExists string' = { param($s) $s.LocalDumpsExplorer.KeyExists = 'true' }; 'KeyExists number' = { param($s) $s.LocalDumpsExplorer.KeyExists = 1 }; 'KeyExists null' = { param($s) $s.LocalDumpsExplorer.KeyExists = $null }
  'Values is an array' = { param($s) $s.LocalDumpsExplorer.Values = @('x') }; 'Values is a string' = { param($s) $s.LocalDumpsExplorer.Values = 'x' }; 'Values null' = { param($s) $s.LocalDumpsExplorer.Values = $null }
  'KeyExists false but Values populated' = { param($s) $s.LocalDumpsExplorer.KeyExists = $false }
  'DumpType Kind unsupported (MultiString)' = { param($s) $s.LocalDumpsExplorer.Values.DumpType.Kind = 'MultiString' }
  'DumpType Kind null' = { param($s) $s.LocalDumpsExplorer.Values.DumpType.Kind = $null }
  'DumpType DWord with string data' = { param($s) $s.LocalDumpsExplorer.Values.DumpType.Data = 'two' }
  'DumpCount DWord with null data' = { param($s) $s.LocalDumpsExplorer.Values.DumpCount.Data = $null }
  'DumpFolder String with integer data' = { param($s) $s.LocalDumpsExplorer.Values.DumpFolder.Kind = 'String'; $s.LocalDumpsExplorer.Values.DumpFolder.Data = 7 }
  'DumpFolder not an object' = { param($s) $s.LocalDumpsExplorer.Values.DumpFolder = 'C:\dumps' }
  'State transitional' = { param($s) $s.Services.WSearch.State = 'Start Pending' }; 'State null' = { param($s) $s.Services.WSearch.State = $null }
  'StartMode Boot' = { param($s) $s.Services.SysMain.StartMode = 'Boot' }; 'StartMode null' = { param($s) $s.Services.SysMain.StartMode = $null }
  'Service not an object' = { param($s) $s.Services.WSearch = 'Running' }
  'Delayed Exists string' = { param($s) $s.Services.WSearch.DelayedAutostart.Exists = 'true' }
  'Delayed Exists true but Kind null' = { param($s) $s.Services.WSearch.DelayedAutostart.Kind = $null }
  'Delayed Exists true but Data null' = { param($s) $s.Services.WSearch.DelayedAutostart.Data = $null }
  'Delayed Kind String' = { param($s) $s.Services.WSearch.DelayedAutostart.Kind = 'String'; $s.Services.WSearch.DelayedAutostart.Data = '1' }
  'Delayed Data is a string' = { param($s) $s.Services.WSearch.DelayedAutostart.Data = '1' }
  'Delayed Data is a float' = { param($s) $s.Services.WSearch.DelayedAutostart.Data = 1.5 }
  'Delayed absent but Kind set' = { param($s) $s.Services.WSearch.DelayedAutostart = [ordered]@{ Exists = $false; Kind = 'DWord'; Data = $null } }
  'Delayed absent but Data set' = { param($s) $s.Services.WSearch.DelayedAutostart = [ordered]@{ Exists = $false; Kind = $null; Data = 0 } }
  'Delayed not an object' = { param($s) $s.Services.SysMain.DelayedAutostart = 1 }
  'ScanScheduleDay 9' = { param($s) $s.Defender.ScanScheduleDay = 9 }; 'ScanScheduleDay -1' = { param($s) $s.Defender.ScanScheduleDay = -1 }
  'ScanScheduleDay string' = { param($s) $s.Defender.ScanScheduleDay = '8' }; 'ScanScheduleDay null' = { param($s) $s.Defender.ScanScheduleDay = $null }
  'ExclusionPath is a string' = { param($s) $s.Defender.ExclusionPath = 'C:\x' }; 'ExclusionPath null' = { param($s) $s.Defender.ExclusionPath = $null }
  'ExclusionPath has an empty entry' = { param($s) $s.Defender.ExclusionPath = @('C:\a', '') }; 'ExclusionPath has a non-string' = { param($s) $s.Defender.ExclusionPath = @('C:\a', 5) }
  'RealTime string' = { param($s) $s.Defender.RealTimeProtectionEnabled = 'true' }; 'RealTime null' = { param($s) $s.Defender.RealTimeProtectionEnabled = $null }
  'Tamper number' = { param($s) $s.Defender.IsTamperProtected = 1 }; 'Tamper null' = { param($s) $s.Defender.IsTamperProtected = $null }
  'Added is a string' = { param($s) $s.ExclusionsAddedByScript = 'C:\T325' }; 'Added null' = { param($s) $s.ExclusionsAddedByScript = $null }
  'Added has an empty entry' = { param($s) $s.ExclusionsAddedByScript = @('') }; 'Added has a non-string' = { param($s) $s.ExclusionsAddedByScript = @(1) }
  'Added path was already excluded originally' = { param($s) $s.ExclusionsAddedByScript = @('c:\existing') }
}
foreach ($n in $mut.Keys) { $s = RichState; & $mut[$n] $s; Check "$n is invalid" ((Problems $s).Count -ge 1) }

Section 'validator: legitimate variations stay valid'
$s = RichState; $s.LocalDumpsExplorer.Values.DumpType = [ordered]@{ Kind = 'QWord'; Data = 2 };                       Check 'QWord DumpType' ((Problems $s).Count -eq 0)
$s = RichState; $s.LocalDumpsExplorer.Values.Remove('DumpCount');                                                        Check 'DumpCount absent while key exists' ((Problems $s).Count -eq 0)
$s = RichState; $s.LocalDumpsExplorer.Values = [ordered]@{};                                                             Check 'key exists with no values' ((Problems $s).Count -eq 0)
$s = RichState; $s.Services.WSearch.DelayedAutostart.Data = 0;                                                           Check 'Delayed zero (present, not absent)' ((Problems $s).Count -eq 0)
$s = RichState; $s.ExclusionsAddedByScript = @();                                                                        Check 'nothing added by prep' ((Problems $s).Count -eq 0)
$s = RichState; $s.Services.WSearch.StartMode = 'Disabled'; $s.Services.WSearch.State = 'Running';                       Check 'Running with Disabled startup' ((Problems $s).Count -eq 0)
$s = RichState; $s.Defender.RealTimeProtectionEnabled = $false; $s.Defender.IsTamperProtected = $false;                  Check 'protections off are still a valid RECORD (prep refuses on real-time separately)' ((Problems $s).Count -eq 0)
$s = RichState; $s.Recorded = (Get-Date -Format o);                                                                      Check 'real Get-Date -Format o stamp' ((Problems $s).Count -eq 0)

# ---------------------------------------------------------------- Write-OriginalsValidated
Section 'write: success returns exactly one confirmed result and leaves only the final file'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
$w = @(Write-OriginalsValidated (RichState) $final)
Check 'exactly one output object'     ($w.Count -eq 1)
Check 'Confirmed is true'             ($w[0].Confirmed -eq $true -and $w[0].Path -eq $final)
Check 'no rename error reported'      (-not $w[0].RenameReportedError)
Check 'final file valid and complete' ((@(Test-OriginalsValid (Get-Content $final -Raw | ConvertFrom-Json))).Count -eq 0)
Check 'content round-trips'           ((Get-Content $final -Raw | ConvertFrom-Json).Defender.ExclusionPath -contains 'D:\Other')
Check 'only the final file remains'   (@(Get-ChildItem $dir -Force).Count -eq 1)
Remove-Item $dir -Recurse -Force

Section 'write: a pre-existing final file is never touched'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'; [IO.File]::WriteAllText($final, 'PRECIOUS')
Check 'throws'                        (Threw { Write-OriginalsValidated (GoodState) $final })
Check 'existing file byte-for-byte untouched' ((FileText $final) -eq 'PRECIOUS')
Check 'no temp file created'          (-not (Test-Path "$final.tmp"))
Remove-Item $dir -Recurse -Force

Section 'write: a pre-existing temp file is never overwritten or deleted'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'; [IO.File]::WriteAllText("$final.tmp", 'STALE')
Check 'throws'                        (Threw { Write-OriginalsValidated (GoodState) $final })
Check 'stale temp untouched'          ((FileText "$final.tmp") -eq 'STALE')
Check 'no final file'                 (-not (Test-Path $final))
Remove-Item $dir -Recurse -Force

Section 'write: a final file that appears while the temp file is being written is left alone'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
$ser = { param($s) [IO.File]::WriteAllText($script:racePath, 'SOMEONE ELSE'); $s | ConvertTo-Json -Depth 6 }; $script:racePath = $final
Check 'throws'                        (Threw { Write-OriginalsValidated (GoodState) $final -Serializer $ser })
Check 'their file untouched'          ((FileText $final) -eq 'SOMEONE ELSE')
Check 'our temp file removed'         (-not (Test-Path "$final.tmp"))
Remove-Item $dir -Recurse -Force

Section 'write: invalid state / bad serializer / unwritable location -> throws, nothing left behind'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
$bad = GoodState; $bad.Services.WSearch.DelayedAutostart = [ordered]@{ Exists = $true; Kind = $null; Data = $null }
Check 'incomplete DelayedAutostart refused'  (Threw { Write-OriginalsValidated $bad $final })
Check 'nothing left (invalid)'               (@(Get-ChildItem $dir -Force).Count -eq 0)
Check 'garbage serializer refused'           (Threw { Write-OriginalsValidated (GoodState) $final -Serializer { param($s) '{ this is not json' } })
Check 'truncated JSON refused'               (Threw { Write-OriginalsValidated (GoodState) $final -Serializer { param($s) ($s | ConvertTo-Json -Depth 6).Substring(0, 40) } })
Check 'serializer exception refused'         (Threw { Write-OriginalsValidated (GoodState) $final -Serializer { param($s) throw 'serializer exploded' } })
Check 'nothing left (serializers)'           (@(Get-ChildItem $dir -Force).Count -eq 0)
Check 'unwritable location refused'          (Threw { Write-OriginalsValidated (GoodState) (Join-Path $dir 'no-such-subdir\x.json') })
Check 'nothing created'                      (@(Get-ChildItem $dir -Recurse -Force).Count -eq 0)
Remove-Item $dir -Recurse -Force

Section 'rename: reports failure but the destination is byte-identical and valid -> accepted (destination inspected, not assumed absent)'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
$mv = { param($src, $dst) Copy-Item -LiteralPath $src -Destination $dst; Remove-Item -LiteralPath $src -Force; throw 'rename reported an error after creating the destination' }
$w = @(Write-OriginalsValidated (RichState) $final -Mover $mv)
Check 'confirmed'                     ($w.Count -eq 1 -and $w[0].Confirmed -eq $true)
Check 'the reported rename error is passed on' ($w[0].RenameReportedError -like '*after creating the destination*')
Check 'final file valid'              ((@(Test-OriginalsValid (Get-Content $final -Raw | ConvertFrom-Json))).Count -eq 0)
Check 'no temp left'                  (-not (Test-Path "$final.tmp"))
Remove-Item $dir -Recurse -Force

Section 'rename: copy-style mover that creates the destination, throws, and leaves the temp file behind'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
$mv = { param($src, $dst) Copy-Item -LiteralPath $src -Destination $dst; throw 'copy-style failure' }
$w = @(Write-OriginalsValidated (GoodState) $final -Mover $mv)
Check 'confirmed'                     ($w.Count -eq 1 -and $w[0].Confirmed -eq $true)
Check 'our temp file cleaned up'      (-not (Test-Path "$final.tmp"))
Check 'only the final file remains'   (@(Get-ChildItem $dir -Force).Count -eq 1)
Remove-Item $dir -Recurse -Force

Section 'rename: creates a CORRUPT destination then throws -> refused, destination left exactly as found (never deleted)'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
$mv = { param($src, $dst) [IO.File]::WriteAllText($dst, '{ half a file'); throw 'disk full' }
$msg = ThrowMsg { Write-OriginalsValidated (GoodState) $final -Mover $mv }
Check 'throws (so prep cannot proceed)'  ($msg -ne '')
Check 'message names the rename error'   ($msg -like "*disk full*")
Check 'message says the file was left'   ($msg -like '*left exactly as found*' -and $msg -like "*$final*")
Check 'destination NOT deleted and unchanged' ((FileText $final) -eq '{ half a file')
Check 'our temp file removed'            (-not (Test-Path "$final.tmp"))
Remove-Item $dir -Recurse -Force

Section 'rename: creates a valid-but-DIFFERENT destination then throws -> refused, destination untouched'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
$other = (RichState | ConvertTo-Json -Depth 6)
$mv = { param($src, $dst) [IO.File]::WriteAllText($dst, $script:otherJson); throw 'weird' }; $script:otherJson = $other
Check 'throws'                           (Threw { Write-OriginalsValidated (GoodState) $final -Mover $mv })
Check 'destination untouched'            ((FileText $final) -eq $other)
Check 'our temp file removed'            (-not (Test-Path "$final.tmp"))
Remove-Item $dir -Recurse -Force

Section 'rename: reports SUCCESS but the destination is corrupt or truncated -> refused, left in place'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
$mv = { param($src, $dst) Move-Item -LiteralPath $src -Destination $dst; $b = [IO.File]::ReadAllBytes($dst); [IO.File]::WriteAllBytes($dst, $b[0..40]) }
Check 'truncated-after-success refused'  (Threw { Write-OriginalsValidated (GoodState) $final -Mover $mv })
Check 'truncated file left in place'     ((Test-Path $final) -and (Get-Item $final).Length -eq 41)
Remove-Item $dir -Recurse -Force

Section 'rename: reports SUCCESS but no destination exists -> refused, no final file'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
Check 'refused'                          (Threw { Write-OriginalsValidated (GoodState) $final -Mover { param($src, $dst) } })
Check 'no final file'                    (-not (Test-Path $final))
Check 'our temp file removed'            (-not (Test-Path "$final.tmp"))
Remove-Item $dir -Recurse -Force

Section 'rename: throws without creating anything -> refused, no final file, temp removed, cause reported'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
$msg = ThrowMsg { Write-OriginalsValidated (GoodState) $final -Mover { param($src, $dst) throw 'sharing violation' } }
Check 'throws with the cause'            ($msg -like '*sharing violation*')
Check 'no final file'                    (-not (Test-Path $final))
Check 'no left-in-place claim when there is no destination' ($msg -notlike '*left exactly as found*')
Check 'our temp file removed'            (-not (Test-Path "$final.tmp"))
Remove-Item $dir -Recurse -Force

Section 'rename: the default mover never overwrites (File.Move semantics on this runtime)'
$dir = NewDir; $a = Join-Path $dir 'a'; $b = Join-Path $dir 'b'; [IO.File]::WriteAllText($a, 'A'); [IO.File]::WriteAllText($b, 'B')
Check 'File.Move onto an existing file throws' (Threw { [System.IO.File]::Move($a, $b) })
Check 'destination content intact'       ((FileText $b) -eq 'B' -and (FileText $a) -eq 'A')
Remove-Item $dir -Recurse -Force

Section 'caller contract: a failed write can never look like success'
$dir = NewDir; $final = Join-Path $dir 'prep-admin.orig.json'
$res = $null; try { $res = @(Write-OriginalsValidated (GoodState) $final -Mover { param($s, $d) throw 'x' }) } catch { }
Check 'no result object is produced on failure' ($res -eq $null -or @($res).Count -eq 0)
Remove-Item $dir -Recurse -Force

# ---------------------------------------------------------------- guarded verification phase
Section 'Invoke-Verify: a throwing reader -> VERIFY ERROR, exit 1, restore instruction, originals path'
foreach ($msg in 'registry key not found', 'Get-MpPreference failed: access denied', 'Win32_Service query timed out') {
  $r = Invoke-Verify { throw $msg } $true $wanted 'X:\prep-admin.orig.json'
  Check "[$msg] exit 1"          ($r.ExitCode -eq 1 -and $r.Ok -eq $false)
  Check "[$msg] VERIFY ERROR"    ($r.Lines.Count -eq 1 -and $r.Lines[0] -like 'VERIFY ERROR:*')
  Check "[$msg] carries the cause" ($r.Lines[0] -like "*$msg*")
  Check "[$msg] says run restore"  ($r.Lines[0] -like '*run restore-admin.ps1*')
  Check "[$msg] names the originals" ($r.Lines[0] -like '*X:\prep-admin.orig.json*')
  Check "[$msg] says UNVERIFIED"   ($r.Lines[0] -like '*UNVERIFIED*')
}

Section 'Invoke-Verify: a real (non-terminating-turned-terminating) cmdlet error is also caught'
$r = Invoke-Verify { Get-ItemProperty 'HKLM:\SOFTWARE\no-such-key-for-prep-tests' -ErrorAction Stop } $true $wanted 'X:\o.json'
Check 'missing registry key -> VERIFY ERROR, exit 1' ($r.ExitCode -eq 1 -and $r.Lines[0] -like 'VERIFY ERROR:*')

Section 'Invoke-Verify: a healthy reader gives the normal verdict'
$r = Invoke-Verify { Live } $true $wanted 'X:\o.json'
Check 'exit 0 and Ok'          ($r.ExitCode -eq 0 -and $r.Ok)
Check 'no VERIFY ERROR'        (@($r.Lines | Where-Object { $_ -like 'VERIFY ERROR*' }).Count -eq 0)
$r = Invoke-Verify { Live @{ Day = 0 } } $true $wanted 'X:\o.json'
Check 'a failing check still exits 1 (as VERIFY FAIL, not VERIFY ERROR)' ($r.ExitCode -eq 1 -and @($r.Lines | Where-Object { $_ -like 'VERIFY FAIL*' }).Count -eq 1)
$r = Invoke-Verify { $null } $true $wanted 'X:\o.json'
Check 'a reader that returns nothing never yields exit 0' ($r.ExitCode -eq 1)

Write-Host ''
Write-Host "passed $script:pass, failed $($script:failed.Count)"
if ($script:failed.Count) { $script:failed | ForEach-Object { Write-Host "  - $_" }; exit 1 }
exit 0
