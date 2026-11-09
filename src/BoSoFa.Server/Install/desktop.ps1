# Installs the __BRAND_NAME__ desktop app from this server. It updates itself from the same server afterwards.
#   irm <server>/install/desktop.ps1 | iex
# Environment: BOSOFA_APP_DIR    where the app goes (default: %LOCALAPPDATA%\Programs\__BRAND_NAME__)
#              BOSOFA_NO_LAUNCH  set to anything to leave the app closed
& {
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $server = '__BOSOFA_SERVER__'
    $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
    $rid = "win-$arch"
    $target = if ($env:BOSOFA_APP_DIR) { $env:BOSOFA_APP_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\__BRAND_NAME__' }
    $work = Join-Path ([IO.Path]::GetTempPath()) ("__BRAND_ID__-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $file = Join-Path $work 'app.zip'
        Write-Host "Downloading the desktop app for $rid from $server"
        try { $response = Invoke-WebRequest -UseBasicParsing -Uri "$server/api/v1/downloads/desktop/latest/$rid" -OutFile $file -PassThru }
        catch { throw "This server has no desktop release for $rid." }
        $expected = [string]($response.Headers['X-BoSoFa-Sha256'] | Select-Object -First 1)
        $stream = [IO.File]::OpenRead($file)
        try { $actual = [BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose() }
        if ($actual -ne $expected) { throw "The download is damaged: its SHA-256 is $actual, expected $expected." }
        Expand-Archive -Path $file -DestinationPath $work
        if (Test-Path $target) {
            try { Remove-Item -Recurse -Force -Path $target }
            catch { throw "Close __BRAND_NAME__ first: $target is in use." }
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
        Move-Item -Path (Join-Path $work '__BRAND_NAME__') -Destination $target
        Write-Host "Installed in $target"
        $executable = Join-Path $target '__BRAND_ID__-desktop.exe'
        if (-not $env:BOSOFA_APP_DIR) {
            $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) '__BRAND_NAME__.lnk'))
            $shortcut.TargetPath = $executable
            $shortcut.Save()
        }
        if (-not $env:BOSOFA_NO_LAUNCH) { Start-Process -FilePath $executable -ArgumentList '--server', $server }
    }
    finally { Remove-Item -Recurse -Force -Path $work -ErrorAction SilentlyContinue }
}
