@echo off
cd /d "%~dp0"
set AVALONIA_TELEMETRY_OPTOUT=1
dotnet run --project src\StWorkbench.App -c Release
if errorlevel 1 pause
