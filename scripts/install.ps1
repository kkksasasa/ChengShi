#Requires -RunAsAdministrator
# 澄时(Chengshi) 安装脚本 —— 右键「以管理员身份运行」。
#
# 做的事情：
#   1. 跑测试并发布（可用 -SkipPublish 跳过）
#   2. 复制到 C:\Program Files\Chengshi\App 与 \Service（App 里也会放一份 uninstall.ps1）
#   3. 注册 LocalSystem + 延迟自启的守护服务，崩溃自动重启
#   4. 收紧服务 SCM 权限：普通用户（非管理员）不能停止/暂停服务
#   5. 数据目录 %ProgramData%\Chengshi 断开继承并锁成「Users 只读」
#   6. 注册「应用和功能」（ARP）卸载入口：设置 → 应用 里可以直接卸载
#   7. 创建开始菜单 / 桌面快捷方式，配置当前用户开机自启
param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$installRoot = Join-Path $env:ProgramFiles "Chengshi"
$appDir = Join-Path $installRoot "App"
$svcDir = Join-Path $installRoot "Service"
$dataDir = Join-Path $env:ProgramData "Chengshi"
$serviceName = "Chengshi"
$displayName = "澄时守护服务 (Chengshi Guardian)"
$arpKey = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Chengshi"

# 版本号从 Directory.Build.props 读取，用于「应用和功能」展示。
$version = "1.0.0"
$propsFile = Join-Path $root "Directory.Build.props"
if (Test-Path $propsFile) {
    $match = Select-String -Path $propsFile -Pattern "<Version>([^<]+)</Version>" | Select-Object -First 1
    if ($match) { $version = $match.Matches[0].Groups[1].Value }
}

function Write-Step($msg) { Write-Host "`n== $msg ==" -ForegroundColor Cyan }

Write-Host "== 澄时安装 (v$version) ==" -ForegroundColor Cyan

if (-not $SkipPublish) {
    & (Join-Path $root "scripts\publish.ps1")
}
$appSrc = Join-Path $root "artifacts\publish\app"
$svcSrc = Join-Path $root "artifacts\publish\service"
if (-not (Test-Path (Join-Path $appSrc "Chengshi.App.exe"))) { throw "请先运行 scripts\publish.ps1。" }
if (-not (Test-Path (Join-Path $svcSrc "Chengshi.Service.exe"))) { throw "Service 发布产物缺失。" }

# ---- 0. 停止正在运行的澄时：服务/托盘进程锁着 Program Files 里的文件，覆盖复制会失败 ----
Write-Step "停止正在运行的澄时（服务与托盘）"
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe stop $serviceName | Out-Null
}
$stopDeadline = (Get-Date).AddSeconds(20)
foreach ($procName in @("Chengshi.Service", "Chengshi.App")) {
    while ((Get-Date) -lt $stopDeadline -and (Get-Process -Name $procName -ErrorAction SilentlyContinue)) {
        Stop-Process -Name $procName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 400
    }
}

# ---- 1. 复制到 Program Files（App/Service 分目录，ServiceControl 按这个布局找服务）----
Write-Step "复制文件到 $installRoot"
New-Item -ItemType Directory -Force -Path $appDir, $svcDir | Out-Null
Copy-Item "$appSrc\*" $appDir -Recurse -Force
Copy-Item "$svcSrc\*" $svcDir -Recurse -Force
# 开机自启脚本（单文件发布时会打进 exe，这里显式放一份到安装目录）。
$startupVbs = Join-Path $root "src\Chengshi.App\startup.vbs"
if ((Test-Path $startupVbs) -and -not (Test-Path (Join-Path $appDir "startup.vbs"))) {
    Copy-Item $startupVbs $appDir -Force
}
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") (Join-Path $installRoot "uninstall.ps1") -Force

# ---- 2. 数据目录：断开继承，Users 只读，只有 SYSTEM/管理员可写 ----
# 孩子账号改不了 family.json/desks.json，白名单和密码哈希动不了。
Write-Step "收紧数据目录权限 $dataDir"
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
& icacls $dataDir /inheritance:r /grant "SYSTEM:(OI)(CI)F" "Administrators:(OI)(CI)F" "CREATOR OWNER:(OI)(CI)F" "Users:(OI)(CI)RX" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "收紧数据目录权限失败（非致命，服务启动后会再收紧一次）。" }

# ---- 3. 老配置迁移（开发期放在 %LOCALAPPDATA% 的搬过来，家长设置不丢）----
$localData = Join-Path $env:LOCALAPPDATA "Chengshi"
if (Test-Path $localData) {
    foreach ($name in @("family.json", "desks.json", "screentime.json")) {
        $from = Join-Path $localData $name
        $to = Join-Path $dataDir $name
        if ((Test-Path $from) -and -not (Test-Path $to)) {
            Copy-Item $from $to -Force
            Write-Host "已迁移 $name"
        }
    }
}

