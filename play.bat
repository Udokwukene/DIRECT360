@echo off
cd /d "%~dp0"
cls
echo =============================================
echo                 DIRECT360
echo        Universal Controller Remapper
echo =============================================
echo.

echo  Building...
dotnet publish DIRECT360.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:ReadyToRun=false ^
  -o dist ^
  --nologo ^
  --verbosity quiet

if not exist "dist\DIRECT360.exe" (
    echo.
    echo  Build failed. Run rebuild.bat to see full error details.
    echo.
    pause
    exit /b 1
)

echo  Starting...
echo.
dist\DIRECT360.exe
pause