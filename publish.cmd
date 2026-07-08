@echo off
setlocal enabledelayedexpansion

set VERSION_FILE=version.txt

if not exist %VERSION_FILE% (
    echo 0 > %VERSION_FILE%
)

set /p CURRENT_VERSION=<%VERSION_FILE%
set CURRENT_VERSION=%CURRENT_VERSION: =%

set /a NEW_VERSION=%CURRENT_VERSION%+1
echo %NEW_VERSION% > %VERSION_FILE%

echo ===================================================
echo Publishing Trainingify (Version: 1.0.%NEW_VERSION%)
echo ===================================================

set DEPLOY_DIR=deploy
if exist %DEPLOY_DIR% (
    echo Cleaning %DEPLOY_DIR% folder...
    del /s /q %DEPLOY_DIR%\* >nul 2>&1
) else (
    mkdir %DEPLOY_DIR%
)

echo Running dotnet publish...
dotnet publish src/Trainingify/Trainingify.csproj -f net10.0-windows10.0.19041.0 -c Release /p:WindowsPackageType=None /p:WindowsAppSDKSelfContained=true /p:ApplicationVersion=%NEW_VERSION% /p:ApplicationDisplayVersion=1.0.%NEW_VERSION%

if %ERRORLEVEL% neq 0 (
    echo Error during dotnet publish.
    exit /b %ERRORLEVEL%
)

set PUBLISH_DIR=src\Trainingify\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish
set ZIP_FILE=%DEPLOY_DIR%\Trainingify_v1.0.%NEW_VERSION%.zip

echo Zipping %PUBLISH_DIR% to %ZIP_FILE%...
powershell -Command "Compress-Archive -Path '%PUBLISH_DIR%\*' -DestinationPath '%ZIP_FILE%' -Force"

if %ERRORLEVEL% neq 0 (
    echo Error during zipping.
    exit /b %ERRORLEVEL%
)

echo.
echo ===================================================
echo Publish completed successfully!
echo Version : 1.0.%NEW_VERSION%
echo ZIP created : %ZIP_FILE%
echo ===================================================
