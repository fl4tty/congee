# Builds congee-setup-<version>.exe into installer\Output.
#   1. publishes the .NET edition (dotnet\publish\congee.exe)
#   2. publishes the sensor service (sensors\publish\congee-sensors.exe, used by both editions)
#   3. compiles installer\congee.iss with Inno Setup 6
# Needs: .NET 10 SDK (winget install Microsoft.DotNet.SDK.10)
#        Inno Setup 6  (winget install JRSoftware.InnoSetup)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# Pick up tools installed since this shell started
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

Write-Host '== Publishing the .NET edition'
Push-Location (Join-Path $root 'dotnet')
try {
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
} finally { Pop-Location }

Write-Host '== Publishing the sensor service'
Push-Location (Join-Path $root 'sensors')
try {
    if (Test-Path publish) { Remove-Item -Recurse -Force publish }
    dotnet publish -c Release -o publish
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (sensors) failed' }
    Remove-Item publish\*.pdb -ErrorAction SilentlyContinue
} finally { Pop-Location }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

Write-Host '== Compiling the installer'
& $iscc /Q (Join-Path $PSScriptRoot 'congee.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }

Get-ChildItem (Join-Path $PSScriptRoot 'Output') -Filter *.exe | Sort-Object LastWriteTime | Select-Object -Last 1 |
    ForEach-Object { Write-Host "== Built $($_.FullName) ($([math]::Round($_.Length / 1MB, 1)) MB)" }
