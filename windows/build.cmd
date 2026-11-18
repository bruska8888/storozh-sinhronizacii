@echo off
setlocal
cd /d "%~dp0"
set "FRAMEWORK=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
set "CSC=%FRAMEWORK%\csc.exe"
if not exist "%CSC%" (
  echo ERROR: .NET Framework 4.x 64-bit compiler not found: "%CSC%"
  exit /b 1
)
for %%A in (UIAutomationClient UIAutomationTypes WindowsBase) do (
  if not exist "%FRAMEWORK%\WPF\%%A.dll" (
    echo ERROR: Missing "%FRAMEWORK%\WPF\%%A.dll"
    exit /b 1
  )
)
"%CSC%" /nologo /langversion:5 /target:winexe /platform:x64 /optimize+ /codepage:65001 /win32icon:"icon\SyncWatchdog.ico" /out:"SyncWatchdog.exe" /r:"%FRAMEWORK%\System.dll" /r:"%FRAMEWORK%\System.Core.dll" /r:"%FRAMEWORK%\System.Xml.dll" /r:"%FRAMEWORK%\System.Web.Extensions.dll" /r:"%FRAMEWORK%\System.Windows.Forms.dll" /r:"%FRAMEWORK%\System.Drawing.dll" /r:"%FRAMEWORK%\WPF\UIAutomationClient.dll" /r:"%FRAMEWORK%\WPF\UIAutomationTypes.dll" /r:"%FRAMEWORK%\WPF\WindowsBase.dll" "src\SyncWatchdog.cs"
if errorlevel 1 exit /b 1
echo Built: "%CD%\SyncWatchdog.exe"
exit /b 0
