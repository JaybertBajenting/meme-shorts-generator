# Meme Shorts Studio launcher.
# Starts the backend (which also serves the web UI) if it isn't already running,
# waits for it to come up, then opens the browser to it. Safe to run repeatedly —
# if the server is already up it just opens the browser.

$ErrorActionPreference = 'SilentlyContinue'
$api = Join-Path $PSScriptRoot 'src\MemeShorts.Api'
$url = 'http://localhost:5180'

function Up {
    try { Invoke-WebRequest "$url/api/health" -UseBasicParsing -TimeoutSec 2 | Out-Null; return $true }
    catch { return $false }
}

if (-not (Up)) {
    # Launch the server in its own minimized window (independent of this script).
    Start-Process powershell -ArgumentList @(
        '-NoExit', '-Command',
        "`$env:ASPNETCORE_ENVIRONMENT='Development'; Set-Location '$api'; dotnet run"
    ) -WindowStyle Minimized

    # Wait up to ~90s for it to finish starting (first run builds, so give it time).
    for ($i = 0; $i -lt 90; $i++) {
        if (Up) { break }
        Start-Sleep -Seconds 1
    }
}

Start-Process $url
