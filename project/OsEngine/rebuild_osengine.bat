@echo off
setlocal

pushd "%~dp0"

echo Building OsEngine Debug...
dotnet build "..\OsEngine.sln" --configuration Debug
set "BUILD_EXIT=%ERRORLEVEL%"

popd

echo.
if not "%BUILD_EXIT%"=="0" (
    echo Build failed with exit code %BUILD_EXIT%.
) else (
    echo Build completed successfully.
)

if /I not "%~1"=="--no-pause" pause
exit /b %BUILD_EXIT%