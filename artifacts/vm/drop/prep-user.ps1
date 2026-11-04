# T3.2.5 VM preparation, non-elevated part (runs as 'tester'). Records every original value to Z:\..\out (X:).
$ErrorActionPreference = 'Stop'
$log = 'X:\prep-user.log'
function L($m) { $line = "$(Get-Date -Format o)  $m"; $line; Add-Content -Path $log -Value $line }
L "build $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber).$((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR)"

# 1. .NET 10 runtime, xcopy-deployed
if (-not (Test-Path 'C:\dotnet\dotnet.exe')) { Copy-Item 'Z:\dotnet' 'C:\dotnet' -Recurse; L 'copied Z:\dotnet -> C:\dotnet' } else { L 'C:\dotnet already present' }
L ("runtimes: " + ((& 'C:\dotnet\dotnet.exe' --list-runtimes) -join ' | '))

# 2. Dump folder (registry LocalDumps needs elevation: see prep-admin.ps1)
if (-not (Test-Path 'C:\dumps')) { New-Item -ItemType Directory 'C:\dumps' | Out-Null; L 'created C:\dumps' }

# 3. No startup apps: OneDrive autostart (HKCU Run). Original value recorded for restore.
$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$od = (Get-ItemProperty $run -Name OneDrive -ErrorAction SilentlyContinue).OneDrive
if ($od) { L "ORIGINAL HKCU Run\OneDrive = $od"; Remove-ItemProperty $run -Name OneDrive; L 'removed HKCU Run\OneDrive' } else { L 'HKCU Run\OneDrive absent' }
L 'done'
