@echo off
setlocal enabledelayedexpansion

set OS=%1
set BRANCH=%2

if "%~1"=="" goto usage
if "%~2"=="" goto usage
goto begin

:usage
echo Error: This script requires 2 arguments.
echo Usage: %~nx0 ^<os^> ^<branch^>
exit /b 1

:begin
set APP_ID=258550
set RUST_DEP_FILELIST_FILE=rust-dependency-filelist.txt

rem Root
set ROOT_DIR=%~dp0
if "%ROOT_DIR:~-1%"=="\" set ROOT_DIR=%ROOT_DIR:~0,-1%

rem Deps
set DEPS_DIR=%ROOT_DIR%\dependencies\rust
set TEMP_DEPS_DIR=%DEPS_DIR%\.temp
set SHIPPED_DEPS_DIR=%DEPS_DIR%\.shipped

rem Tools
set TOOLS_DIR=%ROOT_DIR%\tools
set DD_DIR=%TOOLS_DIR%\DepotDownloader

rem Validate OS input
set VALID_OS=0
if /i "%OS%"=="windows" set VALID_OS=1
if /i "%OS%"=="linux"   set VALID_OS=1
if "%VALID_OS%"=="0" (
    echo Error: The OS you entered is not valid. Valid options are windows or linux.
    exit /b 1
)

rem Validate BRANCH input
set VALID_BRANCH=0
if /i "%BRANCH%"=="public"  set VALID_BRANCH=1
if /i "%BRANCH%"=="release" set VALID_BRANCH=1
if /i "%BRANCH%"=="staging" set VALID_BRANCH=1
if "%VALID_BRANCH%"=="0" (
    echo Error: The branch you entered is not valid. Valid options are public, release, or staging.
    exit /b 1
)

rem Validate required directories and files
if not exist "%TOOLS_DIR%" (
    echo Error: The tools directory could not be found.
    exit /b 1
)

if not exist "%DD_DIR%\DepotDownloader.exe" (
    echo Error: Could not find the DepotDownloader binary.
    exit /b 1
)

if not exist "%DD_DIR%\%RUST_DEP_FILELIST_FILE%" (
    echo Error: Could not find the rust dependency filelist.
    exit /b 1
)

rem Delete old deps
echo Deleting all old deps...
if exist "%DEPS_DIR%\" (
    for /d %%i in ("%DEPS_DIR%\*") do rd /s /q "%%i"
    del /q "%DEPS_DIR%\*" 2>nul
)

rem Create temp deps directory
if not exist "%TEMP_DEPS_DIR%" (
    echo Creating temp deps directory...
    mkdir "%TEMP_DEPS_DIR%"
)

rem Create shipped deps directory
if not exist "%SHIPPED_DEPS_DIR%" (
    echo Creating shipped deps directory...
    mkdir "%SHIPPED_DEPS_DIR%"
)

rem Download dependencies
echo Downloading all new deps...
"%DD_DIR%\DepotDownloader.exe" -app %APP_ID% -os %OS% -branch %BRANCH% -filelist "%DD_DIR%\%RUST_DEP_FILELIST_FILE%" -dir "%TEMP_DEPS_DIR%"
if errorlevel 1 (
    echo Error: An error occurred while downloading the dependencies.
    exit /b 1
)

rem Move dependencies
echo Moving dependencies from temp directory to shipped directory.
move /y "%TEMP_DEPS_DIR%\RustDedicated_Data\Managed\*" "%SHIPPED_DEPS_DIR%"
if errorlevel 1 (
    echo Error: An error occurred while moving the dependencies.
    exit /b 1
)

rem Clean up temp directory
echo Deleting temp directories and files.
rd /s /q "%TEMP_DEPS_DIR%"
if errorlevel 1 (
    echo Error: An error occurred while deleting the temp directory.
    exit /b 1
)

echo Download completed without errors.
echo Dependencies have been successfully updated.
exit /b 0