# run-infra-tests.ps1
# Runs infrastructure tests against the Cosmos emulator started by Aspire.
# Usage: .\run-infra-tests.ps1 [extra dotnet test args]

$ErrorActionPreference = 'Stop'

# Docker Desktop on Windows binds to 0.0.0.0; Linux Docker binds to 127.0.0.1.
$port = docker ps --format "{{.Ports}}" |
    Select-String -Pattern '(?:127\.0\.0\.1|0\.0\.0\.0):(\d+)->8081' |
    ForEach-Object { $_.Matches[0].Groups[1].Value } |
    Select-Object -First 1

if (-not $port) {
    Write-Error "ERROR: Cosmos emulator is not running.`nStart Aspire first: aspire run .\appHost\apphost.cs"
    exit 1
}

Write-Host "Cosmos emulator detected on port $port"
$env:COSMOS_CONNECTION_STRING = "AccountEndpoint=https://localhost:${port}/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw=="

dotnet test tests/AgentPayWatch.Infrastructure.Tests/AgentPayWatch.Infrastructure.Tests.csproj @args
