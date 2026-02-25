# Launch Aspire Dashboard

Windows-only command: ensures Docker Desktop is running, then starts the Aspire orchestration host.

Run the following steps using the Bash tool:

1. Check if Docker Desktop is running by querying its named pipe:
```bash
powershell -Command "if (Get-Process 'Docker Desktop' -ErrorAction SilentlyContinue) { Write-Output 'running' } else { Write-Output 'not running' }"
```

2. If Docker Desktop is **not running**, start it and wait for the Docker daemon to become ready:
```bash
powershell -Command "Start-Process 'C:\\Program Files\\Docker\\Docker\\Docker Desktop.exe'; Write-Output 'Docker Desktop launching...'"
```
Then poll until the Docker daemon responds (retry every 5 seconds, up to 60 seconds):
```bash
powershell -Command "
\$timeout = 60; \$elapsed = 0
while (\$elapsed -lt \$timeout) {
  try { docker info 2>\$null | Out-Null; Write-Output 'Docker is ready'; break }
  catch { Start-Sleep 5; \$elapsed += 5 }
}
if (\$elapsed -ge \$timeout) { Write-Output 'ERROR: Docker did not start in time'; exit 1 }
"
```

3. Launch the Aspire orchestration host:
```bash
aspire run .\appHost\apphost.cs
```

After running, inform the user that the Aspire dashboard should be available and display any output from the command.
