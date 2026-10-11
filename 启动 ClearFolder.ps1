$ErrorActionPreference = 'Stop'
$program = Join-Path $PSScriptRoot 'artifacts\ClearFolder.exe'
if (-not (Test-Path -LiteralPath $program)) { & (Join-Path $PSScriptRoot 'scripts\Build.ps1') }
Start-Process -FilePath $program -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
