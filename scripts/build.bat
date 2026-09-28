@echo off
rem One-click: compile TrayApp.cs with the csc.exe shipped with .NET Framework 4.8, then launch it.
cd /d %~dp0..
rem kill the running instance first, otherwise the output exe is locked and compile fails
rem also kill orphaned mediamtx so the new app instance owns the ports cleanly
taskkill /F /IM DJI-RTMP-OBS.exe >nul 2>&1
taskkill /F /IM TrayApp.exe >nul 2>&1
taskkill /F /IM mediamtx.exe >nul 2>&1
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /platform:x64 /codepage:65001 /out:DJI-RTMP-OBS.exe src\TrayApp.cs /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll /resource:config\mediamtx.yml,mediamtx.yml
if errorlevel 1 (echo BUILD FAILED & pause & exit /b 1)
echo BUILD OK, launching...
start "" DJI-RTMP-OBS.exe
