@echo off
rem SeedLab machine report.
rem Runs the checks with the .NET runtime in this folder (nothing is installed) and writes
rem seedlab-machine-report.txt beside this file. Takes about 3 to 5 minutes.
setlocal
set "PKG=%~dp0"
set "DOTNET_ROOT=%PKG%dotnet"
set "DOTNET_ROOT_X64=%PKG%dotnet"
title SeedLab machine report
"%PKG%app\SeedLab.MachineReport.exe" %*
set "RC=%ERRORLEVEL%"
echo.
if "%RC%"=="0" echo Finished: every check passed. Please send back seedlab-machine-report.txt from this folder.
if "%RC%"=="1" echo Finished: some checks came out DIFFERENT. Please send back seedlab-machine-report.txt from this folder.
if not "%RC%"=="0" if not "%RC%"=="1" echo The program stopped with exit code %RC%. If seedlab-machine-report.txt exists, please send it back, and a photo of this window.
if /i "%~1"=="--no-pause" goto done
echo.
pause
:done
endlocal & exit /b %RC%
