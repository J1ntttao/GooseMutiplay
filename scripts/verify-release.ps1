$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$release = Join-Path $workspace 'dist\GooseOnline-0.4.0.zip'
$testRoot = Join-Path $workspace ('dist\install-check-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $package = Join-Path $testRoot 'package with spaces'
    $game = Join-Path $testRoot 'game with spaces'
    New-Item -ItemType Directory -Path $game | Out-Null
    New-Item -ItemType File -Path (Join-Path $game 'Untitled.exe') | Out-Null
    Expand-Archive -LiteralPath $release -DestinationPath $package
    foreach ($script in @('install.ps1', 'uninstall.ps1')) {
        $parseTokens = $null
        $parseErrors = $null
        [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $package $script), [ref]$parseTokens, [ref]$parseErrors) | Out-Null
        if ($parseErrors) { throw ($parseErrors | Out-String) }
    }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'install.ps1') -GamePath $game -NoElevation -NoPause
    if ($LASTEXITCODE -ne 0) { throw 'Fresh installation failed' }
    $targetDll = Join-Path $game 'BepInEx\plugins\GooseOnline.dll'
    $expected = (Get-FileHash (Join-Path $workspace 'dist\GooseOnline.dll')).Hash
    if ((Get-FileHash $targetDll).Hash -ne $expected) { throw 'Installed DLL mismatch' }
    if (-not (Test-Path (Join-Path $game 'winhttp.dll'))) { throw 'Loader not installed' }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $package 'uninstall.ps1') -GamePath $game -NoElevation -NoPause
    if ($LASTEXITCODE -ne 0 -or (Test-Path $targetDll)) { throw 'Uninstallation failed' }
    # Standalone installer must restore its embedded DLL without sidecar DLL files.
    $standalone = Join-Path $testRoot 'standalone'
    New-Item -ItemType Directory -Path $standalone | Out-Null
    Copy-Item -LiteralPath (Join-Path $package 'install.ps1') -Destination $standalone
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $standalone 'install.ps1') -GamePath $game -NoElevation -NoPause
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash $targetDll).Hash -ne $expected) { throw 'Embedded DLL install failed' }
    Write-Host 'PASS: ZIP fresh install, loader, DLL hash, uninstall, embedded-only reinstall, paths with spaces, PS syntax.'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $allowed = [IO.Path]::GetFullPath((Join-Path $workspace 'dist')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
    $embeddedFile = Join-Path $env:TEMP 'GooseOnline-embedded.dll'
    if (Test-Path -LiteralPath $embeddedFile) { Remove-Item -LiteralPath $embeddedFile -Force }
}
