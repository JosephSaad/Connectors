<#
.SYNOPSIS
    Installs (or removes) the Altrata Copilot Connector as a Windows service.

.DESCRIPTION
    Registers the published connector executable with the Service Control
    Manager. The app detects service mode automatically — no extra flags needed.

    Deployment layout expected in -InstallDir (see README "Windows service"):
        AltrataConnector.exe   (dotnet publish output)
        config\    schema.json, graph-schema.json, seats.json
        env\       .env.local, .env.local.user
    logs\ and data\ are created there at runtime (ALTRATA_CONNECTOR_HOME).

.EXAMPLE
    # Publish, lay out, then install with the default continuous crawl:
    .\scripts\install-windows-service.ps1 -InstallDir "C:\AltrataConnector"

.EXAMPLE
    # Custom schedule / name:
    .\scripts\install-windows-service.ps1 -InstallDir "C:\AltrataConnector" `
        -Arguments "full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4" `
        -ServiceName "AltrataCopilotConnector"

.EXAMPLE
    .\scripts\install-windows-service.ps1 -ServiceName "AltrataCopilotConnector" -Uninstall
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "AltrataConnector",
    [string]$DisplayName = "Altrata Copilot Connector",
    [string]$InstallDir,
    [string]$Arguments = "full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4",
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"

if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated (Administrator) PowerShell session."
}

if ($Uninstall) {
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $svc) { Write-Host "Service '$ServiceName' is not installed."; exit 0 }
    if ($svc.Status -eq "Running") {
        Write-Host "Stopping '$ServiceName' (graceful — finishes the current chunk)..."
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(120))
    }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Service '$ServiceName' removed."
    exit 0
}

if (-not $InstallDir) { throw "-InstallDir is required (folder containing the published exe, config\ and env\)." }
$InstallDir = (Resolve-Path $InstallDir).Path
$exe = Join-Path $InstallDir "AltrataConnector.exe"

if (-not (Test-Path $exe)) {
    throw "Not found: $exe`nPublish first:  dotnet publish src/AltrataConnector -c Release -r win-x64 -o `"$InstallDir`""
}
foreach ($required in "config\graph-schema.json", "env\.env.local") {
    if (-not (Test-Path (Join-Path $InstallDir $required))) {
        throw "Missing $required in $InstallDir — copy config\ and env\ next to the exe."
    }
}

if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "Service '$ServiceName' already exists. Remove it first with -Uninstall."
}

Write-Host "Creating service '$ServiceName'..."
New-Service -Name $ServiceName `
    -BinaryPathName "`"$exe`" $Arguments" `
    -DisplayName $DisplayName `
    -Description "Microsoft 365 Copilot custom connector for Altrata relationship & wealth intelligence ($Arguments)" `
    -StartupType Automatic | Out-Null

# Resolve config/, env/, logs/, data/ against the install dir (services start in System32).
$envKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
Set-ItemProperty -Path $envKey -Name "Environment" `
    -Value ([string[]]@("ALTRATA_CONNECTOR_HOME=$InstallDir")) -Type MultiString

# Restart on crash: 30s, 60s, then every 5 min; reset the failure count daily.
sc.exe failure $ServiceName reset= 86400 actions= restart/30000/restart/60000/restart/300000 | Out-Null

Write-Host @"
Service installed.

  Name        : $ServiceName
  Command     : $Arguments
  Home        : $InstallDir  (ALTRATA_CONNECTOR_HOME)
  Startup     : Automatic, restart on crash
  Logs        : $InstallDir\logs\  (+ service events in the Windows Application log)

Start it with:  Start-Service $ServiceName
Stopping the service is graceful: the current chunk finishes, the pending
Graph batch is flushed and the checkpoint is saved, so the next start resumes
where it left off.
"@
