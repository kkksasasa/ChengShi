$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dotnetRoot = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet"
$dotnet = Join-Path $dotnetRoot "dotnet.exe"
if (-not (Test-Path $dotnet)) {
    $dotnet = "dotnet"
} else {
    $env:DOTNET_ROOT = $dotnetRoot
    $env:PATH = "$dotnetRoot;$env:PATH"
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
Set-Location $root
& $dotnet build Chengshi.slnx -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& $dotnet run --project (Join-Path $root "src\Chengshi.App\Chengshi.App.csproj") -c Release --no-build
