# Installs the BoSoFa desktop app from this server. It updates itself from the same server afterwards.
#   irm <server>/install/desktop.ps1 | iex
# Environment: BOSOFA_APP_DIR    where the app goes (default: %LOCALAPPDATA%\Programs\BoSoFa)
#              BOSOFA_NO_LAUNCH  set to anything to leave the app closed
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $server = '__BOSOFA_SERVER__'
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $rid = "win-$arch"
    $target = if ($env:BOSOFA_APP_DIR) { $env:BOSOFA_APP_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\BoSoFa' }
    $work = Join-Path ([IO.Path]::GetTempPath()) ("bosofa-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $file = Join-Path $work 'app.zip'
        Write-Host "Downloading the desktop app for $rid from $server"
        try { $response = Invoke-WebRequest -UseBasicParsing -Uri "$server/api/v1/downloads/desktop/latest/$rid" -OutFile $file -PassThru }
        catch { throw "This server has no desktop release for $rid." }
        $expected = [string]($response.Headers['X-BoSoFa-Sha256'] | Select-Object -First 1)
        $actual = (Get-FileHash -Algorithm SHA256 -Path $file).Hash.ToLowerInvariant()
        if ($actual -ne $expected) { throw "The download is damaged: its SHA-256 is $actual, expected $expected." }
        Expand-Archive -Path $file -DestinationPath $work
        if (Test-Path $target) {
            try { Remove-Item -Recurse -Force -Path $target }
            catch { throw "Close BoSoFa first: $target is in use." }
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Move-Item -Path (Join-Path $work 'BoSoFa') -Destination $target
        Write-Host "Installed in $target"
        $executable = Join-Path $target 'bosofa-desktop.exe'
        if (-not $env:BOSOFA_APP_DIR) {
            $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'BoSoFa.lnk'))
            $shortcut.TargetPath = $executable
            $shortcut.Save()
        }
        if (-not $env:BOSOFA_NO_LAUNCH) { Start-Process -FilePath $executable -ArgumentList '--server', $server }
    }
    finally { Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue }
}
