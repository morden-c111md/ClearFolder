$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$out = Join-Path $root 'artifacts'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $root 'src') -Filter '*.cs' | ForEach-Object FullName)
$testSource = Join-Path $root 'tests\BehaviorTests.cs'
$extraTests = @(Get-ChildItem -LiteralPath (Join-Path $root 'tests') -Filter '*.cs' | Where-Object Name -ne 'BehaviorTests.cs' | ForEach-Object FullName)
$wpf = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\WPF'
$wpfRefs = @('UIAutomationClient.dll','UIAutomationTypes.dll','WindowsBase.dll','PresentationCore.dll','PresentationFramework.dll','System.Xaml.dll') | ForEach-Object { if ($_ -eq 'System.Xaml.dll') { '/r:' + (Join-Path (Split-Path $wpf) $_) } else { '/r:' + (Join-Path $wpf $_) } }
& $compiler /nologo /target:exe /platform:x64 /optimize+ /unsafe+ /codepage:65001 /main:ClearFolder.Tests.BehaviorTests "/out:$out\BehaviorTests.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Runtime.Serialization.dll "/r:$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\Microsoft.VisualBasic.dll" @wpfRefs @sources $testSource @extraTests
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
$testData = Join-Path $out ('test-behavior-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
& (Join-Path $out 'BehaviorTests.exe') $testData 2>&1 | Tee-Object -FilePath (Join-Path $out 'behavior-test.txt')
if ($LASTEXITCODE -ne 0) { throw 'Behavior tests failed.' }
Write-Output "Synthetic test data retained: $testData"

