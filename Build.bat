@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "CSC_EXE="

if exist "%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" (
    set "CSC_EXE=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
) else if exist "%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe" (
    set "CSC_EXE=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

if "%CSC_EXE%"=="" (
    echo ERROR: Could not find .NET Framework csc.exe.
    pause
    exit /b 1
)

for %%F in (
  appicon.ico
  Desktop_Dark.ico Desktop_Light.ico
  Laptop_Dark.ico Laptop_Light.ico
  Bolt_Dark.ico Bolt_Light.ico
  Bolt_active_Dark.ico Bolt_active_Light.ico
  Moon_Dark.ico Moon_Light.ico
  BalancedPower_Dark.ico BalancedPower_Light.ico
  SaveEnergy_Dark.ico SaveEnergy_Light.ico
) do (
  if not exist "%SCRIPT_DIR%%%F" (
    echo ERROR: %%F was not found in the build folder.
    echo Please place all existing SwitchPowerTray icons in this folder.
    pause
    exit /b 1
  )
)

if not exist "%SCRIPT_DIR%SwitchPowerTray.manifest" (
    echo ERROR: SwitchPowerTray.manifest was not found.
    pause
    exit /b 1
)

"%CSC_EXE%" ^
  /nologo /target:winexe /platform:anycpu /unsafe ^
  /win32icon:"%SCRIPT_DIR%appicon.ico" ^
  /win32manifest:"%SCRIPT_DIR%SwitchPowerTray.manifest" ^
  /out:"%SCRIPT_DIR%SwitchPowerTray-Test.exe" ^
  /r:System.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  /r:System.Security.dll /r:System.Xml.dll ^
  /resource:"%SCRIPT_DIR%Desktop_Dark.ico","Icon.Desktop.Dark.ico" ^
  /resource:"%SCRIPT_DIR%Desktop_Light.ico","Icon.Desktop.Light.ico" ^
  /resource:"%SCRIPT_DIR%Laptop_Dark.ico","Icon.Laptop.Dark.ico" ^
  /resource:"%SCRIPT_DIR%Laptop_Light.ico","Icon.Laptop.Light.ico" ^
  /resource:"%SCRIPT_DIR%Bolt_Dark.ico","Icon.Bolt.Dark.ico" ^
  /resource:"%SCRIPT_DIR%Bolt_Light.ico","Icon.Bolt.Light.ico" ^
  /resource:"%SCRIPT_DIR%Bolt_active_Dark.ico","Icon.BoltActive.Dark.ico" ^
  /resource:"%SCRIPT_DIR%Bolt_active_Light.ico","Icon.BoltActive.Light.ico" ^
  /resource:"%SCRIPT_DIR%Moon_Dark.ico","Icon.Moon.Dark.ico" ^
  /resource:"%SCRIPT_DIR%Moon_Light.ico","Icon.Moon.Light.ico" ^
  /resource:"%SCRIPT_DIR%BalancedPower_Dark.ico","Icon.Balanced.Dark.ico" ^
  /resource:"%SCRIPT_DIR%BalancedPower_Light.ico","Icon.Balanced.Light.ico" ^
  /resource:"%SCRIPT_DIR%SaveEnergy_Dark.ico","Icon.EnergySave.Dark.ico" ^
  /resource:"%SCRIPT_DIR%SaveEnergy_Light.ico","Icon.EnergySave.Light.ico" ^
  "%SCRIPT_DIR%SwitchPowerTray_pca_fixed_v12.cs"

if errorlevel 1 (
    echo.
    echo Build FAILED.
    pause
    exit /b 1
)

echo.
echo Build SUCCEEDED:
echo %SCRIPT_DIR%SwitchPowerTray-Test.exe
echo.
pause
endlocal
