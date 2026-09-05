# 澄时(Chengshi) 安装脚本 —— 需以管理员身份运行（setup.bat 会自动提权）
#
# 做的事情：
#   1. 取得自包含发布产物（优先用仓库里已发布的 artifacts/publish，否则当场 dotnet publish）
#   2. 复制到 C:\Program Files\Chengshi\App 与 \Service
#   3. 注册 LocalSystem + 自动启动的守护服务（开机自启、孩子杀不掉）
#   4. 配置服务失败自动重启（看门狗基线）
#   5. 收紧服务 SCM 权限：普通用户(非管理员)不可停止/暂停，管理员仍可（便于升级）
#   6. 创建开始菜单 / 桌面快捷方式
#   7. 为当前用户配置开机自启（HKCU Run -> startup.vbs）
#
# 说明：配置数据存于 %ProgramData%\Chengshi，由程序运行时设成“Users 只读”，
#       孩子改不了 family.json；Program Files 安装目录本身也是 Users 只读。
$ErrorActionPreference = 'Stop'

$serviceName = 'Chengshi'
$displayName  = '澄时守护服务 (Chengshi Guardian)'
$installRoot = Join-Path ${env:ProgramFiles} 'Chengshi'
$appDir      = Join-Path $installRoot 'App'
$svcDir      = Join-Path $installRoot 'Service'
$repoRoot    = Resolve-Path (Join-Path $PSScriptRoot '..')
$appProj     = Join-Path $repoRoot 'src\Chengshi.App\Chengshi.App.csproj'
$svcProj     = Join-Path $repoRoot 'src\Chengshi.Service\Chengshi.Service.csproj'

function Write-Step($msg) { Write-Host "`n== $msg ==" -ForegroundColor Cyan }

function Find-Dotnet {
    # 注意：本脚本运行在系统自带的 Windows PowerShell 5.1 上（ChengshiSetup 用
    # powershell.exe 拉起），不能使用 7.0 才有的 ?. 语法。
    $cands = @()
    $onPath = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($onPath) { $cands += $onPath.Source }
    $cands += @(
        'D:\tools\dotnet10\dotnet.exe',
        (Join-Path ${env:ProgramFiles} 'dotnet\dotnet.exe')
    )
    if (${env:ProgramFiles(x86)}) {
        $cands += (Join-Path ${env:ProgramFiles(x86)} 'dotnet\dotnet.exe')
    }
    foreach ($c in $cands) {
        if ($c -and (Test-Path $c)) { return $c }
    }
    return $null
}

# ---- 1. 取得发布产物 ----
Write-Step '准备发布产物'
# 优先用脚本同目录下的 publish（打包分发场景，离线可装）；
# 其次用仓库 artifacts/publish（开发场景）；都没有才当场 dotnet publish（需 SDK+网络）。
$bundledApp = Join-Path $PSScriptRoot 'publish\app'
$bundledSvc = Join-Path $PSScriptRoot 'publish\service'
$devApp = Join-Path $repoRoot 'artifacts\publish\app'
$devSvc = Join-Path $repoRoot 'artifacts\publish\service'

if ((Test-Path (Join-Path $bundledApp 'Chengshi.App.exe')) -and (Test-Path (Join-Path $bundledSvc 'Chengshi.Service.exe'))) {
    $appPub = $bundledApp; $svcPub = $bundledSvc
    Write-Host '使用打包内嵌的发布产物（离线安装）。'
} elseif ((Test-Path (Join-Path $devApp 'Chengshi.App.exe')) -and (Test-Path (Join-Path $devSvc 'Chengshi.Service.exe'))) {
    $appPub = $devApp; $svcPub = $devSvc
    Write-Host '使用仓库 artifacts/publish 的发布产物（离线安装）。'
} else {
    $appPub = $devApp; $svcPub = $devSvc
    $dotnet = Find-Dotnet
    if (-not $dotnet) {
        throw "找不到 dotnet。请先安装 .NET 10 SDK 或将其加入 PATH 后重试。"
    }
    Write-Host "使用 dotnet: $dotnet"
    # 注意：不用 PublishSingleFile。单文件发布会把 startup.vbs / chengshi.ico 这类
    # Content 文件打进 exe 并解压到临时目录，导致开机自启的 Run 键指向临时目录而失效。
    # 装到 Program Files 本就是文件夹形态，普通自包含文件夹布局更稳。
    $pubArgs = @('publish','-c','Release','-r','win-x64','--self-contained','true')

    Write-Host '发布 Chengshi.App ...'
    & $dotnet @pubArgs -o $appPub $appProj | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '发布 Chengshi.App 失败。' }

    Write-Host '发布 Chengshi.Service ...'
    & $dotnet @pubArgs -o $svcPub $svcProj | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '发布 Chengshi.Service 失败。' }
}

