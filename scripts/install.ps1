[CmdletBinding()]
param(
    [string]$GamePath,
    [switch]$NoElevation, [switch]$NoPause
)
$ErrorActionPreference = 'Stop'
Write-Host 'Goose Online installer 0.4.0'
$EmbeddedPluginBase64 = '__EMBEDDED_PLUGIN_BASE64__'
$PackageRoot = $PSScriptRoot
$MarkerName = 'gooseonline.install.json'
function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function Find-PackageFile([string]$Name) {
    $candidates = @(
        (Join-Path $PSScriptRoot (Join-Path 'payload' $Name)),
        (Join-Path $PSScriptRoot $Name),
        (Join-Path (Split-Path -Parent $PSScriptRoot) (Join-Path 'payload' $Name)),
        (Join-Path (Split-Path -Parent $PSScriptRoot) $Name),
        (Join-Path (Get-Location) $Name),
        (Join-Path (Split-Path -Parent (Get-Location)) $Name)
    )
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return (Resolve-Path -LiteralPath $candidate).Path }
    }
    return $null
}
if (-not (Test-IsAdministrator) -and -not $NoElevation) {
    $elevatedArguments = '-NoProfile -ExecutionPolicy Bypass -File "' + $PSCommandPath + '"'
    if ($GamePath) { $elevatedArguments += ' -GamePath "' + $GamePath + '"' }
    $elevatedProcess = Start-Process powershell.exe -Verb RunAs -ArgumentList $elevatedArguments -Wait -PassThru
    exit $elevatedProcess.ExitCode
}
function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    $known = @(${env:ProgramFiles(x86)}, ${env:ProgramFiles}, 'C:\Program Files (x86)', 'D:\Program Files (x86)', 'C:\Steam', 'D:\Steam')
    foreach ($base in $known) {
        if ($base) { $candidate = Join-Path $base 'Steam'; if (Test-Path $candidate) { $roots.Add($candidate) } }
    }
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
        try { $install = (Get-ItemProperty -Path $key -Name InstallPath -ErrorAction Stop).InstallPath; if ($install -and (Test-Path $install)) { $roots.Add($install) } } catch { }
    }
    return $roots | Select-Object -Unique
}
function Get-SteamLibraryPaths {
    $libraries = New-Object System.Collections.Generic.List[string]
    foreach ($steamRoot in Get-SteamRoots) {
        $libraries.Add($steamRoot)
        $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            foreach ($line in Get-Content -LiteralPath $vdf) {
                if ($line -match '"path"\s+"([^"]+)"') { $path = $matches[1] -replace '\\\\', '\'; if (Test-Path $path) { $libraries.Add($path) } }
            }
        }
    }
    return $libraries | Select-Object -Unique
}
function Find-GamePath {
    $candidates = New-Object System.Collections.Generic.List[string]
    foreach ($library in Get-SteamLibraryPaths) { $candidates.Add((Join-Path $library 'steamapps\common\Untitled Goose Game')) }
    $candidates.Add('C:\Program Files (x86)\Steam\steamapps\common\Untitled Goose Game')
    $candidates.Add('D:\Program Files (x86)\Steam\steamapps\common\Untitled Goose Game')
    foreach ($candidate in ($candidates | Select-Object -Unique)) { if (Test-Path (Join-Path $candidate 'Untitled.exe')) { return (Resolve-Path $candidate).Path } }
    return $null
}
function Wait-ForGameExit {
    $deadline = (Get-Date).AddSeconds(60)
    while ($true) {
        $running = @(Get-Process -Name 'Untitled' -ErrorAction SilentlyContinue)
        if ($running.Count -eq 0) { return }
        if ((Get-Date) -ge $deadline) {
            throw 'Untitled.exe is still running. Close the game completely, then run the installer again.'
        }
        Write-Host 'Untitled.exe is running. Close the game; waiting for it to exit...' -ForegroundColor Yellow
        Start-Sleep -Seconds 2
    }
}
function Copy-FileWithRetry([string]$Source, [string]$Destination) {
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force
            return
        } catch {
            if ($attempt -eq 10) {
                throw "Unable to replace GooseOnline.dll. Close Untitled Goose Game and temporarily pause antivirus scanning, then retry. Details: $($_.Exception.Message)"
            }
            Start-Sleep -Milliseconds 750
        }
    }
}
if (-not $GamePath) { $GamePath = Find-GamePath }
if (-not $GamePath) { $GamePath = Read-Host 'Game not found. Paste the game folder path' }
Write-Host "Game folder: $GamePath"
if (-not (Test-Path (Join-Path $GamePath 'Untitled.exe'))) { throw "Untitled.exe was not found: $GamePath" }
Wait-ForGameExit
$sourcePlugin = Find-PackageFile 'GooseOnline.dll'
if (-not $sourcePlugin -or -not (Test-Path -LiteralPath $sourcePlugin -PathType Leaf)) {
    if ($EmbeddedPluginBase64 -eq '__EMBEDDED_PLUGIN_BASE64__') { throw 'The package is missing GooseOnline.dll' }
    $sourcePlugin = Join-Path $env:TEMP 'GooseOnline-embedded.dll'
    [IO.File]::WriteAllBytes($sourcePlugin, [Convert]::FromBase64String($EmbeddedPluginBase64))
}
$bepRoot = Join-Path $GamePath 'BepInEx'
$bepCore = Join-Path $bepRoot 'core\BepInEx.dll'
$installedLoader = -not (Test-Path $bepCore)
if ($installedLoader) {
    $bepZip = Find-PackageFile 'BepInEx_win_x64_5.4.23.3.zip'
    if (-not $bepZip -or -not (Test-Path -LiteralPath $bepZip -PathType Leaf)) {
        Write-Host 'BepInEx dependency not found in the package. Downloading from the official release...'
        $downloadUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.3/BepInEx_win_x64_5.4.23.3.zip'
        try {
            $bepZip = Join-Path $env:TEMP 'BepInEx_win_x64_5.4.23.3.zip'
            Invoke-WebRequest -Uri $downloadUrl -OutFile $bepZip -UseBasicParsing
        } catch {
            throw "BepInEx download failed. Please resend the complete ZIP package or check internet access. Details: $($_.Exception.Message)"
        }
    }
    Expand-Archive -LiteralPath $bepZip -DestinationPath $GamePath -Force
}
$plugins = Join-Path $bepRoot 'plugins'
New-Item -ItemType Directory -Force $plugins | Out-Null
Copy-FileWithRetry -Source $sourcePlugin -Destination (Join-Path $plugins 'GooseOnline.dll')
$marker = [ordered]@{ mod = 'GooseOnline'; version = '0.4.0'; gamePath = (Resolve-Path $GamePath).Path; installedBepInEx = $installedLoader; installedAt = (Get-Date).ToString('o') }
$marker | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $bepRoot $MarkerName)
Write-Host ''
Write-Host 'Goose Online installation complete.' -ForegroundColor Green
Write-Host "Game folder: $GamePath"
Write-Host 'Launch the game from Steam, then press F8.'
if (-not $NoPause) { Read-Host 'Press Enter to close' }



