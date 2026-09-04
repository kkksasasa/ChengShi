param(
    [string]$Runtime = "win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
else { $env:DOTNET_ROOT = Split-Path -Parent $dotnet; $env:PATH = "$(Split-Path -Parent $dotnet);$env:PATH" }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

if (-not $SkipTests) {
    Write-Host "== running tests ==" -ForegroundColor Cyan
    & $dotnet test Chengshi.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; aborting publish." }
}

Write-Host "== publishing App (self-contained single file) ==" -ForegroundColor Cyan
& $dotnet publish "src\Chengshi.App\Chengshi.App.csproj" `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o "artifacts\publish\app"
if ($LASTEXITCODE -ne 0) { throw "App publish failed." }

Write-Host "== publishing Service (self-contained single file) ==" -ForegroundColor Cyan
& $dotnet publish "src\Chengshi.Service\Chengshi.Service.csproj" `
    -c Release -r $Runtime --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false `
    -o "artifacts\publish\service"
if ($LASTEXITCODE -ne 0) { throw "Service publish failed." }

# ---- 代码签名（商用分发强烈建议）----
# 设置环境变量 CHENGSHI_SIGN_PFX（PFX 证书路径）和 CHENGSHI_SIGN_PWD（证书密码）后，
# 发布产物会自动用 signtool 签名；未设置时跳过并给出提示。
# 未签名的 195MB 级安装包会被 SmartScreen 和杀软重点关照，正式售卖前请务必签名。
$signPfx = $env:CHENGSHI_SIGN_PFX
if (-not [string]::IsNullOrWhiteSpace($signPfx) -and (Test-Path $signPfx)) {
    $signtool = Get-Command signtool -ErrorAction SilentlyContinue
    if (-not $signtool) {
        $sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
        $candidate = Get-ChildItem $sdkRoot -Filter "signtool.exe" -Recurse -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($candidate) { $signtool = $candidate.FullName }
    }

    if (-not $signtool) {
        Write-Warning "找不到 signtool.exe，跳过签名。请安装 Windows SDK。"
    } else {
        $signPwd = $env:CHENGSHI_SIGN_PWD
        $tsa = "http://timestamp.digicert.com"
        foreach ($exe in @("artifacts\publish\app\Chengshi.App.exe", "artifacts\publish\service\Chengshi.Service.exe")) {
            Write-Host "== signing $exe ==" -ForegroundColor Cyan
            & $signtool sign /f $signPfx /p $signPwd /fd SHA256 /td SHA256 /tr $tsa $exe
            if ($LASTEXITCODE -ne 0) { throw "签名失败：$exe" }
        }
    }
} else {
    Write-Warning "未设置 CHENGSHI_SIGN_PFX，产物未签名（分发给客户前请签名，否则触发 SmartScreen）。"
}

Write-Host ""
Write-Host "Publish complete:" -ForegroundColor Green
Write-Host "  App:     $root\artifacts\publish\app\Chengshi.App.exe"
Write-Host "  Service: $root\artifacts\publish\service\Chengshi.Service.exe"
Write-Host "Next: run scripts\install.ps1 as Administrator"
