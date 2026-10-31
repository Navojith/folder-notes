@echo off
rem Builds FnS3InfoTip.dll (x64, static CRT). Needs the VS "Desktop development with C++" workload.
setlocal
set "VSWHERE=C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq tokens=*" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (echo MSVC toolset not found & exit /b 1)
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
cd /d "%~dp0"
if not exist out mkdir out
cl /nologo /utf-8 /std:c++20 /W4 /WX /O2 /MT /EHsc /DUNICODE /D_UNICODE /LD InfoTip.cpp /Foout\ /Feout\FnS3InfoTip.dll /link /DEF:InfoTip.def ole32.lib shell32.lib advapi32.lib shlwapi.lib || exit /b 1
echo Built out\FnS3InfoTip.dll
