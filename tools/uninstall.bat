@echo off
chcp 65001 >nul 2>&1
setlocal
set "SCRIPT=%~dp0uninstall.ps1"
echo 正在请求管理员权限以卸载澄时...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%SCRIPT%\"'"
endlocal
