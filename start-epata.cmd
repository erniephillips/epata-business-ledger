@echo off
cd /d %~dp0
powershell -ExecutionPolicy Bypass -File "%~dp0launch-epata.ps1" %*
if /I not "%EPATA_NO_PAUSE%"=="1" pause
