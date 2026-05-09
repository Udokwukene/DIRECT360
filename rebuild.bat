@echo off
cd /d "%~dp0"
cls
echo =============================================
echo        DIRECT360  --  Full Rebuild
echo =============================================
echo.

if exist "dist\DIRECT360.exe" del "dist\DIRECT360.exe"

dotnet publish DIRECT360.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:ReadyToRun=false ^
  -o dist ^
  --nologo ^
  --verbosity normal

echo.
if exist "dist\DIRECT360.exe" (
    echo  Success! DIRECT360.exe is in the dist folder.
) else (
    echo  Build failed. Check errors above.
)
echo.
pause