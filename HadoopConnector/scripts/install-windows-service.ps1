<#
.SYNOPSIS
    Installs (or removes) the BDH Hadoop Copilot Connector as a Windows service.

.DESCRIPTION
    Registers the published connector executable with the Service Control
    Manager. The app detects service mode automatically — no extra flags needed.
    An SCM stop is graceful: the in-flight chunk finishes, the pending Graph
    batch is flushed, and the checkpoint is saved, so the next start resumes
    where it left off.

    Deployment layout expected in -InstallDir (see README "Windows service"):
        HadoopConnector.exe    (dotnet publish output, .NET 10 / win-x64)
        config\    schema.json, graph-schema.json
        env\       .env.local, .env.local.user
    logs\ and data\ are created there at runtime (HADOOP_CONNECTOR_HOME).

.EXAMPLE
    # Publish, lay out, then install with the default continuous crawl:
    .\scripts\install-windows-service.ps1 -InstallDir "C:\HadoopConnector"

.EXAMPLE
    # Custom schedule / name:
    .\scripts\install-windows-service.ps1 -InstallDir "C:\HadoopConnector" `
        -Arguments "full-deployment --continuous --full-crawl-hours 24 --incremental-hours 4" `
        -ServiceName "HadoopCopilotConnector"

.EXAMPLE
    .\scripts\install-windows-service.ps1 -ServiceName "HadoopConnector" -Uninstall
#>
[CmdletBinding()]
param(
    [string]$ServiceName = "HadoopConnector",
    [string]$DisplayName = "BDH Hadoop Copilot Connector",
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
$exe = Join-Path $InstallDir "HadoopConnector.exe"
if (-not (Test-Path $exe)) { throw "Executable not found: $exe (run 'dotnet publish -c Release -r win-x64' first)." }
foreach ($required in @("config\schema.json", "config\graph-schema.json", "env\.env.local")) {
    if (-not (Test-Path (Join-Path $InstallDir $required))) {
        Write-Warning "Missing $required under $InstallDir — the service will fail to start until it exists."
    }
}

$binPath = "`"$exe`" $Arguments"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host "Service '$ServiceName' already exists — updating binary path."
    if ($existing.Status -eq "Running") {
        Stop-Service -Name $ServiceName -Force
        $existing.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(120))
    }
    sc.exe config $ServiceName binPath= $binPath start= auto | Out-Null
}
else {
    New-Service -Name $ServiceName -DisplayName $DisplayName `
        -BinaryPathName $binPath -StartupType Automatic `
        -Description "Ingests BDH Hadoop data-mart (Salesforce-shaped) records into Microsoft 365 Copilot / Microsoft Search." | Out-Null
}

# Resolve relative paths (config/, env/, logs/, data/) against the install dir.
[Environment]::SetEnvironmentVariable("HADOOP_CONNECTOR_HOME", $InstallDir, "Machine")
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

Write-Host "Service '$ServiceName' installed."
Write-Host "  Binary : $binPath"
Write-Host "  Home   : $InstallDir  (HADOOP_CONNECTOR_HOME)"
Write-Host "Start it with:  Start-Service $ServiceName"
