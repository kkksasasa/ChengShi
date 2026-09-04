# 卸载澄时守护服务（需以管理员身份运行）
$ErrorActionPreference = 'Stop'
$serviceName = 'Chengshi'

& sc.exe stop $serviceName | Out-Null
& sc.exe delete $serviceName | Out-Null
Write-Host '已卸载澄时守护服务。'
