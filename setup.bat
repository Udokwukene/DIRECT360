@echo off
cd /d "%~dp0"
cls
echo =============================================
echo           DIRECT360  --  First Setup
echo =============================================
echo.
echo  Downloading packages (needs internet, one time only)...
echo.
dotnet restore
echo.
echo  Setup complete! Run play.bat to start.
echo.
pause