@echo off
setlocal enabledelayedexpansion

rem Root
set ROOT_DIR=%~dp0
if "%ROOT_DIR:~-1%"=="\" set ROOT_DIR=%ROOT_DIR:~0,-1%

rem Deps
set DEPS_DIR=%ROOT_DIR%\dependencies\rust
set SHIPPED_DEPS_DIR=%DEPS_DIR%\.shipped

rem Tools
set TOOLS_DIR=%ROOT_DIR%\tools
set AP_DIR=%TOOLS_DIR%\AssemblyPublicizer

rem Clean up old publicized deps
echo Cleaning up old publicized deps...
del /q "%DEPS_DIR%\*" 2>nul

echo Publicizing All Dependencies
echo ============================
echo Copying unpublicized dependencies...

rem Build list of files pending publicization and copy the rest
set PENDING_COUNT=0
pushd "%SHIPPED_DEPS_DIR%"
for %%f in (*) do (
    set FILE=%%f
    set PENDING=0

    if "!FILE:~0,15!"=="Assembly-CSharp" set PENDING=1
    if "!FILE:~0,10!"=="Facepunch." set PENDING=1
    if "!FILE:~0,5!"=="Rust." set PENDING=1

    if "!PENDING!"=="1" (
        set PENDING_FILES[!PENDING_COUNT!]=%%f
        set /a PENDING_COUNT+=1
    ) else (
        copy /y "%SHIPPED_DEPS_DIR%\%%f" "%DEPS_DIR%\%%f" >nul
    )
)

rem Publicize pending files
for /l %%i in (0,1,%PENDING_COUNT%) do (
    if defined PENDING_FILES[%%i] (
        set FNAME=!PENDING_FILES[%%i]!
        echo Publicizing %SHIPPED_DEPS_DIR%\!FNAME!
        "%AP_DIR%\AssemblyPublicizer.exe" -i "!FNAME!" -o "%DEPS_DIR%\!FNAME!"
        if errorlevel 1 (
            echo Error: Failed to publicize !FNAME!. See error above.
            popd
            exit /b 1
        )
    )
)

popd
echo All dependencies have been publicized.
exit /b 0