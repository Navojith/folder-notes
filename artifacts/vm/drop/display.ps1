# Read-only: physical resolution and DPI of the guest session.
Add-Type -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
[DllImport("user32.dll")] public static extern uint GetDpiForSystem();
'@ -Name U -Namespace W
[W.U]::SetProcessDPIAware() | Out-Null
Add-Type -AssemblyName System.Windows.Forms
"Physical: " + [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
"System DPI: " + [W.U]::GetDpiForSystem() + "  (120 = 125 %)"
"PerMonitorSettings: " + ((Get-ChildItem 'HKCU:\Control Panel\Desktop\PerMonitorSettings' -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName + ' DpiValue=' + (Get-ItemProperty $_.PSPath).DpiValue }) -join '; ')
