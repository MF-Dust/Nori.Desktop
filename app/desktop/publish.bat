@echo off
setlocal EnableExtensions EnableDelayedExpansion
cd /d "%~dp0"

set "APP_VERSION=%NORI_VERSION%"
if "%APP_VERSION%"=="" set "APP_VERSION=%NORI_PRODUCT_VERSION%"
if "%APP_VERSION%"=="" set "APP_VERSION=Dev"
set "NORI_VERSION=%APP_VERSION%"
set "NORI_PRODUCT_VERSION=%APP_VERSION%"
if "%NORI_COMMIT_SHA%"=="" for /f "delims=" %%H in ('git rev-parse HEAD 2^>nul') do set "NORI_COMMIT_SHA=%%H"
set "REVISION=%NORI_DEPLOYMENT_REVISION%"
if "%REVISION%"=="" set "REVISION=0"
if /I "%APP_VERSION%"=="Dev" (
	set "NUMERIC_VERSION=0.0.0"
) else (
	set "VERSION_FOR_NUMERIC=%APP_VERSION%"
	if /I "!VERSION_FOR_NUMERIC:~0,1!"=="v" set "VERSION_FOR_NUMERIC=!VERSION_FOR_NUMERIC:~1!"
	for /f "tokens=1 delims=-+" %%V in ("!VERSION_FOR_NUMERIC!") do set "NUMERIC_VERSION=%%V"
)
if "%NUMERIC_VERSION%"=="" set "NUMERIC_VERSION=0.0.0"
node scripts\validate-publish-input.mjs "%APP_VERSION%" "%REVISION%" win-x64 || goto :error
if "%NORI_INCLUDE_RUNTIME%"=="1" goto :runtime_error
if /I "%NORI_INCLUDE_RUNTIME%"=="true" goto :runtime_error

if /I not "%NORI_SKIP_FRONTEND%"=="1" (
	call pnpm build || goto :error
)

set "ROOT=bin\publish\win-x64"
set "SLOT=app-%NUMERIC_VERSION%-%REVISION%"
if exist "%ROOT%" rmdir /s /q "%ROOT%"
mkdir "%ROOT%\%SLOT%" || goto :error

echo 发布 Nori.Desktop 槽...
node scripts\prepare-unidesktop.mjs win-x64 || goto :error
dotnet publish Nori.Desktop/Nori.Desktop.csproj -c Release -r win-x64 --self-contained false -p:NoriProductVersion="%APP_VERSION%" -p:NoriDeploymentRevision="%REVISION%" -p:NoriSentryDsnNative="%NORI_SENTRY_DSN_NATIVE%" -p:NoriSentryRelease="%NORI_SENTRY_RELEASE%" -p:NoriSentryEnvironment="%NORI_SENTRY_ENVIRONMENT%" -p:PublishSingleFile=false -p:PublishReadyToRun=false -o "%ROOT%\%SLOT%" || goto :error
if not "%NORI_KEEP_SYMBOLS%"=="1" if /I not "%NORI_KEEP_SYMBOLS%"=="true" for /r "%ROOT%\%SLOT%" %%F in (*.pdb) do del /q "%%F"
node scripts\write-deployment-json.mjs "%ROOT%\%SLOT%\deployment.json" "%APP_VERSION%" "%NUMERIC_VERSION%" "%REVISION%" win-x64 Nori.Desktop.exe || goto :error

rem 根入口只由 launcher 发布，发布包不创建 data。
echo 发布稳定根入口...
dotnet publish Nori.AppLauncher/Nori.AppLauncher.csproj -c Release -r win-x64 --self-contained false -p:NoriProductVersion="%APP_VERSION%" -p:NoriDeploymentRevision="%REVISION%" -p:PublishSingleFile=false -p:PublishReadyToRun=false -o "%ROOT%" || goto :error
if not "%NORI_KEEP_SYMBOLS%"=="1" if /I not "%NORI_KEEP_SYMBOLS%"=="true" for /r "%ROOT%" %%F in (*.pdb) do del /q "%%F"
copy /y "..\..\LICENSE" "%ROOT%\LICENSE" >nul || goto :error
>"%ROOT%\.current.tmp" echo %SLOT%
move /y "%ROOT%\.current.tmp" "%ROOT%\.current" >nul
if not exist "%ROOT%\Nori.exe" goto :error
node scripts\validate-publish-structure.mjs "%ROOT%" win-x64 || goto :error
node scripts\check-package-size.mjs --path "%ROOT%" --label "win-x64 package" --max-mib 180 || goto :error

echo 发布完成: %ROOT%
exit /b 0

:runtime_error
echo [错误] Windows 发布固定为 framework-dependent, 不支持打包 .NET Runtime。
exit /b 2
:error
echo [错误] 构建或发布失败。
exit /b 1
