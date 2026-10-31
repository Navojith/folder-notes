@echo off
rem Builds src\FolderNotes.ShellExt with the MSVC toolset + CMake/Ninja bundled with Visual Studio.
rem Usage: eng\build-shellext.cmd [Release|Debug] [x64|arm64]
setlocal
set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"
set "ARCH=%~2"
if "%ARCH%"=="" set "ARCH=x64"

set "VSWHERE=C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq tokens=*" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (echo error: MSVC toolset not found. Install the "Desktop development with C++" workload. & exit /b 1)

if /i "%ARCH%"=="arm64" (set "VCARCH=x64_arm64") else (set "VCARCH=x64")
call "%VS%\VC\Auxiliary\Build\vcvarsall.bat" %VCARCH% >nul || exit /b 1

set "SRC=%~dp0..\src\FolderNotes.ShellExt"
set "OUT=%~dp0..\artifacts\shellext\%ARCH%-%CONFIG%"
cmake -S "%SRC%" -B "%OUT%" -G Ninja -DCMAKE_BUILD_TYPE=%CONFIG% || exit /b 1
cmake --build "%OUT%" || exit /b 1
echo Built %OUT%\FolderNotes.ShellExt.dll
