# ============================================================================
# Mosart TCP Client Test Script
# Usage: .\test-mosart.ps1 [-Host localhost] [-Port 5555]
# ============================================================================

param(
    [string]$TcpHost = "127.0.0.1",
    [int]$Port = 5555
)

Write-Host "=== Mosart TCP Test Client ===" -ForegroundColor Cyan
Write-Host "Connecting to ${TcpHost}:${Port}..."

try {
    $client = New-Object System.Net.Sockets.TcpClient($TcpHost, $Port)
    $stream = $client.GetStream()
    $reader = New-Object System.IO.StreamReader($stream)
    $writer = New-Object System.IO.StreamWriter($stream)
    $writer.AutoFlush = $true

    Write-Host "Connected!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Commands:" -ForegroundColor Yellow
    Write-Host "  cue <GUID>  - Load template (sends GUID|0)"
    Write-Host "  play        - Play current (sends GUID|1)"
    Write-Host "  stop        - Stop current (sends GUID|2)"
    Write-Host "  raw <msg>   - Send raw message"
    Write-Host "  quit        - Exit"
    Write-Host ""

    $lastGuid = "00000000-0000-0000-0000-000000000000"

    while ($true) {
        Write-Host -NoNewline ">> " -ForegroundColor Cyan
        $input = Read-Host

        if ([string]::IsNullOrWhiteSpace($input)) { continue }

        $command = $input.Trim().ToLower()

        if ($command -eq "quit" -or $command -eq "exit") {
            break
        }

        $message = ""

        if ($command.StartsWith("cue ")) {
            $lastGuid = $command.Substring(4).Trim()
            $message = "$lastGuid|0"
        }
        elseif ($command -eq "play") {
            $message = "$lastGuid|1"
        }
        elseif ($command -eq "stop") {
            $message = "$lastGuid|2"
        }
        elseif ($command.StartsWith("raw ")) {
            $message = $command.Substring(4)
        }
        else {
            $message = $input
        }

        Write-Host "Sending: $message" -ForegroundColor DarkGray
        $writer.WriteLine($message)

        # Wait for response
        Start-Sleep -Milliseconds 100

        if ($stream.DataAvailable) {
            $response = $reader.ReadLine()
            Write-Host "<< $response" -ForegroundColor Green
        }
    }
}
catch {
    Write-Host "Error: $_" -ForegroundColor Red
}
finally {
    if ($reader) { $reader.Dispose() }
    if ($writer) { $writer.Dispose() }
    if ($stream) { $stream.Dispose() }
    if ($client) { $client.Dispose() }
    Write-Host "Disconnected." -ForegroundColor Yellow
}
