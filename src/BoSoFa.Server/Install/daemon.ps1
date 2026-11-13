# Installs the __BRAND_NAME__ daemon from this server and makes this computer one of its machines.
#   irm <server>/install/daemon.ps1 | iex
# Environment: BOSOFA_DATA_DIR      the daemon's data folder
#              BOSOFA_INSTALL_ARGS  more arguments for '__BRAND_DAEMON__ install', such as: --scope system
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $server = '__BOSOFA_SERVER__'
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $rid = "win-$arch"
    $work = Join-Path ([IO.Path]::GetTempPath()) ("__BRAND_ID__-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $file = Join-Path $work '__BRAND_DAEMON__.exe'
        Write-Host "Downloading the daemon for $rid from $server"
        try { $response = Invoke-WebRequest -UseBasicParsing -Uri "$server/api/v1/downloads/daemon/$rid" -OutFile $file -PassThru }
        catch { throw "This server carries no daemon for $rid." }
        $expected = [string]($response.Headers['X-BoSoFa-Sha256'] | Select-Object -First 1)
        $stream = [IO.File]::OpenRead($file)
        try { $actual = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($actual -ne $expected) { throw "The download is damaged: its SHA-256 is $actual, expected $expected." }

        $data = if ($env:BOSOFA_DATA_DIR) { @('--data-dir', $env:BOSOFA_DATA_DIR) } else { @() }
        $extra = if ($env:BOSOFA_INSTALL_ARGS) { $env:BOSOFA_INSTALL_ARGS -split ' +' } else { @() }
        & $file enroll --server $server @data
        if ($LASTEXITCODE -ne 0) { throw 'Enrolling this computer failed.' }
        & $file install @data @extra
        if ($LASTEXITCODE -ne 0) { throw 'Installing the service failed.' }
    }
    finally { Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue }
}
