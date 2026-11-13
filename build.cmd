@echo off
rem ============================================================
rem  Pi Web Tray - build script
rem
rem  Build with either the .NET SDK 8.0+ or MSBuild from
rem  Build Tools for Visual Studio 2022. Both require the
rem  .NET Framework 4.8 Targeting Pack. An IDE is not required.
rem  The 8.0+ version is the SDK build tool, not the app target.
rem  The project targets .NET Framework 4.8, so the resulting exe
rem  runs on Windows 7 SP1 and newer (with the
rem  .NET Framework 4.8 runtime installed).
rem
rem  Usage:
rem    build.cmd              Release build, pauses at the end
rem    build.cmd --debug      Debug build
rem    build.cmd --no-pause   for CI / scripts
rem
rem  NOTE: keep this file ASCII-only. cmd.exe parses batch files in
rem  the console code page; UTF-8 Chinese text breaks the parser.
rem ============================================================
setlocal
cd /d "%~dp0"

set "CONFIG=Release"
set "NO_PAUSE="
set "EXITCODE=0"
for %%a in (%*) do (
    if /i "%%~a"=="--debug" set "CONFIG=Debug"
    if /i "%%~a"=="--no-pause" set "NO_PAUSE=1"
)

rem If the build fails because pi-web-tray.exe is running from this folder, exit the
rem tray application and run the script again. The build does not terminate processes.

set "APPBIN=src\bin\%CONFIG%\net48"
if exist "%APPBIN%" (
    for /d %%d in ("%APPBIN%\*") do (
        if exist "%%~fd\pi-web-tray.resources.dll" rmdir /s /q "%%~fd"
    )
)

rem A machine can have the .NET runtime without an SDK. Check for an SDK, not
rem merely the dotnet host; otherwise the MSBuild fallback below is unreachable.
dotnet --list-sdks 2>nul | findstr /r "." >nul
if not errorlevel 1 goto build_dotnet

set "MSBUILD="
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if exist "%VSWHERE%" for /f "usebackq delims=" %%i in (`"%VSWHERE%" -latest -products * -requires Microsoft.Component.MSBuild -find MSBuild\**\Bin\MSBuild.exe`) do set "MSBUILD=%%i"
if not defined MSBUILD goto no_toolchain

echo [i] dotnet not found, using MSBuild:
echo     %MSBUILD%
"%MSBUILD%" "PiWebTray.sln" /t:Restore,Build /p:Configuration=%CONFIG% /nologo /v:minimal
if errorlevel 1 goto failed
goto package

:build_dotnet
dotnet build "PiWebTray.sln" -c %CONFIG% --nologo -v:minimal --no-incremental
if errorlevel 1 goto failed
goto package

:package
if not exist "%APPBIN%\pi-web-tray.exe" goto failed
if not exist artifacts mkdir artifacts
copy /y "%APPBIN%\pi-web-tray.exe" "artifacts\pi-web-tray.exe" >nul
if errorlevel 1 goto failed
if exist "%APPBIN%\pi-web-tray.exe.config" (
    copy /y "%APPBIN%\pi-web-tray.exe.config" "artifacts\pi-web-tray.exe.config" >nul
    if errorlevel 1 goto failed
)

rem Remove stale culture directories from previous builds.
if exist "artifacts" (
    for /d %%d in ("artifacts\*") do (
        if exist "%%~fd\pi-web-tray.resources.dll" rmdir /s /q "%%~fd"
    )
)

rem Localized resources live in satellite assemblies (for example zh-Hans and zh-Hant).
rem Copy every culture directory that contains resources for this executable.
for /d %%d in ("%APPBIN%\*") do (
    if exist "%%~fd\pi-web-tray.resources.dll" (
        if not exist "artifacts\%%~nxd" mkdir "artifacts\%%~nxd"
        copy /y "%%~fd\pi-web-tray.resources.dll" "artifacts\%%~nxd\" >nul
        if errorlevel 1 goto failed
    )
)

echo.
echo [ok] built: %CD%\artifacts\pi-web-tray.exe
echo checks: artifacts\pi-web-tray.exe --check
echo tests : dotnet test tests\PiWebTray.Tests\PiWebTray.Tests.csproj -c %CONFIG%
goto done

:no_toolchain
echo [x] No build toolchain found.
echo     Install the .NET SDK 8.0+ with the .NET Framework 4.8 Targeting Pack,
echo     or Build Tools for Visual Studio 2022 and the Targeting Pack.
set "EXITCODE=1"
goto done

:failed
echo.
echo [x] build failed
set "EXITCODE=1"

:done
if defined NO_PAUSE exit /b %EXITCODE%
pause
exit /b %EXITCODE%
