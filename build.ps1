<#
.SYNOPSIS
  Builds, tests and publishes VisualCronAI.
.DESCRIPTION
  dist\mcp\                 MCP server (framework-dependent, needs .NET 10 runtime) — point Claude Code / VS Code here
  dist\mcp\bridge\          VisualCron bridge (.NET Framework 4.8; loads VisualCronAPI.dll from the VisualCron install dir)
  dist\adf-runner\          VisualCronAI.AdfRunner.exe (self-contained single file) — copy to each VisualCron server
.EXAMPLE
  .\build.ps1                 # build + unit tests + publish
  .\build.ps1 -SkipTests
#>
param(
    [string]$Configuration = "Release",
    [string]$Output = "dist",
    [switch]$SkipTests
)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$dotnet = $null
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) {
    $sdks = & $cmd.Source --list-sdks 2>$null
    if ($sdks -match '^10\.') { $dotnet = $cmd.Source }
}
if (-not $dotnet) {
    $local = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    if (Test-Path $local) { $dotnet = $local } else { throw ".NET 10 SDK not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0" }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
Write-Host "Using $dotnet"

& $dotnet build VisualCronAI.sln -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

if (-not $SkipTests) {
    # DevIntegration tests run only when VISUALCRONAI_IT_DEV_ADDRESS is set.
    & $dotnet test VisualCronAI.sln -c $Configuration --no-build
    if ($LASTEXITCODE -ne 0) { throw "Tests failed" }
}

$out = Join-Path $PSScriptRoot $Output
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

& $dotnet publish src/VisualCronAI.Mcp -c $Configuration -o "$out\mcp" --no-self-contained
if ($LASTEXITCODE -ne 0) { throw "Publish MCP failed" }

& $dotnet publish src/VisualCronAI.Bridge -c $Configuration -o "$out\mcp\bridge"
if ($LASTEXITCODE -ne 0) { throw "Publish bridge failed" }

& $dotnet publish src/VisualCronAI.AdfRunner -c $Configuration -o "$out\adf-runner" -r win-x64 --self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) { throw "Publish ADF runner failed" }
Copy-Item src\VisualCronAI.AdfRunner\adf-runner.sample.json "$out\adf-runner\"

Write-Host ""
Write-Host "Published:" -ForegroundColor Green
Write-Host "  MCP server : $out\mcp\VisualCronAI.Mcp.exe"
Write-Host "  Bridge     : $out\mcp\bridge\VisualCronAI.Bridge.exe"
Write-Host "  ADF runner : $out\adf-runner\VisualCronAI.AdfRunner.exe (+ adf-runner.sample.json)"