# ---- 4. 注册守护服务（延迟自启 + 崩溃自动重启 + SCM 权限收紧）----
Write-Step "注册守护服务"
$svcExe = Join-Path $svcDir "Chengshi.Service.exe"
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe config $serviceName binPath= "`"$svcExe`"" start= delayed-auto obj= LocalSystem | Out-Null
    & sc.exe description $serviceName "澄时家长守护：屏幕时间管控、进程/网络/网站拦截，开机自启、防强杀。" | Out-Null
} else {
    & sc.exe create $serviceName binPath= "`"$svcExe`"" start= delayed-auto obj= LocalSystem DisplayName= $displayName | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "sc create 失败（退出码 $LASTEXITCODE）。" }
    & sc.exe description $serviceName "澄时家长守护：屏幕时间管控、进程/网络/网站拦截，开机自启、防强杀。" | Out-Null
}
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null

# 显式固化标准服务 DACL：SYSTEM/管理员完全控制，交互用户仅查询状态。
# 普通标准用户因此无法 sc stop，实现「孩子杀不掉」；管理员仍可停止以便升级。
$sd = 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)'
& sc.exe sdset $serviceName $sd | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning "设置 SCM 权限失败（非致命，默认权限已足够）。" }

Write-Host "启动服务..."
Start-Service -Name $serviceName
Start-Sleep -Seconds 1
if ((Get-Service -Name $serviceName).Status -ne "Running") { throw "服务没能启动，请查看事件日志。" }

# ---- 5. 注册「应用和功能」卸载入口 ----
Write-Step "注册卸载入口（设置 → 应用）"
New-Item -Path $arpKey -Force | Out-Null
Set-ItemProperty -Path $arpKey -Name "DisplayName" -Value "澄时 (Chengshi 家长守护)"
Set-ItemProperty -Path $arpKey -Name "DisplayVersion" -Value $version
Set-ItemProperty -Path $arpKey -Name "Publisher" -Value "Chengshi"
Set-ItemProperty -Path $arpKey -Name "InstallLocation" -Value $installRoot
Set-ItemProperty -Path $arpKey -Name "DisplayIcon" -Value (Join-Path $appDir "chengshi.ico")
Set-ItemProperty -Path $arpKey -Name "UninstallString" -Value ("powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installRoot\uninstall.ps1`"")
Set-ItemProperty -Path $arpKey -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $arpKey -Name "NoRepair" -Value 1 -Type DWord

# ---- 6. 快捷方式 ----
Write-Step "创建快捷方式"
try {
    $sh = New-Object -ComObject WScript.Shell
    $startMenu = Join-Path $env:ProgramData "Microsoft\Windows\Start Menu\Programs"
    $lnk = $sh.CreateShortcut((Join-Path $startMenu "澄时.lnk"))
    $lnk.TargetPath = Join-Path $appDir "Chengshi.App.exe"
    $lnk.WorkingDirectory = $appDir
    $lnk.IconLocation = (Join-Path $appDir "chengshi.ico")
    $lnk.Description = "澄时 · 家长屏幕时间守护"
    $lnk.Save()

    $desktop = [Environment]::GetFolderPath("Desktop")
    $dlnk = $sh.CreateShortcut((Join-Path $desktop "澄时.lnk"))
    $dlnk.TargetPath = Join-Path $appDir "Chengshi.App.exe"
    $dlnk.WorkingDirectory = $appDir
    $dlnk.IconLocation = (Join-Path $appDir "chengshi.ico")
    $dlnk.Description = "澄时 · 家长屏幕时间守护"
    $dlnk.Save()
} catch {
    Write-Warning "创建快捷方式失败（非致命）：$_"
}

# ---- 7. 当前用户开机自启 ----
Write-Step "配置开机自启"
try {
    $vbs = Join-Path $appDir "startup.vbs"
    if (Test-Path $vbs) {
        $runKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
        Set-ItemProperty -Path $runKey -Name "Chengshi" -Value "wscript.exe `"$vbs`"" -ErrorAction SilentlyContinue
        if (-not (Get-ItemProperty -Path $runKey -Name "Chengshi" -ErrorAction SilentlyContinue)) {
            New-ItemProperty -Path $runKey -Name "Chengshi" -Value "wscript.exe `"$vbs`"" -PropertyType String -Force | Out-Null
        }
    }
} catch {
    Write-Warning "配置开机自启失败（非致命，可在应用内「开机启动」开关补开）：$_"
}

Write-Host ""
Write-Host "✅ 安装完成。" -ForegroundColor Green
Write-Host "  1. 从开始菜单打开「澄时」，完成家长设置（顺手抄下找回码）。"
Write-Host "  2. 点「开始守护」。请给孩子使用标准用户（非管理员）账号。"
Write-Host "  3. 卸载：设置 → 应用 → 澄时，或运行 $installRoot\uninstall.ps1"
