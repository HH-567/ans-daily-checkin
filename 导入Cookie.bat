@echo off
chcp 65001 >nul
cd /d "%~dp0"
node import-cookie.mjs
echo.
pause
