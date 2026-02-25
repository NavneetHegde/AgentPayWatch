# Launch AgentPayWatch Aspire dashboard (Windows only)
# Usage: .\aspire-run.ps1

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 1. Ensure Docker Desktop is running
if (-not (Get-Process 'Docker Desktop' -ErrorAction SilentlyContinue)) {
    Write-Host "Starting Docker Desktop..." -ForegroundColor Yellow
    Start-Process 'C:\Program Files\Docker\Docker\Docker Desktop.exe'

    $timeout = 60
    $elapsed = 0
    Write-Host "Waiting for Docker daemon..." -ForegroundColor Yellow
    while ($elapsed -lt $timeout) {
        $result = & docker info 2>&1
        if ($LASTEXITCODE -eq 0) {
            Write-Host "Docker is ready." -ForegroundColor Green
            break
        }
        Start-Sleep -Seconds 5
        $elapsed += 5
    }
    if ($elapsed -ge $timeout) {
        Write-Error "Docker did not become ready within $timeout seconds. Aborting."
        exit 1
    }
} else {
    Write-Host "Docker Desktop is already running." -ForegroundColor Green
}

# 2. Launch Aspire
Write-Host "Starting Aspire orchestration host..." -ForegroundColor Cyan
Set-Location $PSScriptRoot
aspire run .\appHost\apphost.cs
