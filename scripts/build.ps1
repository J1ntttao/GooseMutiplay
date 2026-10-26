[CmdletBinding()]
param(
    [string]$GamePath = 'D:\Program Files (x86)\Steam\steamapps\common\Untitled Goose Game'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\GooseOnline\GooseOnline.csproj'
if (!(Test-Path $GamePath)) { throw "Game path not found: $GamePath" }
$managed = Join-Path $GamePath 'Untitled_Data\Managed'
foreach ($file in @('Assembly-CSharp.dll','Facepunch.Steamworks.Win64.dll','UnityEngine.dll','UnityEngine.CoreModule.dll')) {
    if (!(Test-Path (Join-Path $managed $file))) { throw "Missing game assembly: $file" }
}
dotnet build $project -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
$dll = Join-Path $root 'dist\GooseOnline.dll'
if (!(Test-Path $dll)) { throw "Build output missing: $dll" }
Write-Host "Built $dll"
