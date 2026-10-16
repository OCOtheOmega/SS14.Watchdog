param([Parameter(Mandatory = $true)][string] $GameRepository)
$ErrorActionPreference = 'Stop'
$gameRoot = (Resolve-Path -LiteralPath $GameRepository).Path
$executable = Join-Path $gameRoot 'bin/Content.Server/Content.Server.exe'
if (!(Test-Path -LiteralPath $executable)) { throw 'Build HeretekRT Content.Server first.' }
$smokeRoot = Join-Path $PSScriptRoot '../SS14.Watchdog.Tests/bin/game-countdown-smoke'
New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null
$smokeRoot = (Resolve-Path -LiteralPath $smokeRoot).Path
$runRoot = Join-Path $smokeRoot ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $runRoot | Out-Null
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$configPath = Join-Path $runRoot 'server.toml'
$dataPath = Join-Path $runRoot 'data'
$stdout = Join-Path $runRoot 'stdout.log'
$stderr = Join-Path $runRoot 'stderr.log'
$config = @"
[net]
port = $port
bindto = "127.0.0.1"
auth_mode = 0
[status]
enabled = true
bind = "127.0.0.1:$port"
[hub]
advertise = false
[update]
restart_delay = 3600.0
[game]
lobbyenabled = true
lobbyduration = 86400.0
[database]
engine = "sqlite"
sqlite_dbpath = "preferences.db"
"@
[System.IO.File]::WriteAllText($configPath, $config)
$oldToken = $env:ROBUST_CVAR_watchdog__token
$env:ROBUST_CVAR_watchdog__token = [Guid]::NewGuid().ToString('N')
$headers = @{ WatchdogToken = $env:ROBUST_CVAR_watchdog__token }
$address = "http://127.0.0.1:$port/watchdog/update"
$serverProcess = $null
function Send-Countdown($action, $requestId, $deadline) {
    $payload = @{ action = $action; requestId = $requestId }
    if ($null -ne $deadline) { $payload.restartAtUtc = $deadline }
    Invoke-RestMethod -Method Post -Uri $address -Headers $headers -ContentType 'application/json' -Body ($payload | ConvertTo-Json) -TimeoutSec 10
}
function Expect-Status([scriptblock] $operation, [int] $expected) {
    try { & $operation | Out-Null }
    catch {
        if ($null -ne $_.Exception.Response -and [int] $_.Exception.Response.StatusCode -eq $expected) { return }
        throw
    }
    throw "Expected HTTP $expected."
}
try {
    $arguments = @('--config-file', ('"' + $configPath + '"'), '--data-dir', ('"' + $dataPath + '"'))
    $serverProcess = Start-Process -FilePath $executable -WorkingDirectory (Split-Path $executable) -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    # Windows PowerShell must open the handle while the process is alive to retain its exit code.
    $processHandle = $serverProcess.Handle
    $readyBy = [DateTimeOffset]::UtcNow.AddSeconds(90)
    $status = $null
    while ([DateTimeOffset]::UtcNow -lt $readyBy) {
        if ($serverProcess.HasExited) { throw "Game exited during startup with code $($serverProcess.ExitCode)." }
        try {
            $status = Invoke-RestMethod -Uri $address -Headers $headers -TimeoutSec 2
            if ($status.ProtocolVersion -eq 1) { break }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    if ($null -eq $status -or $status.ProtocolVersion -ne 1) { throw 'Game countdown API did not become ready.' }
    Expect-Status { Invoke-WebRequest -UseBasicParsing -Uri $address -TimeoutSec 5 } 401
    Expect-Status { Invoke-WebRequest -UseBasicParsing -Uri $address -Headers @{ WatchdogToken = 'wrong-token' } -TimeoutSec 5 } 401
    $requestId = [Guid]::NewGuid().ToString()
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(10).ToString('O')
    $scheduled = Send-Countdown schedule $requestId $deadline
    if ($scheduled.RequestId -ne $requestId) { throw 'Schedule was not acknowledged.' }
    $retried = Send-Countdown schedule $requestId ([DateTimeOffset]::UtcNow.AddMinutes(20).ToString('O'))
    if ($retried.RestartAtUtc -ne $scheduled.RestartAtUtc) { throw 'A retry changed the announced deadline.' }
    Send-Countdown announce $requestId $null | Out-Null
    Expect-Status { Send-Countdown schedule ([Guid]::NewGuid().ToString()) $deadline } 409
    Expect-Status { Send-Countdown schedule ([Guid]::Empty.ToString()) $deadline } 400
    Send-Countdown cancel $requestId $null | Out-Null
    $status = Invoke-RestMethod -Uri $address -Headers $headers -TimeoutSec 5
    if ($null -ne $status.RequestId) { throw 'Cancellation left a pending countdown.' }
    $requestId = [Guid]::NewGuid().ToString()
    Send-Countdown schedule $requestId $deadline | Out-Null
    Send-Countdown shutdown $requestId $null | Out-Null
    if (!$serverProcess.WaitForExit(45000)) { throw 'Game did not shut down after its save barrier.' }
    if ($serverProcess.ExitCode -ne 0) { throw "Game shutdown failed with exit code $($serverProcess.ExitCode)." }
    if (!(Select-String -LiteralPath $stdout -SimpleMatch 'Persistent inventory shutdown barrier:' -Quiet)) {
        throw 'Game shutdown did not report its persistent inventory save barrier.'
    }
    Write-Output 'Game countdown HTTP smoke test passed: authentication, schedule, retry, reminder, conflict, cancel and graceful save barrier.'
} catch {
    if (Test-Path -LiteralPath $stdout) { Get-Content -LiteralPath $stdout -Tail 20 }
    if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -Tail 10 }
    throw
} finally {
    if ($null -ne $serverProcess -and !$serverProcess.HasExited) { $serverProcess.Kill(); $serverProcess.WaitForExit() }
    $env:ROBUST_CVAR_watchdog__token = $oldToken
}