if (-not (Test-Path (Join-Path $appPub 'Chengshi.App.exe'))) { throw 'App 发布产物缺失。' }
if (-not (Test-Path (Join-Path $svcPub 'Chengshi.Service.exe'))) { throw 'Service 发布产物缺失。' }

# ---- 2. 复制到 Program Files ----
# 2.0 先停干净正在运行的澄时：服务进程锁着 Service 目录的 DLL，托盘进程锁着 App 目录的，
#     不停就删/换文件必失败（升级与重装场景都会踩）。
Write-Step '停止正在运行的澄时（服务与托盘）'
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
    & sc.exe stop $serviceName | Out-Null
}
$deadline = (Get-Date).AddSeconds(20)
foreach ($procName in @('Chengshi.Service', 'Chengshi.App')) {
    while ((Get-Date) -lt $deadline -and (Get-Process -Name $procName -ErrorAction SilentlyContinue)) {
        Stop-Process -Name $procName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 400
    }
}
# 服务标记删除，稍后第 3 步会重新注册。
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Milliseconds 500
}

function Remove-Stamped($dir) {
    if (-not (Test-Path $dir)) { return }
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item $dir -Recurse -Force -ErrorAction Stop
            return
        } catch {
            Write-Warning "第 $attempt 次清理 $dir 失败：$($_.Exception.Message)"
            Start-Sleep -Seconds (2 * $attempt)
        }
    }
    throw "无法删除 $dir：文件仍被占用。请退出澄时托盘（或重启电脑）后重新运行安装。"
}

Write-Step '复制到 Program Files'
Remove-Stamped $appDir
Remove-Stamped $svcDir
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
New-Item -ItemType Directory -Path $svcDir -Force | Out-Null
Copy-Item -Path (Join-Path $appPub '*') -Destination $appDir -Recurse -Force
Copy-Item -Path (Join-Path $svcPub '*') -Destination $svcDir -Recurse -Force
# startup.vbs 作为 Content 会进入发布产物，但保险起见再确认一次
if (-not (Test-Path (Join-Path $appDir 'startup.vbs'))) {
    $vbsSrc = @(
        (Join-Path $repoRoot 'src\Chengshi.App\startup.vbs'),
        (Join-Path $PSScriptRoot 'startup.vbs')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($vbsSrc) {
        Copy-Item $vbsSrc $appDir -Force
    } else {
        Write-Warning '找不到 startup.vbs，当前用户开机自启可在应用内开关补开。'
    }
}
# 卸载脚本也放进安装目录，「应用和功能」的卸载入口会指向它。
# 仓库布局在 ..\scripts\；打包内嵌布局就在脚本同目录。
$uninstallSrc = @(
    (Join-Path $PSScriptRoot 'uninstall.ps1'),
    (Join-Path $repoRoot 'scripts\uninstall.ps1')
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($uninstallSrc) {
    Copy-Item $uninstallSrc (Join-Path $installRoot 'uninstall.ps1') -Force
} else {
    Write-Warning '找不到 uninstall.ps1，「应用和功能」卸载入口不可用。'
}
Write-Host "已安装到 $installRoot"

# ---- 2.5 数据目录：断开继承，Users 只读，只有 SYSTEM/管理员可写 ----
# 孩子账号改不了 family.json/desks.json，白名单和密码哈希动不了。
Write-Step '收紧数据目录权限'
$dataRoot = Join-Path ${env:ProgramData} 'Chengshi'
New-Item -ItemType Directory -Force -Path $dataRoot | Out-Null
& icacls $dataRoot /inheritance:r /grant 'SYSTEM:(OI)(CI)F' 'Administrators:(OI)(CI)F' 'CREATOR OWNER:(OI)(CI)F' 'Users:(OI)(CI)RX' | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Warning '收紧数据目录权限失败（非致命，服务启动后会再收紧一次）。' }

# ---- 3. 注册守护服务 ----
Write-Step '注册守护服务 (LocalSystem / 自动启动)'
$svcExe = Join-Path $svcDir 'Chengshi.Service.exe'
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    & sc.exe stop $serviceName | Out-Null
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}
& sc.exe create $serviceName binPath= "`"$svcExe`"" start= auto obj= LocalSystem DisplayName= $displayName
if ($LASTEXITCODE -ne 0) { throw "sc create 失败（退出码 $LASTEXITCODE）。" }
& sc.exe description $serviceName "澄时家长守护：屏幕时间管控、进程/网络/网站拦截，开机自启、防强杀。"
Write-Host '服务已注册。'

# ---- 4. 失败自动重启（看门狗基线）----
Write-Step '配置服务失败自动重启'
# 1 天内累计失败后：第 1 次 1 秒后重启，之后每 1 分钟重启一次
& sc.exe failure $serviceName reset= 86400 actions= restart/1000/restart/60000/restart/60000
if ($LASTEXITCODE -ne 0) { Write-Warning '设置失败重启动作失败（非致命）。' }

# ---- 5. 收紧 SCM 权限：普通（非管理员）用户只可查询、不可停止/暂停 ----
Write-Step '收紧服务 SCM 权限'
# 显式固化标准服务 DACL：SYSTEM / 管理员(BA) 完全控制；交互/服务用户(IU/SU) 仅查询状态
# （CCLCSWLOCRRC 不含 SERVICE_STOP/PACE）。普通标准用户因此无法 sc stop，实现“孩子杀不掉”；
# 管理员仍可停止服务以便升级。（注意：不能 deny“已验证用户(AU)”，因管理员也属 AU，会误伤。）
$sd = 'D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)'
& sc.exe sdset $serviceName $sd
if ($LASTEXITCODE -ne 0) { Write-Warning '设置 SCM 权限失败（非致命，默认权限已足够）。' }

# 启动服务
& sc.exe start $serviceName
Write-Host '守护服务已启动。'

# ---- 6. 快捷方式 ----
Write-Step '创建快捷方式'
try {
    $ws = New-Object -ComObject WScript.Shell
    $startMenu = Join-Path ${env:ProgramData} 'Microsoft\Windows\Start Menu\Programs\Chengshi'
    New-Item -ItemType Directory -Path $startMenu -Force | Out-Null

    $link = $ws.CreateShortcut((Join-Path $startMenu '澄时.lnk'))
    $link.TargetPath = Join-Path $appDir 'Chengshi.App.exe'
    $link.WorkingDirectory = $appDir
    $link.IconLocation = (Join-Path $appDir 'chengshi.ico')
    $link.Description = '澄时 · 家长屏幕时间守护'
    $link.Save()

    $desktop = [Environment]::GetFolderPath('Desktop')
    $dlink = $ws.CreateShortcut((Join-Path $desktop '澄时.lnk'))
    $dlink.TargetPath = Join-Path $appDir 'Chengshi.App.exe'
    $dlink.WorkingDirectory = $appDir
    $dlink.IconLocation = (Join-Path $appDir 'chengshi.ico')
    $dlink.Description = '澄时 · 家长屏幕时间守护'
    $dlink.Save()
    Write-Host '已创建开始菜单与桌面快捷方式。'
} catch {
    Write-Warning "创建快捷方式失败：$_（非致命）。"
}

# ---- 7. 当前用户开机自启 ----
Write-Step '配置开机自启'
try {
    $vbs = Join-Path $appDir 'startup.vbs'
    $cmd = "wscript.exe `"$vbs`""
    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    Set-ItemProperty -Path $runKey -Name 'Chengshi' -Value $cmd
    Write-Host '已为当前用户配置开机自启。'
} catch {
    Write-Warning "配置开机自启失败：$_（非致命，可在应用内“开机启动”开关补开）。"
}

