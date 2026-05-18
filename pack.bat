@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem Packs the Grasshopper plugin (.gha) + dependencies into a shareable zip.
rem Usage: pack.bat [Release|Debug]

set "CONFIG=%~1"
if not defined CONFIG set "CONFIG=Release"

if /I not "%CONFIG%"=="Release" if /I not "%CONFIG%"=="Debug" (
  echo Config must be Release or Debug (got: %CONFIG%) 1>&2
  exit /b 2
)

set "ROOT_DIR=%~dp0"
if "%ROOT_DIR:~-1%"=="\" set "ROOT_DIR=%ROOT_DIR:~0,-1%"

pushd "%ROOT_DIR%" || exit /b 1

dotnet build -c "%CONFIG%" -v minimal
if errorlevel 1 (
  popd
  exit /b 1
)

set "OUT_DIR=%ROOT_DIR%\bin\%CONFIG%\net7.0-windows"
if not exist "%OUT_DIR%" set "OUT_DIR=%ROOT_DIR%\bin\%CONFIG%\net7.0"

if not exist "%OUT_DIR%" (
  echo Build output not found: %OUT_DIR% 1>&2
  popd
  exit /b 3
)

set "GHA_PATH="
set /a GHA_COUNT=0
for /f "delims=" %%F in ('dir /b /a:-d "%OUT_DIR%\*.gha" 2^>nul') do (
  set /a GHA_COUNT+=1
  set "GHA_PATH=%OUT_DIR%\%%F"
  set "BASE_NAME=%%~nF"
)

if !GHA_COUNT! EQU 0 (
  echo No .gha found in: %OUT_DIR% 1>&2
  popd
  exit /b 3
)
if !GHA_COUNT! GTR 1 (
  echo Multiple .gha files found in: %OUT_DIR% 1>&2
  dir /b "%OUT_DIR%\*.gha" 1>&2
  popd
  exit /b 4
)

set "DIST_DIR=%ROOT_DIR%\dist"
set "STAGING_DIR=%DIST_DIR%\staging"
set "ZIP_NAME=%BASE_NAME%-%CONFIG%-net7.zip"

if exist "%DIST_DIR%" rmdir /s /q "%DIST_DIR%"
mkdir "%STAGING_DIR%" || (
  popd
  exit /b 1
)

copy /Y "%GHA_PATH%" "%STAGING_DIR%\" >nul
if errorlevel 1 (
  popd
  exit /b 1
)

if exist "%OUT_DIR%\%BASE_NAME%.deps.json" (
  copy /Y "%OUT_DIR%\%BASE_NAME%.deps.json" "%STAGING_DIR%\" >nul
  if errorlevel 1 (
    popd
    exit /b 1
  )
)

for /f "delims=" %%D in ('dir /b /a:-d "%OUT_DIR%\*.dll" 2^>nul') do (
  copy /Y "%OUT_DIR%\%%D" "%STAGING_DIR%\" >nul
  if errorlevel 1 (
    popd
    exit /b 1
  )
)

if exist "%OUT_DIR%\runtimes" (
  robocopy "%OUT_DIR%\runtimes" "%STAGING_DIR%\runtimes" /E /NFL /NDL /NJH /NJS /NP >nul
  if errorlevel 8 (
    popd
    exit /b 8
  )
)

if exist "%OUT_DIR%\%BASE_NAME%.pdb" (
  copy /Y "%OUT_DIR%\%BASE_NAME%.pdb" "%STAGING_DIR%\" >nul
  if errorlevel 1 (
    popd
    exit /b 1
  )
)

powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%STAGING_DIR%\*' -DestinationPath '%DIST_DIR%\%ZIP_NAME%' -Force"
if errorlevel 1 (
  popd
  exit /b 1
)

rmdir /s /q "%STAGING_DIR%"
popd

echo Created: %DIST_DIR%\%ZIP_NAME%