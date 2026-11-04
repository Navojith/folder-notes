# Read-only guest fact sweep (T3.2.5 VM preparation). Changes nothing.
$cv = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
"Build: $($cv.CurrentBuildNumber).$($cv.UBR)  Edition: $($cv.EditionID)  DisplayVersion: $($cv.DisplayVersion)"
foreach ($f in 'explorer.exe','System32\shell32.dll','System32\ExplorerFrame.dll','System32\windows.storage.dll') {
  $p = Join-Path $env:windir $f; "{0,-30} {1}" -f $f, (Get-Item $p).VersionInfo.FileVersionRaw }
$sys = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
"UAC EnableLUA=$($sys.EnableLUA) ConsentPromptBehaviorAdmin=$($sys.ConsentPromptBehaviorAdmin)"
"Token: " + ((whoami /groups | Select-String 'Mandatory Level|S-1-5-32-544') -join ' | ')
"SMBIOS: " + ((Get-CimInstance Win32_ComputerSystem | % { "$($_.Manufacturer) / $($_.Model)" }))
"RAM GB: " + [math]::Round((Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory/1GB,1)
Get-PSDrive -PSProvider FileSystem | % { "Drive {0}: free {1:N1} GB used {2:N1} GB root {3}" -f $_.Name, ($_.Free/1GB), ($_.Used/1GB), $_.DisplayRoot }
"Shares: " + ((net use) -join ' / ')
foreach ($s in 'wuauserv','UsoSvc','WaaSMedicSvc','WSearch','SysMain','WinDefend','WerSvc') {
  $x = Get-CimInstance Win32_Service -Filter "Name='$s'"; "Svc {0,-13} {1,-8} {2}" -f $s, $x.State, $x.StartMode }
try { $m = Get-MpComputerStatus; "Defender RTP=$($m.RealTimeProtectionEnabled) AV=$($m.AntivirusEnabled) TamperProt=$($m.IsTamperProtected)" } catch { "Defender: $($_.Exception.Message)" }
try { $mp = Get-MpPreference; "Defender ScanScheduleDay=$($mp.ScanScheduleDay) DisableRealtime=$($mp.DisableRealtimeMonitoring) Exclusions=$($mp.ExclusionPath -join ';')" } catch {}
"WU policy: " + ((Get-ItemProperty 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU' -ErrorAction SilentlyContinue | Out-String).Trim())
"WU UX pause: " + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings' -ErrorAction SilentlyContinue | Select Pause* | Out-String).Trim())
"LocalDumps explorer: " + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\explorer.exe' -ErrorAction SilentlyContinue | Out-String).Trim())
"LogPixels (HKCU): " + (Get-ItemProperty 'HKCU:\Control Panel\Desktop' -Name LogPixels -ErrorAction SilentlyContinue).LogPixels
Add-Type -AssemblyName System.Windows.Forms; "Screen: " + ([System.Windows.Forms.Screen]::PrimaryScreen.Bounds)
"Startup (Run HKCU): " + ((Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue | Select * -Exclude PS* | Out-String).Trim())
"Startup (Run HKLM): " + ((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue | Select * -Exclude PS* | Out-String).Trim())
"Explorer procs: " + ((Get-CimInstance Win32_Process -Filter "Name='explorer.exe'" | % { "$($_.ProcessId) $($_.CommandLine)" }) -join ' ; ')
"Time: " + (Get-Date -Format o) + "  Uptime min: " + [math]::Round(((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime).TotalMinutes,1)