# ---- 8. 注册「应用和功能」（ARP）卸载入口 ----
Write-Step '注册卸载入口（设置 → 应用）'
try {
    $version = '1.0.0'
    $propsFile = @(
        (Join-Path $repoRoot 'Directory.Build.props'),
        (Join-Path $PSScriptRoot 'Directory.Build.props')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
    if ($propsFile) {
        $m = Select-String -Path $propsFile -Pattern '<Version>([^<]+)</Version>' | Select-Object -First 1
        if ($m) { $version = $m.Matches[0].Groups[1].Value }
    }
    $arp = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Chengshi'
    New-Item -Path $arp -Force | Out-Null
    Set-ItemProperty -Path $arp -Name 'DisplayName' -Value '澄时 (Chengshi 家长守护)'
    Set-ItemProperty -Path $arp -Name 'DisplayVersion' -Value $version
    Set-ItemProperty -Path $arp -Name 'Publisher' -Value 'Chengshi'
    Set-ItemProperty -Path $arp -Name 'InstallLocation' -Value $installRoot
    Set-ItemProperty -Path $arp -Name 'DisplayIcon' -Value (Join-Path $appDir 'chengshi.ico')
    Set-ItemProperty -Path $arp -Name 'UninstallString' -Value ("powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$installRoot\uninstall.ps1`"")
    Set-ItemProperty -Path $arp -Name 'NoModify' -Value 1 -Type DWord
    Set-ItemProperty -Path $arp -Name 'NoRepair' -Value 1 -Type DWord
    Write-Host '已在「设置 → 应用」注册卸载入口。'
} catch {
    Write-Warning "注册卸载入口失败：$_（非致命）。"
}

Write-Host "`n✅ 安装完成。" -ForegroundColor Green
Write-Host "  - 程序目录 : $installRoot"
Write-Host "  - 守护服务 : $serviceName（自动启动 / LocalSystem / 孩子杀不掉）"
Write-Host "  - 配置数据 : %ProgramData%\Chengshi（孩子只读）"
Write-Host "  - 打开方式 : 桌面或开始菜单的“澄时”快捷方式，或运行 $appDir\Chengshi.App.exe"
