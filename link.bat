@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Symlink the plugin build output into Grasshopper's Libraries folder (Rhino 8 on Windows).
rem Usage:
rem   link.bat
rem
rem Optional env overrides:
rem   GH_LIB_DIR=...           target Grasshopper Libraries directory
rem   BUILD_DIR=...            directory that contains the built .gha/.deps.json/.dll files
rem   CONFIG=Debug|Release     selects build folder when BUILD_DIR isn't set
rem   TFM=net7.0-windows       target framework folder name when BUILD_DIR isn't set

set "PLUGIN_DIR=%~dp0"
if "%PLUGIN_DIR:~-1%"=="\" set "PLUGIN_DIR=%PLUGIN_DIR:~0,-1%"

if not defined CONFIG set "CONFIG=Debug"
if not defined TFM set "TFM=net7.0-windows"

set "DEFAULT_GH_LIB_DIR=%APPDATA%\McNeel\Rhinoceros\8.0\Plug-ins\Grasshopper\Libraries"
if not defined GH_LIB_DIR set "GH_LIB_DIR=%DEFAULT_GH_LIB_DIR%"

if not defined BUILD_DIR set "BUILD_DIR=%PLUGIN_DIR%\bin\%CONFIG%\%TFM%"

if not exist "%BUILD_DIR%" (
  echo Error: BUILD_DIR not found: %BUILD_DIR% 1>&2
  echo Hint: run dotnet build first, or set BUILD_DIR explicitly. 1>&2
  exit /b 1
)

set "GHA_PATH="
set /a GHA_COUNT=0
for /f "delims=" %%F in ('dir /b /a:-d "%BUILD_DIR%\*.gha" 2^>nul') do (
  set /a GHA_COUNT+=1
  set "GHA_PATH=%BUILD_DIR%\%%F"
  set "BASE_NAME=%%~nF"
  set "GHA_FILE=%%F"
)

if !GHA_COUNT! EQU 0 (
  echo Error: No .gha found in: %BUILD_DIR% 1>&2
  exit /b 1
)
if !GHA_COUNT! GTR 1 (
  echo Error: Multiple .gha files found in: %BUILD_DIR% 1>&2
  dir /b "%BUILD_DIR%\*.gha" 1>&2
  echo Set BUILD_DIR to a folder containing exactly one .gha. 1>&2
  exit /b 1
)

if not exist "%GH_LIB_DIR%" mkdir "%GH_LIB_DIR%" || exit /b 1

call :LinkOne "%GHA_PATH%" "%GH_LIB_DIR%\%GHA_FILE%"
if errorlevel 1 exit /b 1

call :LinkOne "%BUILD_DIR%\%BASE_NAME%.deps.json" "%GH_LIB_DIR%\%BASE_NAME%.deps.json"
if errorlevel 1 exit /b 1

for /f "delims=" %%D in ('dir /b /a:-d "%BUILD_DIR%\*.dll" 2^>nul') do (
  call :LinkOne "%BUILD_DIR%\%%D" "%GH_LIB_DIR%\%%D"
  if errorlevel 1 exit /b 1
)

echo Linked plugin from: %BUILD_DIR%
echo Into Grasshopper:  %GH_LIB_DIR%
echo   - %GHA_FILE%
echo   - %BASE_NAME%.deps.json (if present)
echo   - ^*.dll (if present)
echo.
echo Note: Restart Rhino / Grasshopper to reload the plugin.
exit /b 0

:LinkOne
set "SRC=%~1"
set "DST=%~2"

if not exist "%SRC%" exit /b 0

del /f /q "%DST%" >nul 2>&1
mklink "%DST%" "%SRC%" >nul 2>&1
if not errorlevel 1 exit /b 0

copy /Y "%SRC%" "%DST%" >nul
if errorlevel 1 exit /b 1

exit /b 0