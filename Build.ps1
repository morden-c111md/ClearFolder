param([string]$OutputName = 'ClearFolder.exe')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'System .NET Framework C# compiler not found.' }
$out = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$wpfRefs = @('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll') | ForEach-Object { if ($_ -eq 'System.Xaml.dll') { '/r:' + (Join-Path (Split-Path $wpf) $_) } else { '/r:' + (Join-Path $wpf $_) } }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /unsafe+ /codepage:65001 "/out:$out\$OutputName" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll "/r:$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll" @wpfRefs @sources
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Write-Output "Build passed: $out\$OutputName"


