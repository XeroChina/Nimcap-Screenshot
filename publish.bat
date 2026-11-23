@echo off
setlocal EnableExtensions DisableDelayedExpansion

set "PUBLISH_EXIT=1"
set "PUBLISH_PAUSE=1"
if /i "%~1"=="--help" goto help
if /i "%~1"=="--no-pause" (
    set "PUBLISH_PAUSE=0"
) else if not "%~1"=="" (
    echo Unknown argument: %~1
    goto help
)

pushd "%~dp0"
if errorlevel 1 goto finish

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: Install the .NET 10 SDK, then run this script again.
    goto cleanup
)

set "PUBLISH_OUTPUT=%~dp0publish"
set "LAUNCHER_STAGE=%TEMP%\nimcap-launcher-%RANDOM%"

echo Publishing NimCap.Screenshot - Release / win-x64 / self-contained portable layout
echo Output: "%PUBLISH_OUTPUT%"
echo Model weights and local user settings are not included.
echo.

dotnet publish "%~dp0NimCap.Screenshot.csproj" -c Release -r win-x64 --self-contained true -o "%PUBLISH_OUTPUT%\Program" -m:1 -p:UseSharedCompilation=false -p:PublishSingleFile=false -p:PublishTrimmed=false
if errorlevel 1 (
    echo.
    echo ERROR: Publish failed. Review the output above.
    goto cleanup
)
if not exist "%LAUNCHER_STAGE%" mkdir "%LAUNCHER_STAGE%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Build-Launcher.ps1" -Source "%~dp0tools\Launcher\launcher.cpp" -Output "%LAUNCHER_STAGE%\native\NimCap.Screenshot.exe"
if errorlevel 1 (
    dotnet publish "%~dp0tools\Launcher\Launcher.csproj" -c Release -r win-x64 -o "%LAUNCHER_STAGE%\managed" -m:1 -p:UseSharedCompilation=false
    if errorlevel 1 (
        echo.
        echo ERROR: Launcher publish failed. Review the output above.
        rd /s /q "%LAUNCHER_STAGE%" 2>nul
        goto cleanup
    )
    copy /y "%LAUNCHER_STAGE%\managed\NimCap.Screenshot.exe" "%PUBLISH_OUTPUT%\NimCap.Screenshot.exe" >nul
) else (
    copy /y "%LAUNCHER_STAGE%\native\NimCap.Screenshot.exe" "%PUBLISH_OUTPUT%\NimCap.Screenshot.exe" >nul
)
if errorlevel 1 goto launcher_copy_failed
rd /s /q "%LAUNCHER_STAGE%" 2>nul

copy /y "%~dp0LICENSE" "%PUBLISH_OUTPUT%\LICENSE" >nul
if errorlevel 1 goto copy_failed
copy /y "%~dp0THIRD-PARTY-NOTICES.md" "%PUBLISH_OUTPUT%\THIRD-PARTY-NOTICES.md" >nul
if errorlevel 1 goto copy_failed
copy /y "%~dp0README.md" "%PUBLISH_OUTPUT%\README.md" >nul
if errorlevel 1 goto copy_failed
if not exist "%PUBLISH_OUTPUT%\Program\NimCap.Screenshot.exe" (
    echo ERROR: Publish did not produce Program\NimCap.Screenshot.exe.
    goto cleanup
)
if not exist "%PUBLISH_OUTPUT%\Program\ThirdPartyLicenses\PACKAGE-NOTICES.md" (
    echo ERROR: Publish did not include third-party license materials.
    goto cleanup
)
if not exist "%PUBLISH_OUTPUT%\Program\ThirdPartyLicenses\DirectML-LICENSE.txt" (
    echo ERROR: Publish did not include the DirectML distribution license.
    goto cleanup
)

REM The portable marker activates <package root>\Data for every user file.
type nul > "%PUBLISH_OUTPUT%\portable"
if not exist "%PUBLISH_OUTPUT%\Data" mkdir "%PUBLISH_OUTPUT%\Data"
type nul > "%PUBLISH_OUTPUT%\Data\.gitkeep"

echo.
echo SUCCESS: "%PUBLISH_OUTPUT%\NimCap.Screenshot.exe"
echo Distribute the entire output directory, not only the EXE.
echo The target PC does not need the .NET Desktop Runtime.
echo Native features may still require VC++ x64 Runtime / Media Foundation.
set "PUBLISH_EXIT=0"
goto cleanup

:launcher_copy_failed
echo ERROR: Could not copy the portable launcher.
goto cleanup

:copy_failed
echo ERROR: Could not copy distribution documentation.
goto cleanup

:help
echo Usage: publish.bat [--no-pause ^| --help]
echo Publishes a self-contained portable Windows x64 Release build to:
echo   publish\ (next to this script)
echo Layout: NimCap.Screenshot.exe launcher, Program\, Data\, portable marker.
if /i "%~1"=="--help" exit /b 0
exit /b 1

:cleanup
popd
:finish
@REM  if "%PUBLISH_PAUSE%"=="1" pause
@REM  exit /b %PUBLISH_EXIT%
