@echo off
rem Compile TrayApp.cs with the csc.exe shipped with .NET Framework 4.8 (built into Windows).
cd /d %~dp0..
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /platform:x64 /codepage:65001 /out:TrayApp.exe src\TrayApp.cs /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll
if errorlevel 1 (echo BUILD FAILED & pause & exit /b 1)
echo BUILD OK: TrayApp.exe
