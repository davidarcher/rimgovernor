@echo off
rem RimGovernor.cmd
rem   no arguments:  double-click to play. Publishes the launcher when its sources changed (rg launcher-stale) and opens it.
rem   with arguments: runs the repo tool host (tools\rg), e.g.  RimGovernor.cmd issue 3086
rem                   tools: land, test, issue, affected, acceptance   (RimGovernor.cmd -help)
rem The tool host is published ReadyToRun into .rimgovernor\tools\<version>\ and .rimgovernor\tools\current names the
rem live version. A binary older than tools\rg\*.cs|csproj or the brain\ projects rg references reports itself stale (exit 75) before doing anything;
rem this shim then rebuilds under a lock (parallel invocations wait, never share a half-written output) and re-runs.
rem Needs the .NET SDK 10 on PATH only to (re)build. Arguments containing % or ^ are mangled by cmd; use -F for those.
if "%~1"=="" goto :launcher
setlocal EnableExtensions
set "ROOT=%~dp0"
set "TOOLS=%~dp0.rimgovernor\tools"
set "REBUILT="

:run
set "CUR="
if exist "%TOOLS%\current" set /p CUR=<"%TOOLS%\current"
if not defined CUR goto :build
if not exist "%TOOLS%\%CUR%\rg.exe" goto :build
"%TOOLS%\%CUR%\rg.exe" %*
if "%ERRORLEVEL%"=="75" goto :build
exit /b %ERRORLEVEL%

:build
if defined REBUILT (
    echo RimGovernor.cmd: the tool is still stale after a rebuild; check the clock and tools\rg. 1>&2
    exit /b 1
)
set "REBUILT=1"
if not exist "%TOOLS%" mkdir "%TOOLS%"
:lock
set "BUILDING="
rem Holding the lock file open for the duration of the call is the lock: a second opener fails and waits.
(9>"%TOOLS%\build.lock" call :buildlocked) 3>&2 2>nul
if not defined BUILDING (
    ping -n 2 127.0.0.1 >nul
    goto :lock
)
if defined BUILDFAIL exit /b 1
goto :run

:buildlocked
set "BUILDING=1"
set "BUILDFAIL="
set "CUR="
if exist "%TOOLS%\current" set /p CUR=<"%TOOLS%\current"
rem Another invocation may have rebuilt while this one waited for the lock.
if defined CUR if exist "%TOOLS%\%CUR%\rg.exe" (
    "%TOOLS%\%CUR%\rg.exe" --check-stale
    if not errorlevel 75 goto :eof
)
set "PREV=%CUR%"
:pick
set "VER=v%RANDOM%%RANDOM%"
if exist "%TOOLS%\%VER%" goto :pick
echo RimGovernor.cmd: building the tool host (tools\rg) ... 1>&3
dotnet publish "%ROOT%tools\rg\rg.csproj" -c Release -r win-x64 --self-contained false -p:PublishReadyToRun=true -p:NodeReuse=false --nologo -v q -o "%TOOLS%\%VER%" >"%TOOLS%\build.log" 2>&1
if errorlevel 1 (
    type "%TOOLS%\build.log" 1>&3
    echo RimGovernor.cmd: building the tool host failed. 1>&3
    set "BUILDFAIL=1"
    goto :eof
)
>"%TOOLS%\current.tmp" echo %VER%
move /y "%TOOLS%\current.tmp" "%TOOLS%\current" >nul
rem Running copies keep their own version directory; drop every one but the new and the previous (best effort).
for /d %%D in ("%TOOLS%\v*") do (
    if /i not "%%~nxD"=="%VER%" if /i not "%%~nxD"=="%PREV%" rd /s /q "%%D" 2>nul
)
goto :eof

:launcher
rem Already open: a running exe cannot be rebuilt, and one window is enough.
tasklist /fi "imagename eq RimGovernorLauncher.exe" | find /i "RimGovernorLauncher.exe" >nul && exit /b 0
setlocal EnableExtensions
set "LBUILD=%~dp0.rimgovernor\launcher-build"
rem The tool host decides whether the launcher's sources changed since the last publish (tools\rg\SelfBuild.cs, #3295).
rem Bringing the tool host up to date first means a 75 from launcher-stale can only mean the launcher is stale.
call "%~f0" --check-stale || goto :fail
set "CUR="
if exist "%~dp0.rimgovernor\tools\current" set /p CUR=<"%~dp0.rimgovernor\tools\current"
if not defined CUR goto :fail
"%~dp0.rimgovernor\tools\%CUR%\rg.exe" launcher-stale
if "%ERRORLEVEL%"=="0" goto :startlauncher
if not "%ERRORLEVEL%"=="75" goto :fail
rem Published single-file into a staging directory, then copied over the root exe (tools\launcher, #3098). The stamp
rem is written only after both succeed, so a failed publish stays stale.
dotnet publish "%~dp0tools\launcher\launcher.csproj" -c Release -r win-x64 --no-self-contained -p:NodeReuse=false --nologo -v q -o "%LBUILD%" || goto :fail
copy /y "%LBUILD%\RimGovernorLauncher.exe" "%~dp0RimGovernorLauncher.exe" >nul || goto :fail
>"%LBUILD%\current" echo %DATE% %TIME%
:startlauncher
start "" "%~dp0RimGovernorLauncher.exe"
exit /b 0

:fail
echo.
echo Could not build the RimGovernor launcher. The .NET SDK 10 must be installed:
echo https://dotnet.microsoft.com/download
pause
exit /b 1
