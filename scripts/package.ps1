# 澄时(Chengshi) 打包脚本：产出「双击即装」的单文件安装器。
#
# 流程：
#   1. 以文件夹形态发布 App / Service（自带运行时；单文件形态会把
#      startup.vbs / chengshi.ico 这类 Content 打进 exe，影响自启与快捷方式图标）
#   2. 组装 payload（install.ps1 / uninstall.ps1 / check-guard.ps1 / 版本信息 / 发布产物）
#   3. zip 后作为内嵌资源编译进 Chengshi.Setup，得到 dist\ChengshiSetup.exe
#   4. 另打一个 dist\Chengshi-Setup-v版本.zip（安装器 + EULA + 隐私政策）
#
# 用法：powershell -File scripts\package.ps1 [-RunTests]
param(
    [switch]$RunTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
else { $env:DOTNET_ROOT = Split-Path -Parent $dotnet; $env:PATH = "$(Split-Path -Parent $dotnet);$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

# 版本号：安装器、「应用和功能」条目、产物命名都从这里来。
$version = "1.0.0"
$propsFile = Join-Path $root "Directory.Build.props"
if (Test-Path $propsFile) {
    $m = Select-String -Path $propsFile -Pattern "<Version>([^<]+)</Version>" | Select-Object -First 1
    if ($m) { $version = $m.Matches[0].Groups[1].Value }
}
Write-Host "== 澄时打包 v$version ==" -ForegroundColor Cyan

if ($RunTests) {
    Write-Host "== 运行测试 ==" -ForegroundColor Cyan
    & $dotnet test Chengshi.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw "测试失败，中止打包。" }
}

function Publish-Folder($project, $outDir) {
    if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }
    & $dotnet publish $project `
        -c Release -r win-x64 --self-contained true `
        -p:DebugType=None -p:DebugSymbols=false `
        -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "发布失败：$project" }
}

Write-Host "== 发布 App（文件夹形态）==" -ForegroundColor Cyan
Publish-Folder "src\Chengshi.App\Chengshi.App.csproj" "$root\artifacts\publish\folder\app"
Write-Host "== 发布 Service（文件夹形态）==" -ForegroundColor Cyan
Publish-Folder "src\Chengshi.Service\Chengshi.Service.csproj" "$root\artifacts\publish\folder\service"

# 文件夹形态下这两个散文件是自启与快捷方式图标的前提，缺了就别打包。
$appPub = "$root\artifacts\publish\folder\app"
foreach ($must in @("Chengshi.App.exe", "startup.vbs", "chengshi.ico")) {
    if (-not (Test-Path (Join-Path $appPub $must))) { throw "发布产物缺 $must，无法继续。" }
}
$svcPub = "$root\artifacts\publish\folder\service"
if (-not (Test-Path (Join-Path $svcPub "Chengshi.Service.exe"))) { throw "发布产物缺 Chengshi.Service.exe，无法继续。" }

# ---- 组装 payload ----
Write-Host "== 组装 payload ==" -ForegroundColor Cyan
$stage = "$root\artifacts\payload_stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path "$stage\publish" | Out-Null

Copy-Item "$root\tools\install.ps1" "$stage\install.ps1" -Force
Copy-Item "$root\scripts\uninstall.ps1" "$stage\uninstall.ps1" -Force
if (Test-Path "$root\tools\check-guard.ps1") {
    Copy-Item "$root\tools\check-guard.ps1" "$stage\check-guard.ps1" -Force
}
Copy-Item $propsFile "$stage\Directory.Build.props" -Force
Copy-Item "$appPub" "$stage\publish\app" -Recurse
Copy-Item "$svcPub" "$stage\publish\service" -Recurse

# payload 里不该有调试符号。
Get-ChildItem $stage -Recurse -Include *.pdb | Remove-Item -Force

# ---- 语法闸门：payload 脚本必须能被 Windows PowerShell 5.1 解析 ----
# 安装器用 powershell.exe（5.1）执行 payload 脚本；?. / ?? / 三元这类 7.0 语法
# 在解析阶段就会炸掉整个安装。这里显式用 5.1 的解析器逐个过一遍（即使本打包
# 脚本本身跑在 pwsh 7 上，闸门也不会失守）。
Write-Host "== 校验 payload 脚本语法（PowerShell 5.1 兼容）==" -ForegroundColor Cyan
foreach ($script in @("$stage\install.ps1", "$stage\uninstall.ps1", "$stage\check-guard.ps1")) {
    if (-not (Test-Path $script)) { continue }
    # 路径经环境变量传入，命令体里不带任何引号，避免原生参数传递把命令截断。
    $env:CS_PARSE_TARGET = $script
    & powershell.exe -NoProfile -Command '& { $t=$null; $e=$null; $null=[System.Management.Automation.Language.Parser]::ParseFile($env:CS_PARSE_TARGET,[ref]$t,[ref]$e); if ($e.Count -gt 0) { $e | ForEach-Object { Write-Host $_.Message }; exit 1 } exit 0 }' | Out-Host
    Remove-Item Env:CS_PARSE_TARGET -ErrorAction SilentlyContinue
    if ($LASTEXITCODE -ne 0) {
        throw "脚本不是 PowerShell 5.1 兼容语法：$script"
    }
    Write-Host "  OK $(Split-Path -Leaf $script)"
}

# ---- 压缩成内嵌资源 ----
Write-Host "== 压缩 payload.zip ==" -ForegroundColor Cyan
New-Item -ItemType Directory -Force -Path "$root\artifacts\setup" | Out-Null
$payloadZip = "$root\artifacts\setup\payload.zip"
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
if (Get-Command Compress-Archive -ErrorAction SilentlyContinue) {
    Compress-Archive -Path "$stage\*" -DestinationPath $payloadZip -CompressionLevel Optimal
} else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $payloadZip)
}
$zipMb = [math]::Round((Get-Item $payloadZip).Length / 1MB, 1)
Write-Host "payload.zip: $zipMb MB"

# ---- 编译单文件安装器 ----
Write-Host "== 编译 ChengshiSetup.exe ==" -ForegroundColor Cyan
& $dotnet publish "src\Chengshi.Setup\Chengshi.Setup.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -p:Version=$version `
    -o "$root\dist"
if ($LASTEXITCODE -ne 0) { throw "安装器编译失败。" }

# ---- 附一个分发包：安装器 + 法律文件 ----
Write-Host "== 组装分发 zip ==" -ForegroundColor Cyan
$distZip = "$root\dist\Chengshi-Setup-v$version.zip"
if (Test-Path $distZip) { Remove-Item $distZip -Force }
$bundle = "$root\artifacts\setup\bundle"
if (Test-Path $bundle) { Remove-Item $bundle -Recurse -Force }
New-Item -ItemType Directory -Force -Path $bundle | Out-Null
Copy-Item "$root\dist\ChengshiSetup.exe" $bundle -Force
foreach ($doc in @("EULA.md", "PRIVACY.md", "README.md", "CHANGELOG.md")) {
    if (Test-Path "$root\$doc") { Copy-Item "$root\$doc" $bundle -Force }
}
Compress-Archive -Path "$bundle\*" -DestinationPath $distZip -CompressionLevel Optimal
Remove-Item $bundle -Recurse -Force

Write-Host ""
Write-Host "✅ 打包完成 v$version" -ForegroundColor Green
Write-Host "  安装器 : $root\dist\ChengshiSetup.exe（$([math]::Round((Get-Item "$root\dist\ChengshiSetup.exe").Length / 1MB, 1)) MB，双击即装）"
Write-Host "  分发包 : $distZip"
Write-Host "  提示   : 正式分发前请用 signtool 给 ChengshiSetup.exe 签名（见 scripts\publish.ps1 说明）。"
