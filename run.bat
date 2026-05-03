@echo off
REM SmartPantry — local Release runner (Windows .bat wrapper)
REM Delegates to run.ps1 so we get one source of truth for env loading.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1" %*
