<#
.SYNOPSIS
    Installs (or removes) the Seismic Copilot Connector as a Windows service.

.DESCRIPTION
    Registers the published connector executable with the Service Control
    Manager. The app detects service mode automatically — no extra flags
    needed. On SCM stop the connector finishes the in-flight chunk, flushes
    the pending Graph batch and saves its checkpoint before exiting.

    Deployment layout expected in -InstallDir (see README "Windows service"):
        SeismicConnector.exe    (dotnet publish output)
        config\    schema.json, graph-schema.json, exclusions.json
        env\       .env.local, .env.local.user
    logs\ and data\ are created there at runtime (SEISMIC_CONNECTOR_HOME).

.EXAMPLE
    # Publish, lay out, then install with the default continuous crawl:
    .\scripts\install-windows-service.ps1 -InstallDir "C:\SeismicConnector"

.EXAMPLE
    # Custom schedule / name:
    .\scripts\install-windows-service.ps1 -InstallDir "C:\SeismicConnector" `
        -Arguments "full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4" `
        -ServiceName "SeismicCopilotConnector"

.EXAMPLE
    .\scripts\install-windows-service.ps1 -ServiceName "SeismicCopilotConnector" -Uninstall
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "SeismicConnector",
    [string]$DisplayName = "Seismic Copilot Connector",
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
$exe = Join-Path $InstallDir "SeismicConnector.exe"
if (-not (Test-Path $exe)) {
    throw "Executable not found: $exe`nPublish first:  dotnet publish src/SeismicConnector -c Release -r win-x64 -o `"$InstallDir`""
}
foreach ($required in @("config\schema.json", "config\graph-schema.json", "config\exclusions.json", "env\.env.local")) {
    if (-not (Test-Path (Join-Path $InstallDir $required))) {
        Write-Warning "Missing $required under $InstallDir — the service will fail to start until it exists."
    }
}

$binPath = "`"$exe`" $Arguments"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists — updating binPath..."
    if ($existing.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(120))
    }
    sc.exe config $ServiceName binPath= $binPath | Out-Null
}
else {
    New-Service -Name $ServiceName -DisplayName $DisplayName `
        -BinaryPathName $binPath -StartupType Automatic `
        -Description "Ingests Seismic sales-enablement content into Microsoft 365 Copilot via the Graph external connections API." | Out-Null
}

# The connector resolves config/, env/, logs/, data/ against SEISMIC_CONNECTOR_HOME.
[Environment]::SetEnvironmentVariable("SEISMIC_CONNECTOR_HOME", $InstallDir, "Machine")

# Windows Event Log source (EVENTLOG_ENABLED=true mirrors WARNING+ and lifecycle
# events to the Application log — docs/SIEM.md). Source creation needs admin,
# which this script already requires; the runtime service account then only
# needs to WRITE to an existing source. Idempotent: no-op when it exists.
if (-not [System.Diagnostics.EventLog]::SourceExists("SeismicConnector")) {
    New-EventLog -LogName Application -Source "SeismicConnector"
    Write-Host "Event Log source 'SeismicConnector' created (Application log)."
}
else {
    Write-Host "Event Log source 'SeismicConnector' already exists — unchanged."
}

# Restart policy: give transient failures three retries.
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/300000/restart/900000 | Out-Null

Write-Host "Service '$ServiceName' installed."
Write-Host "  binPath : $binPath"
Write-Host "  home    : $InstallDir  (SEISMIC_CONNECTOR_HOME)"
Write-Host "Start it with:  Start-Service $ServiceName"
