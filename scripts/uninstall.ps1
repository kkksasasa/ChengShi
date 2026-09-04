#Requires -RunAsAdministrator
# 澄时(Chengshi) 卸载脚本。默认保留配置（重装不丢家长设置）；加 -RemoveData 连配置和密码一起删。
param(
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"
$serviceName = "Chengshi"
$installRoot = Join-Path $env:ProgramFiles "Chengshi"
$dataDir = Join-Path $env:ProgramData "Chengshi"
$arpKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Chengshi"

Write-Host "== 澄时卸载 ==" -ForegroundColor Cyan

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Host "停止并删除守护服务..." -ForegroundColor Yellow
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe delete $serviceName | Out-Null
} else {
    Write-Host "未发现守护服务，跳过。" -ForegroundColor Yellow
}

Write-Host "移除断网防火墙规则..." -ForegroundColor Yellow
& netsh.exe advfirewall firewall delete rule name="Chengshi-NoInternet" | Out-Null

Write-Host "移除开机自启..." -ForegroundColor Yellow
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "Chengshi" -ErrorAction SilentlyContinue

Write-Host "移除快捷方式..." -ForegroundColor Yellow
$startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs"
@(
    (Join-Path $startMenu "澄时.lnk"),
    (Join-Path $startMenu "Chengshi.lnk"),
    (Join-Path $startMenu "Chengshi\澄时.lnk"),
    (Join-Path ([Environment]::GetFolderPath("Desktop")) "澄时.lnk")
) | ForEach-Object {
    Remove-Item $_ -Force -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path $startMenu "Chengshi") -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "移除「应用和功能」卸载入口..." -ForegroundColor Yellow
Remove-Item $arpKey -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "删除程序文件..." -ForegroundColor Yellow
Remove-Item $installRoot -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveData) {
    Write-Host "删除配置数据（含家长密码与找回码）..." -ForegroundColor Yellow
    Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "保留配置数据：$dataDir（重装后家长设置不丢）" -ForegroundColor Green
}

Write-Host ""
Write-Host "✅ 卸载完成。" -ForegroundColor Green
