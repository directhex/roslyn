@echo off
setlocal enabledelayedexpansion

rem Force build to always be set
set EXTRAARGS=
echo %*|find "-build"
if ERRORLEVEL 1 (set EXTRAARGS=!EXTRAARGS! -build)

powershell -ExecutionPolicy ByPass -NoProfile -command "& """%~dp0eng\build.ps1""" %EXTRAARGS% %*"