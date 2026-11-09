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
rem Иконка EXE-файла (если OktellWatch.ico рядом — вшиваем)
set ICON=
if exist OktellWatch.ico set ICON=/win32icon:OktellWatch.ico

rem UI Automation (ревизия 2: btn:true видит и HTML/Chromium-кнопки).
rem Сначала ищем сборки рядом с компилятором, затем — в GAC (.NET 4.x).
set UIAC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\UIAutomationClient.dll
if not exist "%UIAC%" set UIAC=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll
set UIAT=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\UIAutomationTypes.dll
if not exist "%UIAT%" set UIAT=%WINDIR%\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll
if not exist "%UIAC%" (
  echo [ERROR] UIAutomationClient.dll not found - .NET Framework 4.x required.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ %ICON% ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll ^
  /r:"%UIAC%" /r:"%UIAT%" ^
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
