@echo off
rem ============================================================
rem  Oktell Watch build script
rem  Compiles OktellWatch.exe using the C# compiler (csc.exe)
rem  built into Windows (.NET Framework 4.x) - nothing to install.
rem ============================================================
setlocal
cd /d "%~dp0"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo [ERROR] csc.exe not found. .NET Framework 4.x is required.
  echo         Install .NET Framework 4.8 or run this on Windows 10/11.
  pause
  exit /b 1
)

echo Building OktellWatch.exe ...
"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /out:OktellWatch.exe OktellWatch.cs

if errorlevel 1 (
  echo.
  echo [ERROR] Build failed. Make sure OktellWatch.cs is saved as UTF-8 with BOM.
  pause
  exit /b 1
)

echo.
echo [OK] Done: OktellWatch.exe
echo      Run OktellWatch.exe - the icon appears near the clock.
pause
