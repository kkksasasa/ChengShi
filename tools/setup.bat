@echo off
chcp 65001 >nul 2>&1
setlocal
set "SCRIPT=%~dp0install.ps1"
echo ============================================
echo   澄时 (Chengshi) 安装程序
echo ============================================
echo 将安装到 C:\Program Files\Chengshi\
echo  - 程序与守护服务（开机自启、孩子杀不掉）
echo  - 开始菜单 / 桌面快捷方式
echo  - 当前用户开机自启
echo.
echo 接下来会请求管理员权限，请点“是”。
echo ============================================
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%SCRIPT%\"'"
endlocal
