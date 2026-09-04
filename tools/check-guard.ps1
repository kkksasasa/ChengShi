# 澄时防强杀态势自检（只读，无需管理员）
# 打印守护服务的安装/运行状态、启动方式、SCM 权限与失败重启配置，
# 帮助确认“孩子杀不掉、管理员仍能管理”的防护是否到位。
$serviceName = 'Chengshi'
Write-Host "==== 澄时守护服务自检 ====" -ForegroundColor Cyan

# 1. 安装与运行
$query = & sc.exe query $serviceName 2>&1
if ($query -match 'STATE') {
    Write-Host "已安装：是"
    if ($query -match 'RUNNING') { Write-Host "运行状态：运行中 ✅" -ForegroundColor Green }
    else { Write-Host "运行状态：未运行 ⚠️（点应用内“重新安装/启动”）" -ForegroundColor Yellow }
} else {
    Write-Host "已安装：否 ⚠️（请运行 tools\setup.bat 安装守护服务）" -ForegroundColor Yellow
    Write-Host "未安装时仅软件运行时守护，重启电脑后不自动生效。" -ForegroundColor Gray
    exit 0
}

# 2. 启动方式与运行身份
$qc = & sc.exe qc $serviceName 2>&1
if ($qc -match 'AUTO_START') { Write-Host "开机自启：是 ✅" -ForegroundColor Green }
else { Write-Host "开机自启：否 ⚠️（应显示 AUTO_START）" -ForegroundColor Yellow }
if ($qc -match 'LocalSystem') { Write-Host "运行身份：LocalSystem ✅（孩子杀不掉）" -ForegroundColor Green }
else { Write-Host "运行身份：非 LocalSystem ⚠️（请重新安装）" -ForegroundColor Yellow }

# 3. SCM 权限
$sd = (& sc.exe sdshow $serviceName 2>&1) -join ''
Write-Host "SCM 权限(SDDL)：$sd"
# 标准用户能“停止/暂停”需要被显式授权 SERVICE_STOP(0x20) 或 SERVICE_PAUSE(0x40)。
# 我们的默认 DACL 只给 IU/SU 查询权（CCLCSWLOCRRC），不含 0x20/0x40。
if ($sd -match '0x20|0x40' -and ($sd -match ';;;AU' -or $sd -match ';;;IU' -or $sd -match ';;;SU' -or $sd -match ';;;WD')) {
    Write-Host "⚠️ 检测到普通用户可能被授予停止/暂停权限，请检查。" -ForegroundColor Yellow
} else {
    Write-Host "普通用户停止/暂停：已禁止 ✅（仅 SYSTEM/管理员可管理）" -ForegroundColor Green
}

# 4. 失败自动重启（看门狗）
$fail = & sc.exe failure $serviceName 2>&1
Write-Host "失败重启配置：`n$fail"
if ($fail -match 'RESTART') { Write-Host "看门狗：已启用（崩溃后自动重启）✅" -ForegroundColor Green }
else { Write-Host "看门狗：未配置 ⚠️" -ForegroundColor Yellow }

Write-Host "`n说明：防强杀依赖“LocalSystem + 自动启动 + 标准用户无停止权 + 失败重启”。" -ForegroundColor Gray
Write-Host "孩子若为标准账户，无法 sc stop / 结束进程 / 删 Program Files 下的 exe。" -ForegroundColor Gray
