[CmdletBinding()]
param([string]$GamePath, [switch]$NoElevation, [switch]$NoPause)
$ErrorActionPreference = 'Stop'
Write-Host 'Goose Online uninstaller 0.4.2'
function Test-IsAdministrator { $identity = [Security.Principal.WindowsIdentity]::GetCurrent(); $principal = New-Object Security.Principal.WindowsPrincipal($identity); return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
if (-not (Test-IsAdministrator) -and -not $NoElevation) { $args = @('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath); if ($GamePath) { $args += @('-GamePath',$GamePath) }; Start-Process powershell.exe -Verb RunAs -ArgumentList $args | Out-Null; exit 0 }
function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    $known = @(${env:ProgramFiles(x86)}, ${env:ProgramFiles}, 'C:\Program Files (x86)', 'D:\Program Files (x86)', 'C:\Steam', 'D:\Steam')
    foreach ($base in $known) { if ($base) { $candidate = Join-Path $base 'Steam'; if (Test-Path $candidate) { $roots.Add($candidate) } } }
    foreach ($key in @('HKCU:\Software\Valve\Steam','HKLM:\SOFTWARE\WOW6432Node\Valve\Steam','HKLM:\SOFTWARE\Valve\Steam')) { try { $install = (Get-ItemProperty -Path $key -Name InstallPath -ErrorAction Stop).InstallPath; if ($install -and (Test-Path $install)) { $roots.Add($install) } } catch { } }
    return $roots | Select-Object -Unique
}
function Find-GamePath {
    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($steamRoot in Get-SteamRoots) {
        $libraries.Add($steamRoot)
        $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) { foreach ($line in Get-Content -LiteralPath $vdf) { if ($line -match '"path"\s+"([^"]+)"') { $path = $matches[1] -replace '\\\\', '\'; if (Test-Path $path) { $libraries.Add($path) } } } }
    }
    foreach ($library in ($libraries | Select-Object -Unique)) { $candidate = Join-Path $library 'steamapps\common\Untitled Goose Game'; if (Test-Path (Join-Path $candidate 'BepInEx\gooseonline.install.json')) { return (Resolve-Path $candidate).Path } }
    return $null
}
if (-not $GamePath) { $GamePath = Find-GamePath }
if (-not $GamePath) { $GamePath = Read-Host 'Paste the Untitled Goose Game folder path' }
$plugin = Join-Path $GamePath 'BepInEx\plugins\GooseOnline.dll'
$marker = Join-Path $GamePath 'BepInEx\gooseonline.install.json'
$updateDir = Join-Path $GamePath 'BepInEx\update'
if (Test-Path $plugin) { Remove-Item -LiteralPath $plugin -Force }
if (Test-Path $marker) { Remove-Item -LiteralPath $marker -Force }
foreach ($file in @('GooseOnline.dll.download', 'GooseOnline.version', 'GooseOnlineUpdater.ps1')) {
    $path = Join-Path $updateDir $file
    if (Test-Path $path) { Remove-Item -LiteralPath $path -Force }
}
Write-Host ''
Write-Host 'Goose Online uninstalled.' -ForegroundColor Green
Write-Host 'BepInEx was kept so other mods are not affected.'
if (-not $NoPause) { Read-Host 'Press Enter to close' }
