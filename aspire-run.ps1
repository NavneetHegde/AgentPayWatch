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

# 2. Launch Aspire, watch output for the dashboard login URL, then open browser
Write-Host "Starting Aspire orchestration host..." -ForegroundColor Cyan
Set-Location $PSScriptRoot

$urlLaunched = $false
$partialUrl = $null
& aspire run .\appHost\apphost.cs 2>&1 | ForEach-Object {
    Write-Host $_
    if (-not $urlLaunched) {
        if ($null -ne $partialUrl) {
            # Previous line had a partial URL — check if this line is a wrapped token continuation
            $trimmed = $_.Trim()
            $dashboardUrl = if ($trimmed -match '^[\w\-+=]+$') { $partialUrl + $trimmed } else { $partialUrl }
            $partialUrl = $null
            Write-Host "Opening Aspire dashboard: $dashboardUrl" -ForegroundColor Green
            Start-Process $dashboardUrl
            $urlLaunched = $true
        } elseif ($_ -match '(https?://[\w.:\-]+/login\?t=[\w\-+=]+)') {
            $partialUrl = $Matches[1]
        }
    }
}
