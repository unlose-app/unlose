@echo off
chcp 65001 >nul 2>&1
rem Force .NET CLI output to English on any system locale (the script's own messages are English;
rem without this, `dotnet build/publish` prints localized messages on non-English Windows).
set DOTNET_CLI_UI_LANGUAGE=en
setlocal EnableExtensions
cd /d "%~dp0"

echo.
echo ========================================
echo  Unlose: Build - Publish - MSI
echo ========================================
echo.
echo [0/6] Stopping processes that lock files + cleaning old build output...
echo Stopping Unlose-related processes (errors ignored if not running)...
sc stop unloseService >nul 2>&1
taskkill /IM unlose.Service.exe /F >nul 2>&1
taskkill /IM unlose.UI.exe /F >nul 2>&1
taskkill /IM unlose.exe /F >nul 2>&1
timeout /t 2 /nobreak >nul

if exist "publish" (
  rd /s /q "publish"
  echo Deleted the publish directory and all subdirectories.
) else (
  echo publish directory does not exist, skipped.
)

echo.
echo [1/6] Build Release...
dotnet build src/Unlose.sln -c Release
if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)

echo.
echo [2/6] Publish Service...
rem Self-contained single-file publish: no longer requires the .NET 8 runtime preinstalled on the target
rem (framework-dependent installs reliably failed with 1603 on a clean Win10 in testing)
dotnet publish src/Unlose.Service/Unlose.Service.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/service
if errorlevel 1 (
  echo Service publish failed.
  exit /b 1
)

echo.
echo [3/6] Publish UI...
dotnet publish src/Unlose.UI/Unlose.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/ui
if errorlevel 1 (
  echo UI publish failed.
  exit /b 1
)

echo.
echo [4/6] Publish CLI...
dotnet publish src/Unlose.Cli/Unlose.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/cli
if errorlevel 1 (
  echo CLI publish failed.
  exit /b 1
)

echo.
echo [5/6] Publish McpServer...
dotnet publish src/Unlose.McpServer/Unlose.McpServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish/mcpserver
if errorlevel 1 (
  echo McpServer publish failed.
  exit /b 1
)

if not exist "publish\service\unlose.Service.exe" goto publish_verify_fail
if not exist "publish\ui\unlose.UI.exe" goto publish_verify_fail
if not exist "publish\cli\unlose.exe" goto publish_verify_fail
if not exist "publish\mcpserver\unlose.McpServer.exe" goto publish_verify_fail
goto after_publish_verify
:publish_verify_fail
echo Publish output verification failed.
exit /b 1
:after_publish_verify

echo.
echo [6/6] WiX MSI...
rem Version source of truth: FileVersion in src/Directory.Build.props.
rem MSI ProductVersion uses only first 3 fields (4th ignored by Windows Installer).
rem Release must bump the 3rd field (Patch); details see comments in the props file.
for /f "delims=" %%V in ('powershell -NoProfile -Command "$v=([xml](Get-Content 'src/Directory.Build.props' -Raw)).Project.PropertyGroup.FileVersion; ($v -split '\.')[0..2] -join '.'"') do set "UNLOSE_VER=%%V"
if not defined UNLOSE_VER (
  echo Cannot read version from Directory.Build.props
  exit /b 1
)
echo Version: %UNLOSE_VER%
set "PATH=%USERPROFILE%\.dotnet\tools;%PATH%"
wix --version >nul 2>&1
if errorlevel 1 (
  echo Installing wix dotnet tool...
  dotnet tool install --global wix
  if errorlevel 1 (
    echo WiX tool install failed.
    exit /b 1
  )
)
set "WIXPKGVER="
set "CGWIXVER=%TEMP%\cg-wix-ver.txt"
wix --version >"%CGWIXVER%" 2>nul
if exist "%CGWIXVER%" for /f "usebackq tokens=3 delims= " %%A in ("%CGWIXVER%") do for /f "delims=+" %%B in ("%%A") do set "WIXPKGVER=%%B"
del "%CGWIXVER%" 2>nul
if not defined WIXPKGVER set "WIXPKGVER=6.0.2"
echo Registering WixToolset.Util.wixext/%WIXPKGVER%...
wix extension add -g WixToolset.Util.wixext/%WIXPKGVER%
if errorlevel 1 wix extension add -g WixToolset.Util.wixext/4.0.5
if errorlevel 1 (
  echo WiX extension install failed.
  exit /b 1
)

wix build src/Installer/Unlose.wxs ^
  -ext WixToolset.Util.wixext ^
  -d UnloseVersion=%UNLOSE_VER% ^
  -d Unlose.UI.TargetDir=publish/ui/ ^
  -d Unlose.Service.TargetDir=publish/service/ ^
  -d Unlose.Cli.TargetDir=publish/cli/ ^
  -d Unlose.McpServer.TargetDir=publish/mcpserver/ ^
  -d Unlose.SetupActions.CAPath=src/Unlose.SetupActions/bin/Release/net48/Unlose.SetupActions.CA.dll ^
  -arch x64 ^
  -o publish/unlose-setup-x64.msi
if errorlevel 1 (
  echo WiX build failed.
  exit /b 1
)

if not exist "publish\unlose-setup-x64.msi" (
  echo MSI file not created.
  exit /b 1
)

echo.
echo ========================================
echo  Done: publish\unlose-setup-x64.msi
echo ========================================
if /i "%UNLOSE_MSIBUILD_NOPAUSE%"=="1" goto skip_pause_after_success
pause
:skip_pause_after_success
exit /b 0
