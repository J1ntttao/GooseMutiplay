[CmdletBinding()]
param([string]$GamePath = 'D:\Program Files (x86)\Steam\steamapps\common\Untitled Goose Game')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -GamePath $GamePath
$stage = Join-Path $root 'dist\GooseOnline-0.4.1'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'payload') | Out-Null
Copy-Item (Join-Path $root 'dist\GooseOnline.dll') (Join-Path $stage 'payload\GooseOnline.dll') -Force
Copy-Item (Join-Path $root 'vendor\BepInEx_win_x64_5.4.23.3.zip') (Join-Path $stage 'payload\BepInEx_win_x64_5.4.23.3.zip') -Force
Copy-Item (Join-Path $root 'scripts\install.ps1') (Join-Path $stage 'install.ps1') -Force
$installScript = Join-Path $stage 'install.ps1'
$pluginBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $root 'dist\GooseOnline.dll')))
$installText = Get-Content -LiteralPath $installScript -Raw
$installText = $installText.Replace("`$EmbeddedPluginBase64 = '__EMBEDDED_PLUGIN_BASE64__'", "`$EmbeddedPluginBase64 = '$pluginBase64'")
Set-Content -LiteralPath $installScript -Value $installText -Encoding UTF8
Copy-Item (Join-Path $root 'scripts\uninstall.ps1') (Join-Path $stage 'uninstall.ps1') -Force
Copy-Item (Join-Path $root 'scripts\Install-GooseOnline.bat') (Join-Path $stage 'Install-GooseOnline.bat') -Force
Copy-Item (Join-Path $root 'scripts\Uninstall-GooseOnline.bat') (Join-Path $stage 'Uninstall-GooseOnline.bat') -Force
Copy-Item (Join-Path $root 'README.md') (Join-Path $stage 'README.md') -Force
$zip = Join-Path $root 'dist\GooseOnline-0.4.1.zip'
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Write-Host "Packaged $zip"

