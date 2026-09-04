# 澄时(Chengshi) 卸载脚本 —— 需以管理员身份运行（uninstall.bat 会自动提权）
#
# 移除：守护服务、开始菜单/桌面快捷方式、当前用户开机自启。
# 不删除 %ProgramData%\Chengshi（家长设置/统计留着，重装不丢）；
# 若确实要清掉用户数据，手动删除该目录即可。
$ErrorActionPreference = 'Stop'

$serviceName = 'Chengshi'
$installRoot = Join-Path ${env:ProgramFiles} 'Chengshi'
$dataRoot   = Join-Path ${env:ProgramData} 'Chengshi'

function Write-Step($msg) { Write-Host "`n== $msg ==" -ForegroundColor Cyan }

Write-Step '停止并删除守护服务'
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    & sc.exe stop $serviceName | Out-Null
    & sc.exe delete $serviceName | Out-Null
    Write-Host '服务已移除。'
} else {
    Write-Host '未发现服务（跳过）。'
}

Write-Step '移除断网防火墙规则'
& netsh.exe advfirewall firewall delete rule name='Chengshi-NoInternet' | Out-Null

Write-Step '清除浏览器上网策略'
# 绿色上网通过 Chrome/Edge 企业策略注入，卸载时清掉我们的策略值，
# 避免守护中卸载（或崩溃残留）让浏览器一直显示"由组织管理"。
foreach ($browser in @('Google\Chrome', 'Microsoft\Edge')) {
    $policyKey = "HKLM:\SOFTWARE\Policies\$browser"
    foreach ($value in @('URLBlocklist', 'URLAllowlist', 'URLBlocklistEnabled')) {
        Remove-ItemProperty -Path $policyKey -Name $value -ErrorAction SilentlyContinue
    }
}

Write-Step '删除快捷方式'
$startMenu = Join-Path ${env:ProgramData} 'Microsoft\Windows\Start Menu\Programs'
@(
    (Join-Path $startMenu '澄时.lnk'),
    (Join-Path $startMenu 'Chengshi.lnk'),
    (Join-Path ([Environment]::GetFolderPath('Desktop')) '澄时.lnk')
) | ForEach-Object {
    if (Test-Path $_) { Remove-Item $_ -Force; Write-Host "已删除 $_" }
}

Write-Step '移除「应用和功能」卸载入口'
$arp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Chengshi'
try {
    Remove-Item $arp -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host '已移除卸载入口。'
} catch {
    Write-Warning "移除卸载入口失败：$_（非致命）。"
}

Write-Step '移除开机自启'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
try {
    Remove-ItemProperty -Path $runKey -Name 'Chengshi' -ErrorAction SilentlyContinue
    Write-Host '已移除开机自启项。'
} catch {
    Write-Warning "移除开机自启项失败：$_（非致命）。"
}

Write-Step '删除安装目录'
if (Test-Path $installRoot) {
    Remove-Item $installRoot -Recurse -Force
    Write-Host "已删除 $installRoot"
}

Write-Host "`n✅ 卸载完成。" -ForegroundColor Green
Write-Host "  说明：用户数据目录 $dataRoot 已保留（家长设置不丢）。"
Write-Host "  如需彻底清除数据，请手动删除该目录。"
