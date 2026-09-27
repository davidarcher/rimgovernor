@echo off
rem Double-click to play: builds the launcher (seconds when nothing changed)
rem and opens it. The launcher keeps everything else up to date.
rem Already open: a running exe cannot be rebuilt, and one window is enough.
tasklist /fi "imagename eq RimGovernorLauncher.exe" | find /i "RimGovernorLauncher.exe" >nul && exit /b 0
cd /d "%~dp0go" || goto :fail
set GOTOOLCHAIN=go1.27.1
set CGO_ENABLED=0
go build -ldflags -H=windowsgui -o ..\RimGovernorLauncher.exe ./cmd/launcher || goto :fail
start "" "%~dp0RimGovernorLauncher.exe"
exit /b 0

:fail
echo.
echo Could not build the RimGovernor launcher. Go 1.27 or newer must be installed:
echo https://go.dev/dl/
pause
exit /b 1
