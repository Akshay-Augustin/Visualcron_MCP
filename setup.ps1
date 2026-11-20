<#
.SYNOPSIS
  One-time setup after cloning: checks prerequisites, builds, creates your per-user config and registers the MCP server
  for Claude Code (.mcp.json) in this folder.
.DESCRIPTION
  This script never asks for, reads or stores a password. You store your VisualCron login yourself with cmdkey
  (the command is printed at the end).
.EXAMPLE
  .\setup.ps1
  .\setup.ps1 -SkipTests
#>
param([switch]$SkipTests)
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Ok($m)   { Write-Host "  [ok]   $m" -ForegroundColor Green }
function Warn($m) { Write-Host "  [todo] $m" -ForegroundColor Yellow }

Write-Host "1. Prerequisites"
$vcDir = "C:\Program Files (x86)\VisualCron"
if (Test-Path (Join-Path $vcDir "VisualCronAPI.dll")) { Ok "VisualCron client/API found in $vcDir" }
else { throw "VisualCronAPI.dll not found in $vcDir. Install the VisualCron 12 client on this PC first." }

$sdkOk = $false
foreach ($d in @((Get-Command dotnet -ErrorAction SilentlyContinue).Source, (Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"))) {
    if ($d -and (Test-Path $d) -and ((& $d --list-sdks 2>$null) -match '^10\.')) { $sdkOk = $true }
}
if ($sdkOk) { Ok ".NET 10 SDK found" }
else { throw ".NET 10 SDK not found. Install it:  winget install Microsoft.DotNet.SDK.10   (then open a new terminal)" }

Write-Host "2. Build, test, publish"
if ($SkipTests) { & .\build.ps1 -SkipTests } else { & .\build.ps1 }
Ok "dist\mcp\VisualCronAI.Mcp.exe"

Write-Host "3. Per-user config"
$cfgDir = Join-Path $env:APPDATA "VisualCronAI"
$cfg = Join-Path $cfgDir "appsettings.json"
if (Test-Path $cfg) { Ok "$cfg already exists (left unchanged)" }
else {
    New-Item -ItemType Directory -Force $cfgDir | Out-Null
    Copy-Item config\appsettings.user.sample.json $cfg
    Warn "Created $cfg from the sample. Edit it and replace every <...> placeholder."
}
if ((Get-Content $cfg -Raw) -match '"<[a-z][a-z0-9.\-]*>"') { Warn "$cfg still contains <...> placeholders." }

Write-Host "4. Register with Claude Code"
$exe = (Join-Path $PSScriptRoot "dist\mcp\VisualCronAI.Mcp.exe") -replace '\\', '/'
$mcp = [ordered]@{ mcpServers = [ordered]@{ visualcron = [ordered]@{ type = "stdio"; command = $exe; args = @() } } }
[IO.File]::WriteAllText((Join-Path $PSScriptRoot ".mcp.json"), ($mcp | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding $false))
Ok ".mcp.json -> $exe"

Write-Host ""
Write-Host "Remaining steps (do these yourself, never paste a password into an AI chat):" -ForegroundColor Cyan
Write-Host "  a) Store your VisualCron login (cmdkey prompts for the password):"
Write-Host "       cmdkey /generic:VisualCronAI/VCSERVER /user:<DOMAIN\your.user> /pass"
Write-Host "     (or set AuthMode to AD in the config if your Windows logon is a VisualCron user)"
Write-Host "  b) Connect to the VPN/Zscaler if the VisualCron server is only reachable through it."
Write-Host "  c) Start Claude Code in this folder, approve the 'visualcron' MCP server, then ask: 'test the VisualCron connection for DEV'."
