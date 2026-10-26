@echo off
REM Build OpenIPTV for Windows.
REM   build.bat            -> single all-in-one dist\OpenIPTV-<version>-win-x64.exe (default)
REM   build.bat --onedir   -> app folder + zip (starts faster, nothing unpacked at start)
REM Requires the .NET 10 SDK: https://dotnet.microsoft.com/download/dotnet/10.0
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET SDK was not found. Install the .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
  exit /b 1
)

set MODE=onefile
if /i "%~1"=="--onedir" set MODE=onedir

for /f "delims=" %%v in ('powershell -NoProfile -Command "([xml](Get-Content 'Directory.Build.props')).Project.PropertyGroup.Version"') do set VERSION=%%v
if "%VERSION%"=="" set VERSION=0.0.0
echo ==^> OpenIPTV v%VERSION% (%MODE%)

echo ==^> Running tests
dotnet run --project tests\OpenIPTV.Tests -c Release || exit /b 1

if exist publish rmdir /s /q publish
if not exist dist mkdir dist

if "%MODE%"=="onedir" goto onedir

echo ==^> Publishing single-file executable
dotnet publish src\OpenIPTV.App\OpenIPTV.App.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish\OpenIPTV || exit /b 1

if exist publish\OpenIPTV\libvlc (
  REM The VLC engine was copied beside the exe instead of being bundled: ship exe + libvlc folder as a zip.
  set OUT=dist\OpenIPTV-%VERSION%-win-x64.zip
  powershell -NoProfile -Command "Compress-Archive -Path 'publish\OpenIPTV\*' -DestinationPath 'dist\OpenIPTV-%VERSION%-win-x64.zip' -Force" || exit /b 1
  echo.
  echo ==^> Done: dist\OpenIPTV-%VERSION%-win-x64.zip  ^(OpenIPTV.exe + libvlc folder; keep them together^)
  goto end
)
copy /y publish\OpenIPTV\OpenIPTV.exe "dist\OpenIPTV-%VERSION%-win-x64.exe" >nul || exit /b 1
echo.
echo ==^> Done: dist\OpenIPTV-%VERSION%-win-x64.exe  (copy anywhere and run)
goto end

:onedir
echo ==^> Publishing app folder
dotnet publish src\OpenIPTV.App\OpenIPTV.App.csproj -c Release -r win-x64 --self-contained true ^
  -p:DebugType=none -o publish\OpenIPTV || exit /b 1
powershell -NoProfile -Command "Compress-Archive -Path 'publish\OpenIPTV\*' -DestinationPath 'dist\OpenIPTV-%VERSION%-win-x64-folder.zip' -Force" || exit /b 1
echo.
echo ==^> Done: publish\OpenIPTV\OpenIPTV.exe  and  dist\OpenIPTV-%VERSION%-win-x64-folder.zip

:end
endlocal
