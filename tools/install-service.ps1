# 安装澄时守护服务（需以管理员身份运行）
# 把 Chengshi.Service 注册为自动启动的 LocalSystem 服务：开机自启、孩子杀不掉。
$ErrorActionPreference = 'Stop'
$serviceName = 'Chengshi'
$displayName = '澄时守护服务 (Chengshi Guardian)'

function Find-ServiceExe {
    $candidates = @(
        '..\src\Chengshi.Service\bin\Release\net10.0-windows\win-x64\Chengshi.Service.exe',
        '..\src\Chengshi.Service\bin\Release\net10.0-windows\Chengshi.Service.exe',
        '..\src\Chengshi.Service\bin\Debug\net10.0-windows\win-x64\Chengshi.Service.exe',
        '..\src\Chengshi.Service\bin\Debug\net10.0-windows\Chengshi.Service.exe'
    )
    foreach ($rel in $candidates) {
        $p = Join-Path $PSScriptRoot $rel
        if (Test-Path $p) { return (Resolve-Path $p).Path }
    }
    $found = Get-ChildItem (Join-Path $PSScriptRoot '..\src\Chengshi.Service') -Recurse -Filter Chengshi.Service.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.FullName }
    throw '找不到 Chengshi.Service.exe，请先构建 Chengshi.Service 项目。'
}

$exe = Find-ServiceExe
Write-Host "使用服务程序：$exe"

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    & sc.exe stop $serviceName | Out-Null
    & sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

& sc.exe create $serviceName binPath= "`"$exe`"" start= auto obj= LocalSystem DisplayName= $displayName
if ($LASTEXITCODE -ne 0) { throw "sc create 失败（退出码 $LASTEXITCODE）。" }
& sc.exe description $serviceName "澄时家长守护：屏幕时间管控、进程/网络/网站拦截，开机自启、防强杀。"
& sc.exe start $serviceName
Write-Host '已安装并启动澄时守护服务。'
